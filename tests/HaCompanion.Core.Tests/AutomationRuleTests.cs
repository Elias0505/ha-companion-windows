// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class AutomationRuleTests
{
    private static RuleAction LightOff => new("light.buero", AutomationActions.TurnOff);

    [Fact]
    public void Simple_rule_is_valid()
    {
        var rule = new AutomationRule("lock", null, new[] { LightOff });
        Assert.True(rule.IsValid());
    }

    [Fact]
    public void Multi_action_rule_is_valid()
    {
        var rule = new AutomationRule("lock", null, new[]
        {
            LightOff,
            new RuleAction("climate.wohnzimmer", AutomationActions.TurnOff),
            new RuleAction("scene.feierabend", AutomationActions.Run),
        });
        Assert.True(rule.IsValid());
    }

    [Fact]
    public void Rule_without_actions_is_invalid()
    {
        Assert.False(new AutomationRule("lock", null, Array.Empty<RuleAction>()).IsValid());
    }

    [Fact]
    public void Unknown_trigger_is_invalid()
    {
        Assert.False(new AutomationRule("teleport", null, new[] { LightOff }).IsValid());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("0", false)]
    [InlineData("721", false)]
    [InlineData("1", true)]
    [InlineData("15", true)]
    [InlineData("720", true)]
    public void Idle_trigger_needs_minutes_in_range(string? param, bool expected)
    {
        var rule = new AutomationRule("idle_start", param, new[] { LightOff });
        Assert.Equal(expected, rule.IsValid());
        Assert.Equal(expected, rule.IdleMinutes is not null);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("powerpnt", true)]
    public void App_trigger_needs_a_process_name(string? param, bool expected) =>
        Assert.Equal(expected, new AutomationRule("app_start", param, new[] { LightOff }).IsValid());

    [Fact]
    public void Action_must_be_allowed_for_the_domain()
    {
        // scripts can only "run", never "turn_off"
        var rule = new AutomationRule("lock", null, new[] { new RuleAction("script.klima_aus", AutomationActions.TurnOff) });
        Assert.False(rule.IsValid());
    }

    [Fact]
    public void Entity_without_domain_is_invalid()
    {
        Assert.False(new AutomationRule("lock", null, new[] { new RuleAction("nodomain", AutomationActions.Toggle) }).IsValid());
    }

    [Theory]
    [InlineData("entity", "light.flur", "on", null, null, true)]
    [InlineData("entity", "light.flur", "weird", null, null, false)]
    [InlineData("entity", null, "on", null, null, false)]
    [InlineData("time", null, null, "22:00", "06:00", true)]
    [InlineData("time", null, null, "22:00", null, false)]
    [InlineData("time", null, null, "25:00", "06:00", false)]
    [InlineData("moon_phase", null, null, null, null, false)]
    public void Condition_consistency_gates_validity(
        string type, string? entityId, string? wanted, string? from, string? to, bool expected)
    {
        var rule = new AutomationRule("lock", null, new[] { LightOff },
            new RuleCondition(type, entityId, wanted, from, to));
        Assert.Equal(expected, rule.IsValid());
    }

    [Fact]
    public void Json_round_trip_preserves_the_full_shape()
    {
        // Persistence contract for automations.json — a change that breaks this
        // silently drops the user's rules on the next load.
        var rule = new AutomationRule(
            "app_start", "powerpnt",
            new[] { LightOff, new RuleAction("scene.gaming", AutomationActions.Run) },
            new RuleCondition(RuleCondition.TypeTime, FromTime: "22:00", ToTime: "06:00"),
            IsEnabled: false);

        var json = JsonSerializer.Serialize(rule);
        var back = JsonSerializer.Deserialize<AutomationRule>(json);

        Assert.NotNull(back);
        Assert.Equal(rule.Trigger, back!.Trigger);
        Assert.Equal(rule.Param, back.Param);
        Assert.Equal(rule.Condition, back.Condition);
        Assert.Equal(rule.IsEnabled, back.IsEnabled);
        Assert.Equal(rule.Actions.ToList(), back.Actions.ToList());
        Assert.True(back.IsValid() == rule.IsValid());
    }
}
