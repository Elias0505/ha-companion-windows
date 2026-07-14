// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Automations;

/// <summary>One "DANN" step of a rule: an entity plus an explicit action.</summary>
public sealed record RuleAction(string EntityId, string Action)
{
    public string Domain => EntityId.Split('.')[0];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(EntityId)
        && EntityId.Contains('.')
        && AutomationActions.IsAllowed(Domain, Action ?? "");
}

/// <summary>
/// Optional "NUR WENN" gate of a rule. Two kinds:
/// Type "entity": an HA entity currently reports on/off (<see cref="WantedState"/> "on"|"off").
/// Type "time": local time is inside <see cref="FromTime"/>–<see cref="ToTime"/> ("HH:mm",
/// windows crossing midnight like 22:00–06:00 are allowed).
/// </summary>
public sealed record RuleCondition(
    string Type,
    string? EntityId = null,
    string? WantedState = null,
    string? FromTime = null,
    string? ToTime = null)
{
    public const string TypeEntity = "entity";
    public const string TypeTime = "time";

    public bool IsValid() => Type switch
    {
        TypeEntity => !string.IsNullOrWhiteSpace(EntityId)
                      && EntityId.Contains('.')
                      && WantedState is "on" or "off",
        TypeTime => TryParseTime(FromTime, out _) && TryParseTime(ToTime, out _),
        _ => false,
    };

    internal static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", out time);
}

/// <summary>
/// A Windows automation: WHEN a windows trigger fires (optionally gated by a condition),
/// run one or more HA actions. This shape is the automations.json persistence contract.
/// </summary>
public sealed record AutomationRule(
    string Trigger,
    string? Param,
    IReadOnlyList<RuleAction> Actions,
    RuleCondition? Condition = null,
    bool IsEnabled = true)
{
    /// <summary>Parsed idle threshold (minutes) for idle triggers; null otherwise/invalid.</summary>
    public int? IdleMinutes =>
        WindowsTriggers.TryParse(Trigger, out var t)
        && WindowsTriggers.ParamKind(t) == TriggerParamKind.Minutes
        && int.TryParse(Param, out var minutes)
        && minutes is >= 1 and <= 720
            ? minutes
            : null;

    public bool IsValid()
    {
        if (!WindowsTriggers.TryParse(Trigger, out var trigger))
            return false;
        var paramOk = WindowsTriggers.ParamKind(trigger) switch
        {
            TriggerParamKind.Minutes => IdleMinutes is not null,
            TriggerParamKind.ProcessName => !string.IsNullOrWhiteSpace(Param),
            _ => true,
        };
        return paramOk
               && Actions is { Count: > 0 }
               && Actions.All(a => a is not null && a.IsValid())
               && (Condition is null || Condition.IsValid());
    }
}
