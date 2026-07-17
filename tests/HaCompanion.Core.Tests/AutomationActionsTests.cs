// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class AutomationActionsTests
{
    [Theory]
    [InlineData("script")]
    [InlineData("scene")]
    [InlineData("button")]
    public void Run_only_domains_offer_exactly_run(string domain)
    {
        var allowed = AutomationActions.AllowedFor(domain);
        Assert.Equal(new[] { AutomationActions.Run }, allowed);
    }

    [Fact]
    public void Lock_offers_explicit_directions_but_no_toggle()
    {
        var allowed = AutomationActions.AllowedFor("lock");
        Assert.Contains(AutomationActions.TurnOn, allowed);
        Assert.Contains(AutomationActions.TurnOff, allowed);
        Assert.DoesNotContain(AutomationActions.Toggle, allowed);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("switch")]
    [InlineData("input_boolean")]
    [InlineData("automation")]
    public void Switchable_domains_offer_on_off_toggle(string domain)
    {
        Assert.Equal(
            new[] { AutomationActions.TurnOn, AutomationActions.TurnOff, AutomationActions.Toggle },
            AutomationActions.AllowedFor(domain));
    }

    [Theory]
    [InlineData("media_player", AutomationActions.SetVolume)]
    [InlineData("climate", AutomationActions.SetTemperature)]
    [InlineData("cover", AutomationActions.SetPosition)]
    [InlineData("fan", AutomationActions.SetPercentage)]
    public void Value_domains_add_a_set_verb_after_on_off_toggle(string domain, string setVerb)
    {
        Assert.Equal(
            new[] { AutomationActions.TurnOn, AutomationActions.TurnOff, AutomationActions.Toggle, setVerb },
            AutomationActions.AllowedFor(domain));
    }

    [Theory]
    [InlineData(AutomationActions.SetVolume, "volume_level")]
    [InlineData(AutomationActions.SetTemperature, "temperature")]
    [InlineData(AutomationActions.SetPosition, "position")]
    [InlineData(AutomationActions.SetPercentage, "percentage")]
    [InlineData(AutomationActions.TurnOn, null)]
    public void DataKey_names_the_service_data_field(string action, string? key) =>
        Assert.Equal(key, AutomationActions.DataKey(action));

    [Theory]
    [InlineData("sensor")]
    [InlineData("binary_sensor")]
    [InlineData("weather")]
    public void Non_actionable_domains_offer_nothing(string domain) =>
        Assert.Empty(AutomationActions.AllowedFor(domain));

    [Theory]
    [InlineData("script", "run", "script", "turn_on")]
    [InlineData("scene", "run", "scene", "turn_on")]
    [InlineData("button", "run", "button", "press")]
    [InlineData("lock", "turn_on", "lock", "lock")]
    [InlineData("lock", "turn_off", "lock", "unlock")]
    [InlineData("light", "turn_on", "homeassistant", "turn_on")]
    [InlineData("switch", "turn_off", "homeassistant", "turn_off")]
    [InlineData("climate", "toggle", "homeassistant", "toggle")]
    [InlineData("media_player", "set_volume", "media_player", "volume_set")]
    [InlineData("climate", "set_temperature", "climate", "set_temperature")]
    [InlineData("cover", "set_position", "cover", "set_cover_position")]
    [InlineData("fan", "set_percentage", "fan", "set_percentage")]
    public void Resolve_maps_to_the_right_service(string domain, string action, string svcDomain, string svc) =>
        Assert.Equal((svcDomain, svc), AutomationActions.Resolve(domain, action));

    [Fact]
    public void Resolve_rejects_disallowed_combinations() =>
        Assert.Throws<ArgumentException>(() => AutomationActions.Resolve("script", "banana"));
}
