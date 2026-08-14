// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Configuration;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HaConnectionSettingsTests
{
    [Theory]
    [InlineData("https://ha.example.com:8123", "token", true)]
    [InlineData("http://192.168.1.5:8123", "token", true)]
    [InlineData("ftp://x", "token", false)]
    [InlineData("not a url", "token", false)]
    [InlineData("https://ha.example.com", "", false)]
    public void IsValid_requires_http_url_and_token(string url, string token, bool expected) =>
        Assert.Equal(expected, new HaConnectionSettings { BaseUrl = url, Token = token }.IsValid);

    [Theory]
    [InlineData("https://ha.example.com:8123", "wss://ha.example.com:8123/api/websocket")]
    [InlineData("http://192.168.1.5:8123", "ws://192.168.1.5:8123/api/websocket")]
    [InlineData("https://ha.example.com:8123/", "wss://ha.example.com:8123/api/websocket")]
    public void WebSocketUri_derives_scheme_and_path(string baseUrl, string expected) =>
        Assert.Equal(expected, new HaConnectionSettings { BaseUrl = baseUrl, Token = "t" }.WebSocketUri.ToString());

    [Theory]
    [InlineData("https://ha.local:8123", "https://ha.local:8123", true)]
    [InlineData("https://ha.local:8123", "https://ha.local:8123/lovelace/0", true)]  // path is not part of the origin
    [InlineData("https://ha.local:8123", "https://HA.LOCAL:8123", true)]             // host is case-insensitive
    [InlineData("https://ha.local", "https://ha.local:443", true)]                   // implicit default port
    [InlineData("http://ha.local", "http://ha.local:80", true)]
    [InlineData("https://ha.local:8123", "https://ha.local:8124", false)]            // different port
    [InlineData("https://ha.local:8123", "http://ha.local:8123", false)]             // different scheme
    [InlineData("https://ha.local:8123", "https://evil.local:8123", false)]          // different host
    [InlineData("https://ha.local:8123", "https://ha.local.evil.com:8123", false)]   // suffix confusion
    [InlineData("https://ha.local:8123", "", false)]                                 // unparseable → fail closed
    [InlineData("", "https://ha.local:8123", false)]
    [InlineData("ha.local:8123", "https://ha.local:8123", false)]                    // schemeless → fail closed
    public void IsSameOrigin_compares_scheme_host_and_port(string a, string b, bool expected)
    {
        // The stored token is a credential for ONE host. This comparison decides whether it may
        // survive a URL change — a false "same origin" would hand it to an attacker's instance
        // (e.g. a spoofed mDNS responder on the LAN), so anything unparseable must fail closed.
        Assert.Equal(expected, HaConnectionSettings.IsSameOrigin(a, b));
    }

    [Theory]
    [InlineData("https://ha.local:8123", true)]
    [InlineData("http://192.168.1.5:8123", true)]
    [InlineData("homeassistant.local:8123", false)]  // the classic user input — must not reach BaseUri
    [InlineData("ftp://ha.local", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsableBaseUrl_requires_an_absolute_http_url(string? url, bool expected) =>
        // Checked before probing: a schemeless value used to throw inside CheckAsync and leave
        // the settings page stuck on "Connecting…" with no message.
        Assert.Equal(expected, HaConnectionSettings.IsUsableBaseUrl(url));

    [Fact]
    public void An_empty_or_first_time_base_url_is_never_the_same_origin()
    {
        // First setup: nothing stored yet. The connect path uses "origin changed" to decide
        // whether the stored token may travel — with no stored URL there is nothing to protect,
        // but the comparison must still be well-defined and fail closed rather than throw.
        Assert.False(HaConnectionSettings.IsSameOrigin(string.Empty, "https://ha.local:8123"));
        Assert.False(HaConnectionSettings.IsSameOrigin(null, null));
    }

    [Fact]
    public void Same_origin_survives_trailing_slashes_and_ports_written_out()
    {
        // The stored value and what the user types rarely match character for character;
        // only scheme+host+port may decide, or a harmless edit would block the connect.
        Assert.True(HaConnectionSettings.IsSameOrigin("https://ha.local:8123/", "https://ha.local:8123"));
        Assert.True(HaConnectionSettings.IsSameOrigin("https://ha.local:8123", "https://ha.local:8123/lovelace/0"));
    }

    [Theory]
    [InlineData("http://ha.local:8123", "https://ha.local:8123", true)]   // explicit same port
    [InlineData("http://ha.local", "https://ha.local", true)]             // 80 -> 443 (both default)
    [InlineData("https://ha.local", "http://ha.local", false)]            // DOWNGRADE — never
    [InlineData("http://ha.local:8123", "https://ha.local", false)]       // port changes too
    [InlineData("http://ha.local", "https://ha.local:8123", false)]
    [InlineData("http://ha.local:8123", "https://other.local:8123", false)] // different host
    [InlineData("http://ha.local:8123", "http://ha.local:8123", false)]   // no upgrade at all
    [InlineData("", "https://ha.local", false)]                            // unparseable → closed
    public void Scheme_upgrade_is_the_only_origin_change_that_keeps_credentials(string from, string to, bool expected)
    {
        // This is the single allowance that lets the stored token survive an origin change —
        // a false positive here would hand it to a different instance, so pin every edge.
        Assert.Equal(expected, HaConnectionSettings.IsSchemeUpgrade(from, to));
    }
}
