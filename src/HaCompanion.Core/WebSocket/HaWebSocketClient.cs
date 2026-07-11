// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.WebSockets;
using System.Text.Json;
using HaCompanion.Core.Models;
using Microsoft.Extensions.Logging;

namespace HaCompanion.Core.WebSocket;

/// <summary>
/// Maintains a Home Assistant WebSocket API connection: authenticates,
/// subscribes to <c>state_changed</c> events and pushes them to subscribers.
/// Automatically reconnects with exponential backoff.
/// </summary>
public sealed class HaWebSocketClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<HaWebSocketClient> _logger;

    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private Uri _uri = null!;
    private string _token = string.Empty;
    private bool _ignoreCertErrors;
    private int _msgId;

    public HaConnectionStatus Status { get; private set; } = HaConnectionStatus.Disconnected;

    public event EventHandler<HaConnectionStatus>? StatusChanged;
    public event EventHandler<HaEntityState>? StateChanged;

    public HaWebSocketClient(ILogger<HaWebSocketClient> logger) => _logger = logger;

    /// <summary>Start (or restart) the supervised connection loop.</summary>
    public void Start(Uri webSocketUri, string token, bool ignoreCertErrors = false)
    {
        Stop();
        _uri = webSocketUri;
        _token = token;
        _ignoreCertErrors = ignoreCertErrors;
        _cts = new CancellationTokenSource();
        _supervisor = Task.Run(() => SuperviseAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts is null)
            return;
        try { _cts.Cancel(); }
        catch { /* already disposed */ }
        _cts.Dispose();
        _cts = null;
        SetStatus(HaConnectionStatus.Disconnected);
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var backoffSeconds = 1.0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(ct).ConfigureAwait(false);
                backoffSeconds = 1.0; // clean session ended; reset backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home Assistant WebSocket session ended; will reconnect");
            }

            if (ct.IsCancellationRequested)
                break;

            SetStatus(HaConnectionStatus.Reconnecting);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoffSeconds = Math.Min(30, backoffSeconds * 2);
        }

        SetStatus(HaConnectionStatus.Disconnected);
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        SetStatus(HaConnectionStatus.Connecting);

        using var socket = new ClientWebSocket();
        if (_ignoreCertErrors)
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        await socket.ConnectAsync(_uri, ct).ConfigureAwait(false);

        SetStatus(HaConnectionStatus.Authenticating);

        // 1) server -> auth_required
        using (await ReceiveJsonAsync(socket, ct).ConfigureAwait(false)) { }

        // 2) client -> auth
        await SendAsync(socket, new { type = "auth", access_token = _token }, ct).ConfigureAwait(false);

        // 3) server -> auth_ok / auth_invalid
        using (var authDoc = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false))
        {
            var authType = authDoc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (authType != "auth_ok")
            {
                SetStatus(HaConnectionStatus.AuthFailed);
                throw new InvalidOperationException($"Home Assistant authentication failed (type='{authType}').");
            }
        }

        SetStatus(HaConnectionStatus.Connected);

        // subscribe to state changes
        await SendAsync(socket, new
        {
            id = NextId(),
            type = "subscribe_events",
            event_type = "state_changed",
        }, ct).ConfigureAwait(false);

        // receive loop
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var doc = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
            DispatchStateChanged(doc);
        }
    }

    private void DispatchStateChanged(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "event")
            return;
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

    private int NextId() => Interlocked.Increment(ref _msgId);

    private void SetStatus(HaConnectionStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Home Assistant closed the WebSocket.");
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
    }
}
