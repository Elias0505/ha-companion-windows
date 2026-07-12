// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.NetworkInformation;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HaCompanion.App.Services;

/// <summary>Nudges the connection to retry immediately when connectivity plausibly returned.</summary>
public interface IConnectivityWatcher
{
    /// <summary>Subscribe to network-change and resume-from-sleep events (call once at startup).</summary>
    void Initialize();
}

/// <inheritdoc cref="IConnectivityWatcher"/>
/// <remarks>
/// Without this, waking the machine or switching networks left the app waiting out the
/// full reconnect backoff (up to 30s of a seemingly dead panel). Events are debounced —
/// Windows fires several address/availability changes for a single real transition.
/// </remarks>
public sealed class ConnectivityWatcher : IConnectivityWatcher
{
    private const int DebounceMs = 3000;

    private readonly IHaConnection _connection;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ConnectivityWatcher> _logger;
    private long _lastPokeMs;

    public ConnectivityWatcher(IHaConnection connection, ISettingsStore settingsStore, ILogger<ConnectivityWatcher> logger)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public void Initialize()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable)
                Poke("network available");
        };
        NetworkChange.NetworkAddressChanged += (_, _) => Poke("network address changed");
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume)
                Poke("resume from sleep");
        };
    }

    private void Poke(string reason)
    {
        var now = Environment.TickCount64;
        if (now - _lastPokeMs < DebounceMs)
            return;
        _lastPokeMs = now;

        if (!_settingsStore.Load().HasConnection)
            return;

        _logger.LogInformation("Connectivity event ({Reason}) — skipping reconnect backoff", reason);
        _connection.PokeReconnect();
    }
}
