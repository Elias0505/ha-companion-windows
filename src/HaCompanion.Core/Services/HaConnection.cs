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
    }

    public HaConnectionStatus Status => _ws.Status;

    public IReadOnlyDictionary<string, HaEntityState> Entities => _entities;

    public event EventHandler<HaConnectionStatus>? StatusChanged;

    public event EventHandler<HaEntityState>? EntityUpdated;

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
