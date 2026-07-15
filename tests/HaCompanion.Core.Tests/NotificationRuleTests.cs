// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.Models;
using HaCompanion.Core.Notifications;
using Xunit;

namespace HaCompanion.Core.Tests;

public class NotificationRuleTests
{
    private static HaEntityState State(string entityId, string state) =>
        new() { EntityId = entityId, State = state };

    [Theory]
    [InlineData("binary_sensor.haustuer", "turned_on", true)]
    [InlineData("light.flur", "turned_off", true)]
    [InlineData("sensor.temp", "any_change", true)]
    [InlineData("light.flur", "explodes", false)]
    [InlineData("nodomain", "turned_on", false)]
    [InlineData("", "turned_on", false)]
    public void Validity(string entityId, string mode, bool expected) =>
        Assert.Equal(expected, new NotificationRule(entityId, mode).IsValid());

    [Fact]
    public void Json_round_trip_preserves_shape()
    {
        var rule = new NotificationRule("binary_sensor.haustuer", NotificationRule.TurnedOn, IsEnabled: false);
        var back = JsonSerializer.Deserialize<NotificationRule>(JsonSerializer.Serialize(rule));
        Assert.Equal(rule, back);
    }

    // ----- matcher -----

    [Fact]
    public void Turned_on_fires_only_on_the_rising_edge()
    {
        var rule = new NotificationRule("binary_sensor.tuer", NotificationRule.TurnedOn);
        Assert.True(NotificationRuleMatcher.ShouldNotify(rule, State("binary_sensor.tuer", "off"), State("binary_sensor.tuer", "on")));
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("binary_sensor.tuer", "on"), State("binary_sensor.tuer", "on")));
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("binary_sensor.tuer", "on"), State("binary_sensor.tuer", "off")));
    }

    [Fact]
    public void Open_counts_as_on_for_doors()
    {
        var rule = new NotificationRule("cover.tor", NotificationRule.TurnedOn);
        Assert.True(NotificationRuleMatcher.ShouldNotify(rule, State("cover.tor", "closed"), State("cover.tor", "open")));
    }

    [Fact]
    public void First_sighting_never_notifies()
    {
        var rule = new NotificationRule("light.flur", NotificationRule.TurnedOn);
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, null, State("light.flur", "on")));
    }

    [Fact]
    public void Unavailable_flapping_stays_silent()
    {
        var rule = new NotificationRule("light.flur", NotificationRule.AnyChange);
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("light.flur", "unavailable"), State("light.flur", "on")));
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("light.flur", "on"), State("light.flur", "unavailable")));
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("light.flur", "on"), State("light.flur", "unknown")));
    }

    [Fact]
    public void Any_change_fires_on_sensor_value_changes()
    {
        var rule = new NotificationRule("sensor.temp", NotificationRule.AnyChange);
        Assert.True(NotificationRuleMatcher.ShouldNotify(rule, State("sensor.temp", "21.5"), State("sensor.temp", "22.0")));
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("sensor.temp", "21.5"), State("sensor.temp", "21.5")));
    }

    [Fact]
    public void Other_entities_and_disabled_rules_never_match()
    {
        var rule = new NotificationRule("light.flur", NotificationRule.TurnedOn);
        Assert.False(NotificationRuleMatcher.ShouldNotify(rule, State("light.kueche", "off"), State("light.kueche", "on")));
        var disabled = rule with { IsEnabled = false };
        Assert.False(NotificationRuleMatcher.ShouldNotify(disabled, State("light.flur", "off"), State("light.flur", "on")));
    }
}
