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

    // ----- new fields: Id, Name, Conditions[], action Data -----

    [Fact]
    public void Json_round_trip_preserves_id_name_conditions_and_action_data()
    {
        var rule = new AutomationRule(
            "lock", null,
            new[] { new RuleAction("light.buero", AutomationActions.TurnOn,
                new Dictionary<string, object?> { ["brightness_pct"] = 30 }) },
            Conditions: new[]
            {
                new RuleCondition(RuleCondition.TypePc, PcField: "locked", WantedState: "on"),
                new RuleCondition(RuleCondition.TypeNumeric, "sensor.temp", Operator: "<", Number: 18),
            },
            Id: "abc123", Name: "Bei Dunkelheit");

        var json = JsonSerializer.Serialize(rule);
        var back = JsonSerializer.Deserialize<AutomationRule>(json);

        Assert.Equal("abc123", back!.Id);
        Assert.Equal("Bei Dunkelheit", back.Name);
        Assert.Equal(2, back.EffectiveConditions.Count);
        Assert.Equal("locked", back.EffectiveConditions[0].PcField);
        Assert.Equal("<", back.EffectiveConditions[1].Operator);
        Assert.Equal(18, back.EffectiveConditions[1].Number);
        Assert.True(back.IsValid());
    }

    [Fact]
    public void Json_round_trip_preserves_action_data_payload()
    {
        // The Data dictionary is the automation feature's real cargo (brightness, colour,
        // hand-added extras) — the earlier round-trip tests never actually inspected it.
        var rule = new AutomationRule(
            "lock", null,
            new[] { new RuleAction("light.buero", AutomationActions.TurnOn,
                new Dictionary<string, object?>
                {
                    ["brightness_pct"] = 60,
                    ["rgb_color"] = new[] { 255, 0, 0 },
                    ["transition"] = 2, // a foreign, hand-added key must survive too
                }) });

        var json = JsonSerializer.Serialize(rule);
        var back = JsonSerializer.Deserialize<AutomationRule>(json);

        var data = back!.Actions[0].Data!;
        Assert.Equal(60, ((JsonElement)data["brightness_pct"]!).GetInt32());
        Assert.Equal(new[] { 255, 0, 0 },
            ((JsonElement)data["rgb_color"]!).EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(2, ((JsonElement)data["transition"]!).GetInt32());
    }

    [Fact]
    public void Legacy_single_condition_migrates_into_effective_conditions()
    {
        // Old files stored a single "Condition"; EffectiveConditions must fold it in.
        var legacy = new AutomationRule("lock", null, new[] { LightOff },
            Condition: new RuleCondition(RuleCondition.TypeTime, FromTime: "22:00", ToTime: "06:00"));
        Assert.Single(legacy.EffectiveConditions);
        Assert.Equal(RuleCondition.TypeTime, legacy.EffectiveConditions[0].Type);
    }

    [Theory]
    [InlineData("sensor.temp", ">", 5.0, true)]
    [InlineData("sensor.temp", "bogus", 5.0, false)]  // unknown operator
    [InlineData("sensor.temp", ">", null, false)]     // missing number
    [InlineData("nodot", ">", 5.0, false)]            // entity needs a domain
    public void Numeric_condition_validity(string entity, string op, double? number, bool expected)
    {
        var c = new RuleCondition(RuleCondition.TypeNumeric, entity, Operator: op, Number: number);
        Assert.Equal(expected, c.IsValid());
    }

    [Theory]
    [InlineData("locked", "on", true)]
    [InlineData("fullscreen", "off", true)]
    [InlineData("locked", "maybe", false)]  // wanted state must be on/off
    [InlineData("weird", "on", false)]      // unknown pc field
    public void Pc_condition_validity(string field, string wanted, bool expected)
    {
        var c = new RuleCondition(RuleCondition.TypePc, PcField: field, WantedState: wanted);
        Assert.Equal(expected, c.IsValid());
    }

    [Fact]
    public void Schedule_rule_requires_a_valid_schedule_param()
    {
        Assert.True(new AutomationRule("schedule", "07:00;12345", new[] { LightOff }).IsValid());
        Assert.False(new AutomationRule("schedule", "nonsense", new[] { LightOff }).IsValid());
        Assert.False(new AutomationRule("schedule", null, new[] { LightOff }).IsValid());
    }
}
