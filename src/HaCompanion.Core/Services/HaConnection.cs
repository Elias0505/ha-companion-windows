// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, HaEntityState> _entities = new(StringComparer.Ordinal);

    public HaConnection(HaRestClient rest, HaWebSocketClient ws, ILogger<HaConnection> logger)
    {
        _rest = rest;
        _ws = ws;
        _logger = logger;
        _ws.StatusChanged += OnWebSocketStatusChanged;
        _ws.StateChanged += OnWebSocketStateChanged;
        _ws.NotificationReceived += (_, n) => NotificationReceived?.Invoke(this, n);
    }

    public HaConnectionStatus Status => _ws.Status;

    public IReadOnlyDictionary<string, HaEntityState> Entities => _entities;

    public event EventHandler<HaConnectionStatus>? StatusChanged;

    public event EventHandler<HaEntityState>? EntityUpdated;

    public event EventHandler<HaNotification>? NotificationReceived;

    public async Task<bool> ConnectAsync(HaConnectionSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid)
            throw new ArgumentException("Connection settings are incomplete (base URL and token required).", nameof(settings));

        _rest.Configure(settings.BaseUri, settings.Token, settings.IgnoreCertificateErrors);

        if (!await _rest.ValidateAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Home Assistant REST validation failed for {BaseUrl}", settings.BaseUrl);
            return false;
        }

        await RefreshStatesAsync(ct).ConfigureAwait(false);
        _ws.Start(settings.WebSocketUri, settings.Token, settings.IgnoreCertificateErrors);
        return true;
    }

    public void Disconnect() => _ws.Stop();

    public void PokeReconnect() => _ws.PokeReconnect();

    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        var states = await _rest.GetStatesAsync(ct).ConfigureAwait(false);
        foreach (var state in states)
        {
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

    public async Task<IReadOnlyList<string>> GetDashboardEntityIdsAsync(string? urlPath, CancellationToken ct = default)
    {
        var fields = string.IsNullOrEmpty(urlPath)
            ? null
            : new Dictionary<string, object?> { ["url_path"] = urlPath };

        var config = await _ws.SendCommandAsync("lovelace/config", fields, ct).ConfigureAwait(false);

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ExtractEntityIds(config, ids, seen);
        return ids;
    }

    private static void ExtractEntityIds(System.Text.Json.JsonElement el, List<string> ids, HashSet<string> seen)
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

    private void OnWebSocketStatusChanged(object? sender, HaConnectionStatus status) =>
        StatusChanged?.Invoke(this, status);

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
