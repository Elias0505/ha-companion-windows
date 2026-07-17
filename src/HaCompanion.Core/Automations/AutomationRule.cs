// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json.Serialization;

namespace HaCompanion.Core.Automations;

/// <summary>One "DANN" step of a rule: an entity, an action, and optional service data
/// (e.g. brightness_pct, volume_level, temperature).</summary>
public sealed record RuleAction(string EntityId, string Action,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    [JsonIgnore]
    public string Domain => EntityId.Split('.')[0];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(EntityId)
        && EntityId.Contains('.')
        && AutomationActions.IsAllowed(Domain, Action ?? "");
}

/// <summary>
/// Optional "NUR WENN" gate. Four kinds:
/// <c>entity</c> — an HA entity currently reports on/off (<see cref="WantedState"/> "on"|"off").
/// <c>time</c> — local time is inside <see cref="FromTime"/>–<see cref="ToTime"/> ("HH:mm").
/// <c>numeric</c> — an HA entity's numeric state compares (<see cref="Operator"/>) to <see cref="Number"/>.
/// <c>pc</c> — this PC's live state (<see cref="PcField"/>) is on/off (<see cref="WantedState"/>).
/// </summary>
public sealed record RuleCondition(
    string Type,
    string? EntityId = null,
    string? WantedState = null,
    string? FromTime = null,
    string? ToTime = null,
    string? Operator = null,
    double? Number = null,
    string? PcField = null)
{
    public const string TypeEntity = "entity";
    public const string TypeTime = "time";
    public const string TypeNumeric = "numeric";
    public const string TypePc = "pc";

    public static readonly IReadOnlyList<string> Operators = [">", "<", ">=", "<=", "==", "!="];

    /// <summary>PC-state fields usable in a <c>pc</c> condition (map to the live snapshot).</summary>
    public static readonly IReadOnlyList<string> PcFields =
        ["locked", "display_on", "fullscreen", "mic", "cam", "audio", "idle"];

    public bool IsValid() => Type switch
    {
        TypeEntity => HasEntity && WantedState is "on" or "off",
        TypeTime => TryParseTime(FromTime, out _) && TryParseTime(ToTime, out _),
        TypeNumeric => HasEntity && Operator is not null && Operators.Contains(Operator) && Number.HasValue,
        TypePc => PcField is not null && PcFields.Contains(PcField) && WantedState is "on" or "off",
        _ => false,
    };

    private bool HasEntity => !string.IsNullOrWhiteSpace(EntityId) && EntityId!.Contains('.');

    internal static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

/// <summary>
/// A Windows automation: WHEN a windows trigger fires (optionally gated by one or more
/// AND-combined conditions), run one or more HA actions. This shape is the automations.json
/// persistence contract. <see cref="Condition"/> is the legacy single-condition field kept
/// only so old files still deserialize; <see cref="EffectiveConditions"/> is the canonical set.
/// </summary>
public sealed record AutomationRule(
    string Trigger,
    string? Param,
    IReadOnlyList<RuleAction> Actions,
    RuleCondition? Condition = null,
    bool IsEnabled = true,
    IReadOnlyList<RuleCondition>? Conditions = null,
    string? Id = null,
    string? Name = null)
{
    /// <summary>All conditions (AND). Migrates the legacy single <see cref="Condition"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<RuleCondition> EffectiveConditions =>
        Conditions is { Count: > 0 } ? Conditions
        : Condition is not null ? [Condition]
        : [];

    /// <summary>Parsed idle threshold (minutes) for idle triggers; null otherwise/invalid.</summary>
    [JsonIgnore]
    public int? IdleMinutes =>
        WindowsTriggers.TryParse(Trigger, out var t)
        && WindowsTriggers.ParamKind(t) == TriggerParamKind.Minutes
        && int.TryParse(Param, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
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
            TriggerParamKind.Schedule => ScheduleSpec.TryParse(Param, out _),
            _ => true,
        };
        return paramOk
               && Actions is { Count: > 0 }
               && Actions.All(a => a is not null && a.IsValid())
               && EffectiveConditions.All(c => c is not null && c.IsValid());
    }
}
