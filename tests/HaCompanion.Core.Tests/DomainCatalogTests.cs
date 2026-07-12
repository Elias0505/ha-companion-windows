// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Models;
using Xunit;

namespace HaCompanion.Core.Tests;

public class DomainCatalogTests
{
    [Theory]
    [InlineData("light", true)]
    [InlineData("script", true)]
    [InlineData("sensor", false)]
    [InlineData("binary_sensor", false)]
    [InlineData("weather", false)]
    public void HasAction_only_for_actionable(string domain, bool expected) =>
        Assert.Equal(expected, DomainCatalog.HasAction(domain));

    [Theory]
    [InlineData("light", true)]
    [InlineData("sensor", true)]
    [InlineData("binary_sensor", true)]
    [InlineData("weather", false)]
    public void IsDisplayable_includes_read_only(string domain, bool expected) =>
        Assert.Equal(expected, DomainCatalog.IsDisplayable(domain));

    [Fact]
    public void Lock_toggles_in_both_directions()
    {
        // unlocked (active) -> lock; locked (inactive) -> unlock — the bug class where a
        // lock could only ever be unlocked.
        Assert.Equal(("lock", "lock"), DomainCatalog.ResolveAction("lock", isOn: true));
        Assert.Equal(("lock", "unlock"), DomainCatalog.ResolveAction("lock", isOn: false));
    }

    [Theory]
    [InlineData("scene", "scene", "turn_on")]
    [InlineData("button", "button", "press")]
    [InlineData("script", "script", "toggle")]
    [InlineData("automation", "automation", "toggle")]
    [InlineData("light", "homeassistant", "toggle")]
    [InlineData("switch", "homeassistant", "toggle")]
    public void ResolveAction_maps_domains(string domain, string expDomain, string expService) =>
        Assert.Equal((expDomain, expService), DomainCatalog.ResolveAction(domain, isOn: false));
}
