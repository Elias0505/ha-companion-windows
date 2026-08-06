// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaCompanion.Core.Models;
using HaCompanion.Core.Rest;
using Microsoft.Extensions.Logging;

namespace HaCompanion.Core.WebSocket;

/// <summary>
/// Maintains a Home Assistant WebSocket API connection: authenticates,
/// subscribes to <c>state_changed</c> events, supports id-correlated
/// request/response commands and pushes events to subscribers.
/// Automatically reconnects with exponential backoff.
/// </summary>
public sealed class HaWebSocketClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<HaWebSocketClient> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _backoffSkip; // cancelled by PokeReconnect() to end a wait early
    private bool _sessionReachedConnected;

    // Session generation: Start()/Stop() bump it, and every shared-state write from a
    // supervisor (Status, _pending, _backoffSkip) checks it first — the tail of an old
    // session that is still unwinding must never write over the newer session's state
    // (same reason _activeSocket is cleared with a CompareExchange below).
    private int _generation;
    private readonly OutageLogGate _outageGate = new();
    private Task? _supervisor;
    private ClientWebSocket? _activeSocket;
    private Uri _uri = null!;
    private string _token = string.Empty;
    private bool _ignoreCertErrors;
    private int _msgId;
    private int _notificationSubId; // id of the persistent_notification/subscribe command
    private int _pushSubId;         // id of the mobile_app push channel subscription (0 = none)
    private string? _pushWebhookId;
    private readonly object _pushGate = new(); // serializes the subscribe claim (connect vs EnablePushChannel)

    public HaConnectionStatus Status { get; private set; } = HaConnectionStatus.Disconnected;

    public event EventHandler<HaConnectionStatus>? StatusChanged;
    public event EventHandler<HaEntityState>? StateChanged;

    /// <summary>Raised when Home Assistant ADDS a persistent notification.</summary>
    public event EventHandler<HaNotification>? NotificationReceived;

    /// <summary>Raised for every notification pushed over the mobile_app websocket channel
    /// (raw event payload; parse with PushMessageParser).</summary>
    public event EventHandler<JsonElement>? PushNotificationReceived;

    public HaWebSocketClient(ILogger<HaWebSocketClient> logger) => _logger = logger;

    /// <summary>Start (or restart) the supervised connection loop.</summary>
    public void Start(Uri webSocketUri, string token, bool ignoreCertErrors = false)
    {
        Stop();
        _uri = webSocketUri;
        _token = token;
        _ignoreCertErrors = ignoreCertErrors;
        _cts = new CancellationTokenSource();
        var gen = Interlocked.Increment(ref _generation);
        var ct = _cts.Token;
        _supervisor = Task.Run(() => SuperviseAsync(gen, ct));
    }

    /// <summary>
    /// Skip the current reconnect backoff (network came back / machine resumed): a waiting
    /// supervisor retries immediately with a fresh 1s backoff. Safe from any thread; no-op
    /// while connected or stopped.
    /// </summary>
    public void PokeReconnect()
    {
        try { _backoffSkip?.Cancel(); }
        catch (ObjectDisposedException) { /* raced with the wait ending — fine */ }
    }

    /// <summary>
    /// Subscribe the mobile_app push notification channel for this webhook id — on every
    /// (re)connect, and immediately when already connected. Pass null/empty to disable.
    /// </summary>
    public void EnablePushChannel(string? webhookId)
    {
        _pushWebhookId = string.IsNullOrWhiteSpace(webhookId) ? null : webhookId;
        var socket = _activeSocket;
        if (socket is null || _pushWebhookId is null)
            return;
        // Claim the (single) subscription for this connection atomically — the connect handler
        // claims it too, and without this both could subscribe and deliver every toast twice.
        var id = ClaimPushSub();
        if (id != 0)
            _ = SendSubscribeAsync(socket, id, CancellationToken.None);
    }

    /// <summary>Assign this connection's push-sub id if none is live yet; 0 means already claimed.</summary>
    private int ClaimPushSub()
    {
        lock (_pushGate)
            return ClaimPushSubLocked();
    }

    /// <summary>Caller must hold <see cref="_pushGate"/>.</summary>
    private int ClaimPushSubLocked() =>
        _pushWebhookId is not null && _pushSubId == 0 ? _pushSubId = NextId() : 0;

    /// <summary>Acknowledge a pushed notification (the channel is subscribed with support_confirm).</summary>
    public async Task ConfirmPushAsync(string confirmId, CancellationToken ct = default)
    {
        var socket = _activeSocket;
        if (socket is null || _pushWebhookId is null)
            return;
        await SendRawAsync(socket, JsonSerializer.Serialize(new
        {
            id = NextId(),
            type = "mobile_app/push_notification_confirm",
            webhook_id = _pushWebhookId,
            confirm_id = confirmId,
        }, JsonOptions), ct).ConfigureAwait(false);
    }

    private async Task SendSubscribeAsync(ClientWebSocket socket, int id, CancellationToken ct)
    {
        try
        {
            await SendRawAsync(socket, JsonSerializer.Serialize(new
            {
                id,
                type = "mobile_app/push_notification_channel",
                webhook_id = _pushWebhookId,
                support_confirm = true,
            }, JsonOptions), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not subscribe the push notification channel");
        }
    }

    public void Stop()
    {
        if (_cts is null)
            return;
        // Invalidate the running supervisor's generation BEFORE cancelling: its unwinding
        // tail then loses every shared-state write race against this (authoritative) call.
        var gen = Interlocked.Increment(ref _generation);
        try { _cts.Cancel(); }
        catch { /* already disposed */ }
        _cts.Dispose();
        _cts = null;
        FailPending(gen, new OperationCanceledException("Connection stopped."));
        SetStatus(gen, HaConnectionStatus.Disconnected);
    }

    /// <summary>
    /// Send an id-correlated command (e.g. <c>lovelace/dashboards/list</c>) and await its result.
    /// Throws when not connected or when Home Assistant reports an error.
    /// </summary>
    public async Task<JsonElement> SendCommandAsync(
        string type,
        IReadOnlyDictionary<string, object?>? fields = null,
        CancellationToken ct = default)
    {
        var socket = _activeSocket
            ?? throw new InvalidOperationException("Not connected to Home Assistant.");

        var id = NextId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var payload = new JsonObject { ["id"] = id, ["type"] = type };
            if (fields is not null)
                foreach (var (key, value) in fields)
                    payload[key] = value is null ? null : JsonSerializer.SerializeToNode(value, JsonOptions);

            await SendRawAsync(socket, payload.ToJsonString(), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using var reg = timeout.Token.Register(
                () => tcs.TrySetException(new TimeoutException($"Command '{type}' timed out.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SuperviseAsync(int gen, CancellationToken ct)
    {
        var backoffSeconds = 1.0;
        while (!ct.IsCancellationRequested)
        {
            _sessionReachedConnected = false;
            try
            {
                await ConnectAndListenAsync(gen, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A certificate problem doesn't heal itself — classify and stop retrying,
                // exactly like an auth failure (the UI turns it into an actionable hint).
                if (Status != HaConnectionStatus.AuthFailed
                    && HaRestClient.ClassifyException(ex) == ConnectionCheckStatus.TlsError)
                    SetStatus(gen, HaConnectionStatus.TlsError);

                if (_outageGate.OnFailure())
                    _logger.LogWarning(ex, "Home Assistant connection lost");
                else
                    _logger.LogDebug(ex, "Reconnect attempt failed");
            }
            finally
            {
                // _activeSocket is cleared inside ConnectAndListenAsync with a CompareExchange
                // against ITS OWN socket — clearing it here could clobber the socket of a NEWER
                // supervisor when Start() is called while an old session is still unwinding.
                FailPending(gen, new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Session ended."));
            }

            if (ct.IsCancellationRequested)
                break;

            // Auth/TLS failures don't heal themselves — stop retrying and surface the state.
            if (Status is HaConnectionStatus.AuthFailed or HaConnectionStatus.TlsError)
                break;

            // A session that actually reached Connected earns a fresh backoff — real drops end
            // in exceptions, so resetting only on a "clean" return meant the delay ratcheted up
            // permanently (1s -> ... -> 30s) across days of otherwise stable connections.
            if (_sessionReachedConnected)
                backoffSeconds = 1.0;

            SetStatus(gen, HaConnectionStatus.Reconnecting);
            using var skip = new CancellationTokenSource();
            if (gen == Volatile.Read(ref _generation))
                _backoffSkip = skip;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, skip.Token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), wait.Token).ConfigureAwait(false);
                backoffSeconds = Math.Min(30, backoffSeconds * 2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                backoffSeconds = 1.0; // poked: retry right away and start the ladder over
            }
            finally
            {
                // Clear only OUR skip source — a newer session may already own the field.
                Interlocked.CompareExchange(ref _backoffSkip, null, skip);
            }
        }

        if (Status is not HaConnectionStatus.AuthFailed and not HaConnectionStatus.TlsError)
            SetStatus(gen, HaConnectionStatus.Disconnected);
    }

    private async Task ConnectAndListenAsync(int gen, CancellationToken ct)
    {
        SetStatus(gen, HaConnectionStatus.Connecting);

        using var socket = new ClientWebSocket();
#pragma warning disable CA5359 // deliberate user opt-in: "ignore certificate errors" for self-signed HTTPS
        if (_ignoreCertErrors)
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359

        await socket.ConnectAsync(_uri, ct).ConfigureAwait(false);

        SetStatus(gen, HaConnectionStatus.Authenticating);

        // 1) server -> auth_required
        using (await ReceiveJsonAsync(socket, ct).ConfigureAwait(false)) { }

        // 2) client -> auth
        await SendRawAsync(socket,
            JsonSerializer.Serialize(new { type = "auth", access_token = _token }, JsonOptions),
            ct).ConfigureAwait(false);

        // 3) server -> auth_ok / auth_invalid
        using (var authDoc = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false))
        {
            var authType = authDoc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (authType != "auth_ok")
            {
                SetStatus(gen, HaConnectionStatus.AuthFailed);
                throw new InvalidOperationException($"Home Assistant authentication failed (type='{authType}').");
            }
        }

        _activeSocket = socket;
        try
        {
            SetStatus(gen, HaConnectionStatus.Connected);
            _sessionReachedConnected = true;
            if (_outageGate.OnRestored())
                _logger.LogInformation("Home Assistant connection restored");

            // subscribe to state changes
            await SendRawAsync(socket, JsonSerializer.Serialize(new
            {
                id = NextId(),
                type = "subscribe_events",
                event_type = "state_changed",
            }, JsonOptions), ct).ConfigureAwait(false);

            // subscribe to persistent notifications (pushed as added/removed/current events)
            _notificationSubId = NextId();
            await SendRawAsync(socket, JsonSerializer.Serialize(new
            {
                id = _notificationSubId,
                type = "persistent_notification/subscribe",
            }, JsonOptions), ct).ConfigureAwait(false);

            // mobile_app push channel (notify.mobile_app_<device> deliveries + PC commands).
            // Reset first (the previous connection's subscription is dead), then claim a fresh
            // id atomically so a concurrent EnablePushChannel can't subscribe a second time.
            int pushId;
            lock (_pushGate)
            {
                _pushSubId = 0;
                pushId = ClaimPushSubLocked();
            }
            if (pushId != 0)
                await SendSubscribeAsync(socket, pushId, ct).ConfigureAwait(false);

            // receive loop
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var doc = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
                Dispatch(doc);
            }
        }
        finally
        {
            // Only clear the field if it still refers to THIS session's socket.
            Interlocked.CompareExchange(ref _activeSocket, null, socket);
        }
    }

    private void Dispatch(JsonDocument doc)
    {
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        // Surface the push-channel subscribe outcome (sent fire-and-forget, so no pending TCS):
        // HA answers success=false when the registration lacks push_websocket_channel in app_data.
        if (type == "result"
            && root.TryGetProperty("id", out var pushIdEl)
            && pushIdEl.TryGetInt32(out var pushResultId)
            && _pushSubId != 0 && pushResultId == _pushSubId)
        {
            var ok = root.TryGetProperty("success", out var ps) && ps.GetBoolean();
            if (ok)
            {
                _logger.LogInformation("Push notification channel subscribed");
            }
            else
            {
                var msg = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                    ? m.GetString() : "unknown";
                _logger.LogWarning("Push channel subscribe rejected: {Message}", msg);
                _pushSubId = 0; // don't route events to a dead subscription
            }
            return;
        }

        if (type == "result"
            && root.TryGetProperty("id", out var idEl)
            && idEl.TryGetInt32(out var id)
            && _pending.TryRemove(id, out var tcs))
        {
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                tcs.TrySetResult(result);
            }
            else
            {
                var message = root.TryGetProperty("error", out var err)
                              && err.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "unknown error"
                    : "unknown error";
                tcs.TrySetException(new InvalidOperationException($"Home Assistant error: {message}"));
            }
            return;
        }

        if (type == "event")
        {
            var eid = root.TryGetProperty("id", out var evId) && evId.TryGetInt32(out var parsed) ? parsed : -1;
            if (eid == _notificationSubId)
                DispatchNotification(root);
            else if (_pushSubId != 0 && eid == _pushSubId)
                DispatchPush(root);
            else
                DispatchStateChanged(root);
        }
    }

    private void DispatchNotification(JsonElement root)
    {
        // Shape: { id, type:"event", event: { type: "added"|"removed"|"current"|"updated",
        //          notifications: { "<id>": { notification_id, title, message, ... } } } }
        // Only freshly ADDED notifications become toasts — the initial "current" batch
        // would replay old ones at every reconnect.
        try
        {
            if (!root.TryGetProperty("event", out var ev)
                || !ev.TryGetProperty("type", out var evType) || evType.GetString() != "added"
                || !ev.TryGetProperty("notifications", out var items)
                || items.ValueKind != JsonValueKind.Object)
                return;

            foreach (var item in items.EnumerateObject())
            {
                var n = item.Value;
                var id = n.TryGetProperty("notification_id", out var i) ? i.GetString() ?? item.Name : item.Name;
                var title = n.TryGetProperty("title", out var t) ? t.GetString() : null;
                var message = n.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                NotificationReceived?.Invoke(this,
                    new HaNotification(id, string.IsNullOrWhiteSpace(title) ? "Home Assistant" : title!, message));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse persistent notification payload");
        }
    }

    private void DispatchPush(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
                PushNotificationReceived?.Invoke(this, ev.Clone());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not dispatch push notification payload");
        }
    }

    private void DispatchStateChanged(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev))
            return;
        if (!ev.TryGetProperty("event_type", out var et) || et.GetString() != "state_changed")
            return;
        if (!ev.TryGetProperty("data", out var data))
            return;
        if (!data.TryGetProperty("new_state", out var newState) || newState.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var entity = newState.Deserialize<HaEntityState>(JsonOptions);
            if (entity is not null && entity.EntityId.Length > 0)
                StateChanged?.Invoke(this, entity);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse state_changed payload");
        }
    }

    private void FailPending(int gen, Exception exception)
    {
        if (gen != Volatile.Read(ref _generation))
            return; // stale session tail — the commands in _pending belong to a newer session
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(exception);
    }

    private int NextId() => Interlocked.Increment(ref _msgId);

    private void SetStatus(int gen, HaConnectionStatus status)
    {
        if (gen != Volatile.Read(ref _generation))
            return; // stale session tail must not overwrite the newer session's status
        if (Status == status)
            return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private async Task SendRawAsync(ClientWebSocket socket, string json, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Upper bound for one received message. HA's get_states-style payloads reach a few MB
    /// on attribute-heavy installs; 32 MB is an order of magnitude of headroom while still
    /// stopping a broken or hostile endpoint long before the MemoryStream eats all RAM.
    /// Exceeding it throws, which unwinds the session into the normal reconnect path.
    /// </summary>
    internal const int MaxMessageBytes = 32 * 1024 * 1024;

    private static Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct) =>
        ReceiveJsonAsync(socket.ReceiveAsync, ct);

    /// <summary>Testable core of the receive loop (the socket is reduced to its receive call).</summary>
    internal static async Task<JsonDocument> ReceiveJsonAsync(
        Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> receiveAsync,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await receiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Home Assistant closed the WebSocket.");
            if (ms.Length + result.Count > MaxMessageBytes)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                    $"Message exceeds the {MaxMessageBytes / (1024 * 1024)} MB receive limit.");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_supervisor is not null)
        {
            try { await _supervisor.ConfigureAwait(false); }
            catch { /* ignore shutdown errors */ }
        }
        _sendLock.Dispose();
    }
}
