// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class ConditionEvaluatorTests
{
    private static readonly Func<string, bool?> NoEntities = _ => null;

    private static Func<string, bool?> Entity(string id, bool isOn) =>
        e => e == id ? isOn : null;

    [Fact]
    public void Null_condition_is_always_satisfied() =>
        Assert.True(ConditionEvaluator.IsSatisfied(null, NoEntities, new TimeOnly(12, 0)));

    [Theory]
    [InlineData("on", true, true)]
    [InlineData("on", false, false)]
    [InlineData("off", true, false)]
    [InlineData("off", false, true)]
    public void Entity_condition_compares_the_wanted_state(string wanted, bool actualOn, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypeEntity, "light.flur", wanted);
        Assert.Equal(expected, ConditionEvaluator.IsSatisfied(cond, Entity("light.flur", actualOn), new TimeOnly(12, 0)));
    }

    [Fact]
    public void Unknown_entity_fails_the_condition()
    {
        var cond = new RuleCondition(RuleCondition.TypeEntity, "light.gibtsnicht", "on");
        Assert.False(ConditionEvaluator.IsSatisfied(cond, NoEntities, new TimeOnly(12, 0)));
    }

    [Theory]
    [InlineData("08:00", "17:00", 12, 0, true)]
    [InlineData("08:00", "17:00", 7, 59, false)]
    [InlineData("08:00", "17:00", 17, 0, false)] // end exclusive
    [InlineData("22:00", "06:00", 23, 30, true)] // crosses midnight
    [InlineData("22:00", "06:00", 3, 0, true)]
    [InlineData("22:00", "06:00", 12, 0, false)]
    [InlineData("22:00", "06:00", 22, 0, true)]  // start inclusive
    public void Time_window_handles_midnight_crossing(string from, string to, int hour, int minute, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypeTime, FromTime: from, ToTime: to);
        Assert.Equal(expected, ConditionEvaluator.IsSatisfied(cond, NoEntities, new TimeOnly(hour, minute)));
    }

    [Fact]
    public void Equal_from_and_to_means_always()
    {
        var cond = new RuleCondition(RuleCondition.TypeTime, FromTime: "09:00", ToTime: "09:00");
        Assert.True(ConditionEvaluator.IsSatisfied(cond, NoEntities, new TimeOnly(9, 0)));
        Assert.True(ConditionEvaluator.IsSatisfied(cond, NoEntities, new TimeOnly(21, 0)));
    }

    [Fact]
    public void Unknown_condition_type_fails_closed() =>
        Assert.False(ConditionEvaluator.IsSatisfied(
            new RuleCondition("moon_phase"), NoEntities, new TimeOnly(12, 0)));
}
