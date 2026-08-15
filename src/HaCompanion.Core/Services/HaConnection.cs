// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Text.Json;
using HaCompanion.Core.Configuration;
using HaCompanion.Core.Models;
using HaCompanion.Core.Rest;
using HaCompanion.Core.WebSocket;
using Microsoft.Extensions.Logging;

namespace HaCompanion.Core.Services;

/// <inheritdoc cref="IHaConnection"/>
public sealed class HaConnection : IHaConnection, IAsyncDisposable
{
    private readonly HaRestClient _rest;
    private readonly HaWebSocketClient _ws;
    private readonly ILogger<HaConnection> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, HaEntityState> _entities = new(StringComparer.Ordinal);

    public HaConnection(HaRestClient rest, HaWebSocketClient ws, ILogger<HaConnection> logger, ILoggerFactory loggerFactory)
    {
        _rest = rest;
        _ws = ws;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _ws.StatusChanged += OnWebSocketStatusChanged;
        _ws.StateChanged += OnWebSocketStateChanged;
        _ws.NotificationReceived += (_, n) => NotificationReceived?.Invoke(this, n);
        _ws.PushNotificationReceived += (_, payload) => PushNotificationReceived?.Invoke(this, payload);
    }

    public HaConnectionStatus Status => _ws.Status;

    public IReadOnlyDictionary<string, HaEntityState> Entities => _entities;

    public event EventHandler<HaConnectionStatus>? StatusChanged;

    public event EventHandler<HaEntityState>? EntityUpdated;

    public event EventHandler<HaNotification>? NotificationReceived;

    public event EventHandler<JsonElement>? PushNotificationReceived;

    public void EnablePushChannel(string? webhookId) => _ws.EnablePushChannel(webhookId);

    public Task ConfirmPushAsync(string confirmId, CancellationToken ct = default) =>
        _ws.ConfirmPushAsync(confirmId, ct);

    public Task<bool> FireEventAsync(string eventType, object? data = null, CancellationToken ct = default) =>
        _rest.FireEventAsync(eventType, data, ct);

    public async Task<ConnectionCheckResult> CheckAsync(HaConnectionSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid)
            throw new ArgumentException("Connection settings are incomplete (base URL and token required).", nameof(settings));

