// SPDX-License-Identifier: AGPL-3.0-only
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

    /// <summary>Validate credentials, load the initial snapshot and start the live feed.</summary>
    Task<bool> ConnectAsync(HaConnectionSettings settings, CancellationToken ct = default);

    void Disconnect();

    Task RefreshStatesAsync(CancellationToken ct = default);

    /// <summary>Toggle an entity (works for lights, switches, fans, input_booleans, ...).</summary>
    Task ToggleAsync(string entityId, CancellationToken ct = default);

    Task CallServiceAsync(string domain, string service, string entityId, CancellationToken ct = default);
}
