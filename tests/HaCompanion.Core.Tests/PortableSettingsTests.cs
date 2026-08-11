// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json.Nodes;
using HaCompanion.Core.Configuration;
using Xunit;

namespace HaCompanion.Core.Tests;

public class PortableSettingsTests
{
    [Fact]
    public void No_security_decision_is_importable()
    {
        // A config bundle is a file a user may receive from someone else. If an import could
        // flip these, "import my setup" would turn into: disable certificate checking, enable
        // the HA→PC commands and seed a launch whitelist — remote code execution by sharing
        // a .json. Secrets must not be importable either.
        foreach (var forbidden in PortableSettings.NeverImportable)
            Assert.DoesNotContain(forbidden, PortableSettings.Keys);
    }

    [Fact]
    public void Changing_the_base_url_must_reset_credentials()
    {
        // Keeping the stored token while repointing the URL is exactly how an imported
        // bundle would exfiltrate it: the next connect sends it to the attacker's host.
        Assert.True(PortableSettings.ForcesCredentialReset("BaseUrl"));
        Assert.False(PortableSettings.ForcesCredentialReset("Hotkey"));
    }

    [Fact]
    public void A_well_formed_bundle_validates()
    {
        var bundle = new JsonObject
        {
            ["BaseUrl"] = "https://ha.local:8123",
            ["QuickPanelWidth"] = 520,
            ["AutoHideQuickPanel"] = true,
            ["Language"] = "de",
        };
        Assert.True(PortableSettings.TypesValid(bundle));
    }

    [Fact]
    public void Missing_keys_are_fine()
    {
        // Bundles from older versions simply carry fewer keys.
        Assert.True(PortableSettings.TypesValid(new JsonObject()));
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["Hotkey"] = "Ctrl+H" }));
    }

    [Theory]
    [InlineData("QuickPanelWidth", "wide")]          // number expected
    [InlineData("AutoHideQuickPanel", "yes")]        // bool expected
    [InlineData("BaseUrl", 42)]                      // string expected
    [InlineData("IdleSensorThresholdMinutes", true)]
    public void A_type_confused_value_rejects_the_bundle(string key, object value)
    {
        // Writing a wrong type would make settings.json undeserializable; the load path then
        // falls back to defaults, and the next save overwrites the encrypted token with an
        // empty string — permanent credential loss from a malformed file.
        var bundle = new JsonObject { [key] = JsonValue.Create(value) };
        Assert.False(PortableSettings.TypesValid(bundle));
    }

    [Fact]
    public void Null_values_do_not_trip_validation()
    {
        var bundle = new JsonObject { ["Hotkey"] = null };
        Assert.True(PortableSettings.TypesValid(bundle));
    }
}
