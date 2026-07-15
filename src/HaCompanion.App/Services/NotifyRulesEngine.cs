// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Models;
using HaCompanion.Core.Notifications;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// The local "benachrichtige mich wenn …" feature: watches the live entity stream and
/// shows a Windows toast when a rule's entity crosses the configured edge — no HA
/// automation needed. Keeps its own previous-state map for real edge detection.
/// </summary>
public interface INotifyRulesEngine
{
    void Initialize();

    /// <summary>Re-read the rule list after it changed (add/remove/enable).</summary>
    void Reload();
}

/// <inheritdoc cref="INotifyRulesEngine"/>
public sealed class NotifyRulesEngine : INotifyRulesEngine
{
    // Anti-flap window, keyed per entity+state: a deliberate on->off pair notifies immediately
    // (different states), only the SAME transition repeated within the window is debounced.
    private const int CooldownMs = 3000;

    private readonly INotifyRulesStore _store;
    private readonly IHaConnection _connection;
    private readonly INotificationService _notifications;
    private readonly LocalizationService _loc;
    private readonly ILogger<NotifyRulesEngine> _logger;

    private readonly Dictionary<string, HaEntityState> _lastStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cooldown = new(StringComparer.Ordinal);
    private IReadOnlyList<NotificationRule> _rules = Array.Empty<NotificationRule>();

    public NotifyRulesEngine(INotifyRulesStore store, IHaConnection connection,
        INotificationService notifications, LocalizationService loc, ILogger<NotifyRulesEngine> logger)
    {
        _store = store;
        _connection = connection;
        _notifications = notifications;
        _loc = loc;
        _logger = logger;
    }

    public void Initialize()
    {
        Reload();
        _connection.EntityUpdated += OnEntityUpdated;
    }

    public void Reload() => _rules = _store.Load();

    private void OnEntityUpdated(object? sender, HaEntityState newState)
    {
        try
        {
            // Track ONLY entities a rule watches — the full state stream is thousands strong.
            var watched = false;
            foreach (var rule in _rules)
            {
                if (!string.Equals(rule.EntityId, newState.EntityId, StringComparison.Ordinal))
                    continue;
                watched = true;

                _lastStates.TryGetValue(newState.EntityId, out var oldState);
                if (!NotificationRuleMatcher.ShouldNotify(rule, oldState, newState))
                    continue;

                var now = Environment.TickCount64;
                var cooldownKey = $"{newState.EntityId}|{newState.State}";
                if (_cooldown.TryGetValue(cooldownKey, out var last) && now - last < CooldownMs)
                    continue;
                _cooldown[cooldownKey] = now;

                _notifications.Show(newState.FriendlyName, StateText(newState));
                _logger.LogInformation("Notify rule fired: {Entity} -> {State}", newState.EntityId, newState.State);
            }
            if (watched)
                _lastStates[newState.EntityId] = newState;
            else if (_lastStates.Count > 0)
                _lastStates.Remove(newState.EntityId); // rule was deleted — stop tracking
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notify rule handling failed");
        }
    }

    /// <summary>"Ein"/"Aus"/"Offen"/"Geschlossen" for binary-ish states, raw value otherwise.</summary>
    private string StateText(HaEntityState state) => state.State.ToLowerInvariant() switch
    {
        "on" => _loc["Osd_On"],
        "off" => _loc["Osd_Off"],
        "open" => _loc["Osd_Opened"],
        "closed" => _loc["Osd_Closed"],
        _ => state.State,
    };
}
