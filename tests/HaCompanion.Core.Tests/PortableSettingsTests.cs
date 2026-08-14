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
    public void An_explicit_null_rejects_the_bundle()
    {
        // A key present with JSON null is not "missing": on the non-nullable int fields it
        // throws at load, and on a string field it nulls a value the app assumes is set.
        // {"QuickPanelWidth": null} was the concrete crash — it made settings.json
        // undeserializable, tripping the .bad fallback that wipes the DPAPI token.
        Assert.False(PortableSettings.TypesValid(new JsonObject { ["Hotkey"] = null }));
        Assert.False(PortableSettings.TypesValid(new JsonObject { ["QuickPanelWidth"] = null }));
    }

    [Theory]
    [InlineData(3.5)]          // non-integer number
    [InlineData(1e30)]         // overflows Int32
    [InlineData(-1e30)]
    public void A_non_integer_number_rejects_an_int_key(double value)
    {
        // JsonValueKind.Number alone isn't enough: these all pass the kind check but throw
        // when deserialized into the non-nullable int QuickPanelWidth.
        var bundle = new JsonObject { ["QuickPanelWidth"] = JsonValue.Create(value) };
        Assert.False(PortableSettings.TypesValid(bundle));
    }

    [Fact]
    public void A_plain_integer_is_accepted_for_int_keys()
    {
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["QuickPanelWidth"] = 520 }));
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["IdleSensorThresholdMinutes"] = 15 }));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("homeassistant.local:8123")]
    [InlineData("file:///C:/x")]
    public void A_base_url_that_is_not_http_rejects_the_bundle(string url)
    {
        // BaseUrl feeds `new Uri(..., Absolute)` at runtime; an unusable value would throw
        // inside the WebView init or the connect path long after the import succeeded.
        Assert.False(PortableSettings.TypesValid(new JsonObject { ["BaseUrl"] = url }));
    }

    [Fact]
    public void A_valid_or_empty_base_url_is_accepted()
    {
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["BaseUrl"] = "http://192.168.1.5:8123" }));
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["BaseUrl"] = "" })); // not configured yet
    }

    [Fact]
    public void Toast_name_is_portable_and_the_device_name_is_not()
    {
        // ToastAppName is cosmetic and machine-independent (#9). HaDeviceName is a per-machine
        // identity (#8): imported onto a second PC, both would register under one name and
        // fight over notify.mobile_app_<slug>.
        Assert.Contains("ToastAppName", PortableSettings.Keys);
        Assert.DoesNotContain("HaDeviceName", PortableSettings.Keys);
        Assert.True(PortableSettings.TypesValid(new JsonObject { ["ToastAppName"] = "Home Assistant" }));
        Assert.False(PortableSettings.TypesValid(new JsonObject { ["ToastAppName"] = 42 }));
    }
}
