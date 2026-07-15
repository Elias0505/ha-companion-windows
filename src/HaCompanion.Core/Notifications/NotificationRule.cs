// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Models;

namespace HaCompanion.Core.Notifications;

/// <summary>
/// A local notification rule: toast me when this entity changes. Modes:
/// "turned_on" (door opens / light turns on), "turned_off", "any_change"
/// (every state-string change — also sensor values). Persistence contract
/// for notify_rules.json.
/// </summary>
public sealed record NotificationRule(string EntityId, string Mode, bool IsEnabled = true)
{
    public const string TurnedOn = "turned_on";
    public const string TurnedOff = "turned_off";
    public const string AnyChange = "any_change";

    public static readonly IReadOnlyList<string> Modes = new[] { TurnedOn, TurnedOff, AnyChange };

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(EntityId)
        && EntityId.Contains('.')
        && Modes.Contains(Mode, StringComparer.Ordinal);
}

/// <summary>Edge detection for notification rules — only real transitions notify.</summary>
public static class NotificationRuleMatcher
{
    /// <summary>
    /// True when the change from <paramref name="oldState"/> to <paramref name="newState"/>
    /// should raise a toast. A null old state (first sighting after connect) never notifies —
    /// otherwise every app start would replay the whole world as "changes".
    /// </summary>
    public static bool ShouldNotify(NotificationRule rule, HaEntityState? oldState, HaEntityState newState)
    {
        if (!rule.IsEnabled
            || !string.Equals(rule.EntityId, newState.EntityId, StringComparison.Ordinal)
            || oldState is null)
            return false;

        // unavailable/unknown flapping (reboots, zigbee dropouts) must stay silent
        if (oldState.IsUnavailable || newState.IsUnavailable
            || string.IsNullOrEmpty(oldState.State) || string.IsNullOrEmpty(newState.State))
            return false;

        return rule.Mode switch
        {
            NotificationRule.TurnedOn => !oldState.IsOn && newState.IsOn,
            NotificationRule.TurnedOff => oldState.IsOn && !newState.IsOn,
            NotificationRule.AnyChange => !string.Equals(oldState.State, newState.State, StringComparison.Ordinal),
            _ => false,
        };
    }
}