        // Throwaway probe client: validating CANDIDATE settings must not reconfigure the
        // live session's REST client (a failed probe would otherwise break it).
        using var probe = new HaRestClient(_loggerFactory.CreateLogger<HaRestClient>());
        probe.Configure(settings.BaseUri, settings.Token, settings.IgnoreCertificateErrors);
        return await probe.CheckAsync(ct).ConfigureAwait(false);
    }

    public async Task<ConnectionCheckResult> ConnectAsync(HaConnectionSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid)
            throw new ArgumentException("Connection settings are incomplete (base URL and token required).", nameof(settings));

        _rest.Configure(settings.BaseUri, settings.Token, settings.IgnoreCertificateErrors);

        var check = await _rest.CheckAsync(ct).ConfigureAwait(false);
        if (!check.IsOk)
        {
            _logger.LogWarning("Home Assistant REST validation failed for {BaseUrl}: {Reason}",
                settings.BaseUrl, check.Status);
            // A NETWORK-class failure must still start the supervisor, or nothing ever retries:
            // autostart at logon races the VPN/Wi-Fi coming up, the first check hits DnsError,
            // and without a running supervisor even PokeReconnect (the connectivity watcher's
            // "network is back" signal) is a no-op — the app then sits on "Disconnected"
            // forever. The supervisor retries with backoff and, on genuine auth/TLS problems,
            // stops terminally on its own. Only failures the RETRY cannot cure (bad token,
            // certificate rejected) keep the old report-and-stop behavior.
            if (check.Status is not ConnectionCheckStatus.AuthFailed and not ConnectionCheckStatus.TlsError)
                _ws.Start(settings.WebSocketUri, settings.Token, settings.IgnoreCertificateErrors);
            return check;
        }

        // A (re)connect may target a DIFFERENT instance whose entity ids overlap the old one's.
        // Stale entries would then win the refresh's timestamp comparison forever (the other
        // instance's clocks/last_updated are unrelated). Order matters twice here:
        //  - stop the OLD session BEFORE rebuilding, or its receive loop keeps writing the old
        //    instance's states (with fresh timestamps) into the map mid-rebuild;
        //  - fetch BEFORE clearing, so a failed fetch leaves the previous entities visible
        //    instead of blanking every tile.
        _ws.Stop();
        try
        {
            var states = await _rest.GetStatesAsync(ct).ConfigureAwait(false);
            _entities.Clear();
            foreach (var state in states)
            {
                _entities[state.EntityId] = state;
                EntityUpdated?.Invoke(this, state);
            }
            _snapshotIsFresh = true; // skip the redundant re-fetch on the imminent Connected event
        }
        catch (Exception ex)
        {
            // The check above already proved the instance reachable; a failed snapshot (proxy
            // hiccup, api/states timing out on a huge install) must not strand us with NO
            // session — the old one is stopped by now. Start anyway: the supervisor retries
            // with backoff, and the Connected handler re-fetches the snapshot we are missing
            // (_snapshotIsFresh stays false).
            _logger.LogWarning(ex, "Initial state snapshot failed; connecting anyway");
        }
        _ws.Start(settings.WebSocketUri, settings.Token, settings.IgnoreCertificateErrors);
        return check;
    }

    public void Disconnect() => _ws.Stop();

    public void PokeReconnect() => _ws.PokeReconnect();

    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        var states = await _rest.GetStatesAsync(ct).ConfigureAwait(false);
        foreach (var state in states)
        {
            // The snapshot is taken while state_changed events are already flowing, so by the
            // time it arrives an entity may have moved on. Blind-writing it rolled such an
            // entity back in the UI until its next change — compare timestamps and keep the
            // newer one. (Missing timestamps: treat the snapshot as authoritative, as before.)
            if (_entities.TryGetValue(state.EntityId, out var live)
                && live.LastUpdated is { } liveAt && state.LastUpdated is { } snapshotAt
                && liveAt > snapshotAt)
            {
                continue;
            }
            _entities[state.EntityId] = state;
            EntityUpdated?.Invoke(this, state);
        }
    }

    public Task ToggleAsync(string entityId, CancellationToken ct = default) =>
        _rest.CallServiceAsync("homeassistant", "toggle", new { entity_id = entityId }, ct);

    public Task CallServiceAsync(string domain, string service, string entityId, CancellationToken ct = default) =>
        _rest.CallServiceAsync(domain, service, new { entity_id = entityId }, ct);

    public Task CallServiceAsync(string domain, string service, string entityId,
        IReadOnlyDictionary<string, object?> data, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>(data) { ["entity_id"] = entityId };
        return _rest.CallServiceAsync(domain, service, payload, ct);
    }

    public async Task<IReadOnlyList<HaDashboardInfo>> ListDashboardsAsync(CancellationToken ct = default)
    {
        var dashboards = new List<HaDashboardInfo>();

        try
        {
            var result = await _ws.SendCommandAsync("lovelace/dashboards/list", ct: ct).ConfigureAwait(false);
            if (result.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    // Mirror Home Assistant's own sidebar: skip dashboards the user hid
                    // (show_in_sidebar=false). This also drops HA's built-in "lovelace"
                    // default, which it exposes under the SAME localized title as the
                    // user's real home dashboard (e.g. two "Übersicht" entries) — the
                    // source of the reported duplicate.
                    if (item.TryGetProperty("show_in_sidebar", out var vis) &&
                        vis.ValueKind == System.Text.Json.JsonValueKind.False)
                        continue;

                    var urlPath = item.TryGetProperty("url_path", out var u) ? u.GetString() : null;
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title))
                        title = string.IsNullOrEmpty(urlPath) ? "Overview" : urlPath;
                    var icon = item.TryGetProperty("icon", out var i) ? i.GetString() : null;
                    dashboards.Add(new HaDashboardInfo(urlPath, title, icon));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list Lovelace dashboards");
        }

        // Fallback only when the sidebar is empty or the call failed: show the implicit
        // default dashboard at the site root so the picker is never empty.
        if (dashboards.Count == 0)
            dashboards.Add(new HaDashboardInfo(null, "Overview", "mdi:view-dashboard"));

        return dashboards;
    }

    // (The public dashboard-entity-ids wrapper was removed as dead code; this recursive
    // extractor stays — it is pure, unit-tested, and the natural building block if a
    // dashboard-scoped feature returns.)
    internal static void ExtractEntityIds(System.Text.Json.JsonElement el, List<string> ids, HashSet<string> seen)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if ((prop.NameEquals("entity") || prop.NameEquals("entity_id"))
                        && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        AddId(prop.Value.GetString(), ids, seen);
                    else
                        ExtractEntityIds(prop.Value, ids, seen);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        AddId(item.GetString(), ids, seen); // e.g. entities: ["light.kitchen", ...]
                    else
                        ExtractEntityIds(item, ids, seen);
                }
                break;
        }
    }

    private static void AddId(string? id, List<string> ids, HashSet<string> seen)
    {
        if (id is not null && IsEntityId(id) && seen.Add(id))
            ids.Add(id);
    }

    private static bool IsEntityId(string s)
    {
        var dot = s.IndexOf('.');
        if (dot <= 0 || dot == s.Length - 1)
            return false;
        if (s.IndexOf('.', dot + 1) >= 0)
            return false; // exactly one dot
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.'))
                return false;
        return true;
    }

    private volatile bool _snapshotIsFresh;

    private void OnWebSocketStatusChanged(object? sender, HaConnectionStatus status)
    {
        // After a WS-internal reconnect the snapshot is stale (state_changed events were
        // missed during the outage) — reload it so the UI shows CURRENT values, not the
        // last ones seen before the drop. The initial ConnectAsync already loaded fresh.
        if (status == HaConnectionStatus.Connected)
        {
            if (_snapshotIsFresh)
                _snapshotIsFresh = false;
            else
                _ = RefreshAfterReconnectAsync();
        }
        StatusChanged?.Invoke(this, status);
    }

    private async Task RefreshAfterReconnectAsync()
    {
        try
        {
            await RefreshStatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State refresh after reconnect failed");
        }
    }

    private void OnWebSocketStateChanged(object? sender, HaEntityState state)
    {
        _entities[state.EntityId] = state;
        EntityUpdated?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        _ws.StatusChanged -= OnWebSocketStatusChanged;
        _ws.StateChanged -= OnWebSocketStateChanged;
        await _ws.DisposeAsync().ConfigureAwait(false);
        _rest.Dispose();
    }
}
