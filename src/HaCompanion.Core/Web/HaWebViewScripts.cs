// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;

namespace HaCompanion.Core.Web;

/// <summary>
/// Scripts and origin checks for embedding the Home Assistant frontend in a WebView2.
/// Lives in Core (no WebView2 dependency) so the token-guarding logic is unit-testable.
/// </summary>
public static class HaWebViewScripts
{
    /// <summary>
    /// The HA origin exactly as the browser reports it in <c>location.origin</c>:
    /// lowercase scheme+host (punycode for IDN, brackets for IPv6), default port omitted.
    /// </summary>
    public static string ComputeOrigin(Uri uri)
    {
        // Uri already lowercases scheme and host during parsing; IdnHost yields punycode.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost + "]" : uri.IdnHost;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>True when <paramref name="candidate"/> is an http(s) URL on the same origin as the HA base.</summary>
    public static bool IsSameOrigin(string? candidate, Uri baseUri) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.Equals(ComputeOrigin(uri), ComputeOrigin(baseUri), StringComparison.Ordinal);

    /// <summary>
    /// Whether a top-level navigation may stay inside the privileged WebView.
    /// Only the HA origin itself (plus the inert about:blank) — everything else
    /// belongs in the system browser.
    /// </summary>
    public static bool IsAllowedTopLevelNavigation(string? uri, Uri baseUri) =>
        string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)
        || IsSameOrigin(uri, baseUri);

    /// <summary>
    /// Pre-seed <c>localStorage["hassTokens"]</c> so the HA frontend logs in with the
    /// long-lived token without any prompt. Inject before the first navigation.
    /// WebView2 runs document-created scripts in every top-level AND child-frame
    /// document, so the script must self-disarm: it writes the token only in the
    /// top frame and only when the document's own origin is the configured HA origin —
    /// an embedded iframe card (foreign origin) gets nothing.
    /// </summary>
    public static string BuildAuthScript(Uri baseUri, string token)
    {
        var origin = ComputeOrigin(baseUri);
        var authJson = JsonSerializer.Serialize(new
        {
            hassUrl = origin,
            clientId = (string?)null,
            expires = 9999999999999,
            refresh_token = "",
            access_token = token,
            expires_in = 315360000,
        });
        return $$"""
            (function () {
              try {
                if (window.top !== window.self) return;
                if (window.location.origin !== {{JsonSerializer.Serialize(origin)}}) return;
                window.localStorage.setItem('hassTokens', {{JsonSerializer.Serialize(authJson)}});
              } catch (e) { }
            })();
            """;
    }

    /// <summary>
    /// Best-effort: hide HA's top toolbar and sidebar (across shadow roots) for a clean
    /// chrome-less embed. Re-applies for ~15s after each navigation. Used by the quick
    /// panel (which navigates via the app's own dashboard picker).
    /// </summary>
    public const string HideChromeScript =
        """
        (function () {
          const css = `
            .header, .mdc-top-app-bar { display: none !important; }
            .mdc-top-app-bar--fixed-adjust { padding-top: 0 !important; }
            ha-drawer .mdc-drawer, ha-sidebar { display: none !important; }
            ha-drawer .mdc-drawer-app-content { margin-inline-start: 0 !important; }
          `;
          function inject(root) {
            if (!root || root.__hacHidden) return;
            try {
              const s = document.createElement('style');
              s.textContent = css;
              root.appendChild(s);
              root.__hacHidden = true;
            } catch (e) { }
          }
          function walk() {
            try {
              const ha = document.querySelector('home-assistant');
              const main = ha && ha.shadowRoot && ha.shadowRoot.querySelector('home-assistant-main');
              const drawer = main && main.shadowRoot && main.shadowRoot.querySelector('ha-drawer');
              if (drawer && drawer.shadowRoot) inject(drawer.shadowRoot);
              const ppr = main && main.shadowRoot && main.shadowRoot.querySelector('partial-panel-resolver');
              const lovelace = ppr && ppr.querySelector('ha-panel-lovelace');
              const hui = lovelace && lovelace.shadowRoot && lovelace.shadowRoot.querySelector('hui-root');
              if (hui && hui.shadowRoot) inject(hui.shadowRoot);
              inject(document.head);
            } catch (e) { }
          }
          let n = 0;
          const t = setInterval(function () { walk(); if (++n > 60) clearInterval(t); }, 250);
          walk();
        })();
        """;
}
