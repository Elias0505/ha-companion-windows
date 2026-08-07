// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Web;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HaWebViewScriptsTests
{
    [Theory]
    [InlineData("https://ha.local:443/lovelace/0", "https://ha.local")]
    [InlineData("http://ha.local:80/", "http://ha.local")]
    [InlineData("http://192.168.1.5:8123", "http://192.168.1.5:8123")]
    [InlineData("HTTPS://HA.Example.COM:8123/x", "https://ha.example.com:8123")]
    [InlineData("https://[::1]:8123/", "https://[::1]:8123")]
    [InlineData("https://bücher.example:8123", "https://xn--bcher-kva.example:8123")]
    public void Origin_matches_browser_location_origin(string url, string expected) =>
        Assert.Equal(expected, HaWebViewScripts.ComputeOrigin(new Uri(url)));

    [Theory]
    [InlineData("https://ha.local:8123/lovelace/0", true)]   // same origin, any path
    [InlineData("https://ha.local:8123", true)]
    [InlineData("https://ha.local:8124/", false)]            // different port
    [InlineData("http://ha.local:8123/", false)]             // different scheme
    [InlineData("https://evil.example/", false)]             // different host
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Same_origin_check_is_strict(string? candidate, bool expected) =>
        Assert.Equal(expected, HaWebViewScripts.IsSameOrigin(candidate, new Uri("https://ha.local:8123")));

    [Fact]
    public void About_blank_is_an_allowed_top_level_navigation()
    {
        var baseUri = new Uri("https://ha.local:8123");
        Assert.True(HaWebViewScripts.IsAllowedTopLevelNavigation("about:blank", baseUri));
        Assert.True(HaWebViewScripts.IsAllowedTopLevelNavigation("https://ha.local:8123/history", baseUri));
        Assert.False(HaWebViewScripts.IsAllowedTopLevelNavigation("https://evil.example/", baseUri));
    }

    [Fact]
    public void Auth_script_guards_frame_and_origin_before_writing()
    {
        var script = HaWebViewScripts.BuildAuthScript(new Uri("https://ha.local:8123"), "secret-token");

        var frameGuard = script.IndexOf("window.top !== window.self", StringComparison.Ordinal);
        var originGuard = script.IndexOf("window.location.origin !== \"https://ha.local:8123\"", StringComparison.Ordinal);
        var write = script.IndexOf("setItem('hassTokens'", StringComparison.Ordinal);

        Assert.True(frameGuard >= 0, "frame guard missing");
        Assert.True(originGuard >= 0, "origin guard missing");
        Assert.True(write >= 0, "token write missing");
        Assert.True(frameGuard < write && originGuard < write, "guards must run before the token write");
    }

    [Fact]
    public void Auth_script_uses_normalized_origin_as_hassUrl()
    {
        // The stored hassUrl must match what the frontend expects — origin, no trailing slash.
        var script = HaWebViewScripts.BuildAuthScript(new Uri("https://HA.Local:443/"), "t");
        Assert.Contains("\\u0022hassUrl\\u0022:\\u0022https://ha.local\\u0022", script.Replace("\\\"", "\\u0022", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Hide_chrome_script_hides_the_drawer_in_every_layout_mode()
    {
        var script = HaWebViewScripts.HideChromeScript;

        // Selectors are matched INSIDE each shadow root — a `ha-drawer .mdc-drawer`
        // descendant prefix never matches within ha-drawer's own shadow root. That
        // selector once shipped and only LOOKED like it worked because HA's own CSS
        // hides a closed modal drawer; the docked desktop drawer stayed visible.
        Assert.DoesNotContain("ha-drawer .mdc-drawer", script, StringComparison.Ordinal);
        Assert.Contains(".mdc-drawer, .mdc-drawer-scrim { display: none !important; }", script, StringComparison.Ordinal);
        Assert.Contains("ha-sidebar { display: none !important; }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Hide_chrome_script_reapplies_permanently()
    {
        var script = HaWebViewScripts.HideChromeScript;

        // HA recreates chrome elements long after load (SPA navigation, reconnect,
        // narrow/docked layout flips) — a hide pass that stops after startup leaves
        // the sidebar visible from then on.
        Assert.Contains("setInterval(walk, 2000)", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('resize', walk", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('visibilitychange', walk", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_script_json_escapes_a_hostile_token()
    {
        var script = HaWebViewScripts.BuildAuthScript(
            new Uri("https://ha.local:8123"), "a\"b\\c</script><script>alert(1)</script>");

        // The token must only ever appear JSON-escaped — never as raw markup that
        // could terminate the script block or the string literal.
        Assert.DoesNotContain("</script>", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hassTokens", script, StringComparison.Ordinal);
    }
}
