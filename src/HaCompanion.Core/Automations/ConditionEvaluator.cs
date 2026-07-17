// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;

namespace HaCompanion.Core.Automations;

/// <summary>Evaluates a rule's optional "NUR WENN" gate. Pure — all inputs are injected.</summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// True when EVERY condition holds (implicit AND). An empty list is always true.
    /// </summary>
    /// <param name="entityState">HA entity id → its raw state string (null when unknown).</param>
    /// <param name="pcState">PC-state field → whether it is currently true (null when unknown).</param>
    public static bool AllSatisfied(
        IReadOnlyList<RuleCondition> conditions,
        Func<string, string?> entityState,
        Func<string, bool?> pcState,
        DateTime now)
    {
        foreach (var condition in conditions)
            if (!IsSatisfied(condition, entityState, pcState, now))
                return false;
        return true;
    }

    public static bool IsSatisfied(
        RuleCondition condition,
        Func<string, string?> entityState,
        Func<string, bool?> pcState,
        DateTime now)
    {
        switch (condition.Type)
        {
            case RuleCondition.TypeEntity:
            {
                var isOn = IsOn(entityState(condition.EntityId ?? ""));
                if (isOn is null)
                    return false; // unknown entity — never fire on guesswork
                return condition.WantedState == "on" ? isOn.Value : !isOn.Value;
            }

            case RuleCondition.TypeNumeric:
            {
                if (!TryNumber(entityState(condition.EntityId ?? ""), out var actual) || condition.Number is not { } wanted)
                    return false; // non-numeric / unknown state fails closed
                return condition.Operator switch
                {
                    ">" => actual > wanted,
                    "<" => actual < wanted,
                    ">=" => actual >= wanted,
                    "<=" => actual <= wanted,
                    "==" => actual == wanted,
                    "!=" => actual != wanted,
                    _ => false,
                };
            }

            case RuleCondition.TypePc:
            {
                var value = pcState(condition.PcField ?? "");
                if (value is null)
                    return false;
                return condition.WantedState == "on" ? value.Value : !value.Value;
            }

            case RuleCondition.TypeTime:
            {
                if (!RuleCondition.TryParseTime(condition.FromTime, out var from)
                    || !RuleCondition.TryParseTime(condition.ToTime, out var to))
                    return false;
                // IsBetween handles windows crossing midnight (22:00–06:00); start inclusive,
                // end exclusive; from == to is an empty window → treat as "always".
                var nowTime = TimeOnly.FromDateTime(now);
                return from == to || nowTime.IsBetween(from, to);
            }

            default:
                return false;
        }
    }

    private static bool? IsOn(string? state) => state is null
        ? null
        : string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);

    private static bool TryNumber(string? state, out double value) =>
        double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
