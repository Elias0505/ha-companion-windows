// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.Configuration;
using HaCompanion.Core.Models;

namespace HaCompanion.Core.Services;

/// <summary>
/// High-level facade over the Home Assistant REST + WebSocket APIs.
/// Holds the current entity snapshot and raises events as states change.
/// </summary>
public interface IHaConnection
{
    HaConnectionStatus Status { get; }

    /// <summary>The latest known state of every entity, keyed by entity id.</summary>
    IReadOnlyDictionary<string, HaEntityState> Entities { get; }

    event EventHandler<HaConnectionStatus>? StatusChanged;

    /// <summary>Raised whenever an entity's state is (re)loaded or changes.</summary>
    event EventHandler<HaEntityState>? EntityUpdated;

    /// <summary>Raised when Home Assistant adds a persistent notification.</summary>
    event EventHandler<HaNotification>? NotificationReceived;

    /// <summary>Raised for every mobile_app websocket push (raw payload — PushMessageParser).</summary>
    event EventHandler<JsonElement>? PushNotificationReceived;

    /// <summary>Subscribe the mobile_app push channel (now and on every reconnect).</summary>
    void EnablePushChannel(string? webhookId);

    /// <summary>Acknowledge a pushed notification carrying a confirm id.</summary>
    Task ConfirmPushAsync(string confirmId, CancellationToken ct = default);

    /// <summary>Fire a custom event on the HA event bus (notification action callbacks).</summary>
    Task<bool> FireEventAsync(string eventType, object? data = null, CancellationToken ct = default);

    /// <summary>
    /// Probe CANDIDATE settings against the server without persisting anything and without
    /// touching the live session (test-before-configure). Returns the classified outcome.
    /// </summary>
    Task<Rest.ConnectionCheckResult> CheckAsync(HaConnectionSettings settings, CancellationToken ct = default);

    /// <summary>Validate credentials, load the initial snapshot and start the live feed.</summary>
    Task<Rest.ConnectionCheckResult> ConnectAsync(HaConnectionSettings settings, CancellationToken ct = default);

    void Disconnect();

    /// <summary>Skip the current reconnect backoff (e.g. network restored / resume from sleep).</summary>
    void PokeReconnect();

    Task RefreshStatesAsync(CancellationToken ct = default);

    /// <summary>Toggle an entity (works for lights, switches, fans, input_booleans, ...).</summary>
    Task ToggleAsync(string entityId, CancellationToken ct = default);

    Task CallServiceAsync(string domain, string service, string entityId, CancellationToken ct = default);

    /// <summary>Call a service with extra data fields (merged with entity_id), e.g. brightness_pct.</summary>
    Task CallServiceAsync(string domain, string service, string entityId,
        IReadOnlyDictionary<string, object?> data, CancellationToken ct = default);

    /// <summary>
    /// List the Lovelace dashboards (always includes the default dashboard first).
    /// Requires an active WebSocket connection.
    /// </summary>
    Task<IReadOnlyList<HaDashboardInfo>> ListDashboardsAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch a dashboard's Lovelace config and return the entity ids it references
    /// (recursively across views/sections/cards), in first-seen order.
    /// </summary>
    Task<IReadOnlyList<string>> GetDashboardEntityIdsAsync(string? urlPath, CancellationToken ct = default);
}
