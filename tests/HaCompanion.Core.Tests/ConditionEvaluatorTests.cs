// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class ConditionEvaluatorTests
{
    private static readonly Func<string, string?> NoEntities = _ => null;
    private static readonly Func<string, bool?> NoPc = _ => null;
    private static readonly DateTime Noon = new(2026, 7, 16, 12, 0, 0);

    private static Func<string, string?> Entity(string id, string state) =>
        e => e == id ? state : null;

    private static bool One(RuleCondition c, Func<string, string?> ent, Func<string, bool?> pc, DateTime now) =>
        ConditionEvaluator.IsSatisfied(c, ent, pc, now);

    // ----- entity on/off -----

    [Theory]
    [InlineData("on", "on", true)]
    [InlineData("on", "off", false)]
    [InlineData("off", "off", true)]
    [InlineData("off", "on", false)]
    public void Entity_condition_compares_the_wanted_state(string wanted, string actual, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypeEntity, "light.flur", wanted);
        Assert.Equal(expected, One(cond, Entity("light.flur", actual), NoPc, Noon));
    }

    [Fact]
    public void Unknown_entity_fails_the_condition() =>
        Assert.False(One(new RuleCondition(RuleCondition.TypeEntity, "light.x", "on"), NoEntities, NoPc, Noon));

    // ----- numeric -----

    [Theory]
    [InlineData(">", 18.0, "20", true)]
    [InlineData(">", 18.0, "18", false)]
    [InlineData("<", 18.0, "17.5", true)]
    [InlineData(">=", 18.0, "18", true)]
    [InlineData("<=", 18.0, "18", true)]
    [InlineData("==", 18.0, "18.0", true)]
    [InlineData("!=", 18.0, "19", true)]
    public void Numeric_condition_applies_the_operator(string op, double wanted, string state, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypeNumeric, "sensor.temp", Operator: op, Number: wanted);
        Assert.Equal(expected, One(cond, Entity("sensor.temp", state), NoPc, Noon));
    }

    [Theory]
    [InlineData("unknown")]  // non-numeric state
    [InlineData("")]
    public void Numeric_condition_fails_closed_on_non_numbers(string state)
    {
        var cond = new RuleCondition(RuleCondition.TypeNumeric, "sensor.temp", Operator: "<", Number: 18);
        Assert.False(One(cond, Entity("sensor.temp", state), NoPc, Noon));
    }

    // ----- pc-state -----

    [Theory]
    [InlineData("on", true, true)]
    [InlineData("off", true, false)]
    [InlineData("off", false, true)]
    public void Pc_condition_reads_the_live_snapshot(string wanted, bool actual, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypePc, PcField: "locked", WantedState: wanted);
        Assert.Equal(expected, One(cond, NoEntities, f => f == "locked" ? actual : null, Noon));
    }

    [Fact]
    public void Pc_condition_fails_closed_when_the_probe_is_unavailable() =>
        Assert.False(One(new RuleCondition(RuleCondition.TypePc, PcField: "audio", WantedState: "on"),
            NoEntities, _ => null, Noon));

    // ----- time -----

    [Theory]
    [InlineData(8, 12, true)]
    [InlineData(8, 7, false)]
    [InlineData(8, 17, false)] // end exclusive (window 08:00–17:00)
    public void Time_window(int _, int hour, bool expected)
    {
        var cond = new RuleCondition(RuleCondition.TypeTime, FromTime: "08:00", ToTime: "17:00");
        Assert.Equal(expected, One(cond, NoEntities, NoPc, new DateTime(2026, 7, 16, hour, 0, 0)));
    }

    [Fact]
    public void Unknown_condition_type_fails_closed() =>
        Assert.False(One(new RuleCondition("moon_phase"), NoEntities, NoPc, Noon));

    // ----- AND across the whole list -----

    [Fact]
    public void Empty_list_is_always_satisfied() =>
        Assert.True(ConditionEvaluator.AllSatisfied([], NoEntities, NoPc, Noon));

    [Fact]
    public void All_conditions_must_hold()
    {
        var conditions = new[]
        {
            new RuleCondition(RuleCondition.TypePc, PcField: "locked", WantedState: "on"),
            new RuleCondition(RuleCondition.TypeNumeric, "sensor.temp", Operator: "<", Number: 18),
        };
        Func<string, string?> ent = Entity("sensor.temp", "16");
        Assert.True(ConditionEvaluator.AllSatisfied(conditions, ent, f => f == "locked", Noon)); // both true
        Assert.False(ConditionEvaluator.AllSatisfied(conditions, ent, f => f == "locked" ? false : (bool?)null, Noon)); // pc false
        Assert.False(ConditionEvaluator.AllSatisfied(conditions, Entity("sensor.temp", "20"), f => f == "locked", Noon)); // numeric false
    }
}
