// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Automations;

/// <summary>Evaluates a rule's optional "NUR WENN" gate. Pure — inputs are injected.</summary>
public static class ConditionEvaluator
{
    /// <param name="entityIsOn">
    /// Looks up whether an HA entity is currently on; null when the entity is unknown
    /// (an unknown entity fails the condition — a rule must not fire on guesswork).
    /// </param>
    public static bool IsSatisfied(RuleCondition? condition, Func<string, bool?> entityIsOn, TimeOnly now)
    {
        if (condition is null)
            return true;

        switch (condition.Type)
        {
            case RuleCondition.TypeEntity:
                var isOn = entityIsOn(condition.EntityId ?? "");
                if (isOn is null)
                    return false;
                return condition.WantedState == "on" ? isOn.Value : !isOn.Value;

            case RuleCondition.TypeTime:
                if (!RuleCondition.TryParseTime(condition.FromTime, out var from)
                    || !RuleCondition.TryParseTime(condition.ToTime, out var to))
                    return false;
                // IsBetween handles windows that cross midnight (22:00–06:00); the start
                // is inclusive, the end exclusive. from == to would be an empty window —
                // treat it as "always" (the user clearly didn't mean 'never').
                return from == to || now.IsBetween(from, to);

            default:
                return false;
        }
    }
}
