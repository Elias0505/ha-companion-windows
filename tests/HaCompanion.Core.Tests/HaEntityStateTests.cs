// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.Models;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HaEntityStateTests
{
    private static HaEntityState State(string entityId, string state, Dictionary<string, JsonElement>? attrs = null) =>
        new() { EntityId = entityId, State = state, Attributes = attrs ?? new() };

    [Theory]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("open", true)]
    [InlineData("home", true)]
    [InlineData("unlocked", true)] // locks: unlocked must count as active or they can never be LOCKED
    [InlineData("off", false)]
    [InlineData("closed", false)]
    [InlineData("locked", false)]
    [InlineData("idle", false)]
    public void IsOn_matches_active_states(string state, bool expected) =>
        Assert.Equal(expected, State("light.x", state).IsOn);

    [Theory]
    [InlineData("unavailable", true)]
    [InlineData("unknown", true)]
    [InlineData("on", false)]
    public void IsUnavailable_matches(string state, bool expected) =>
        Assert.Equal(expected, State("sensor.x", state).IsUnavailable);

    [Theory]
    [InlineData("light.kitchen", "light")]
    [InlineData("binary_sensor.door", "binary_sensor")]
    [InlineData("nodomain", "nodomain")]
    public void Domain_is_prefix_before_dot(string id, string expected) =>
        Assert.Equal(expected, State(id, "on").Domain);

    [Fact]
    public void FriendlyName_falls_back_to_entity_id()
    {
        Assert.Equal("light.x", State("light.x", "on").FriendlyName);

        var doc = JsonDocument.Parse("\"Kitchen\"");
        var attrs = new Dictionary<string, JsonElement> { ["friendly_name"] = doc.RootElement.Clone() };
        Assert.Equal("Kitchen", State("light.x", "on", attrs).FriendlyName);
    }

    [Fact]
    public void GetAttributeString_returns_only_strings()
    {
        var attrs = new Dictionary<string, JsonElement>
        {
            ["unit"] = JsonDocument.Parse("\"kW\"").RootElement.Clone(),
            ["num"] = JsonDocument.Parse("5").RootElement.Clone(),
        };
        var s = State("sensor.x", "5", attrs);
        Assert.Equal("kW", s.GetAttributeString("unit"));
        Assert.Null(s.GetAttributeString("num"));
        Assert.Null(s.GetAttributeString("missing"));
    }
}
