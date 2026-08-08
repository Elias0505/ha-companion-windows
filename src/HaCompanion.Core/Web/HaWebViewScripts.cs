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
    /// Camera stills self-destruct under live resizing: <c>ha-camera-image</c> requests
    /// <c>/api/camera_proxy/&lt;entity&gt;?width=W&amp;height=H</c> with the element box of the
    /// moment, the backend returns an image distorted to EXACTLY that box (verified: 500×500
    /// requested → 500×500 returned), and <c>hui-image</c> (aspect-ratio: auto) then adopts the
    /// skewed image's ratio permanently — the wrong box drives the next request, forever. The
    /// race sits inside HA's own resize handling (new width × stale height), so even a single
    /// re-layout can trip it. Without size parameters the proxy returns the camera's TRUE
    /// aspect, so this script rewrites every same-origin camera_proxy still URL at the
    /// img.src / setAttribute layer: the first load goes parameter-less (native size reveals
    /// the true ratio), every later one requests a fixed small box in that learned ratio —
    /// distortion becomes impossible, stills stay cheap to decode, and identical URLs turn
    /// resize-triggered refetch storms into no-ops.
    /// </summary>
    public const string CameraStillFixScript =
        """
        (function () {
          // True aspect ratio per camera path, learned from the first (parameter-less,
          // native-size) still. Requesting a FIXED small box in the true ratio afterwards
          // keeps stills cheap to decode/paint (a permanent full-res still made live
          // resizing visibly laggy) while remaining distortion-proof — and because the
          // rewritten URL is byte-identical on every set, resize-triggered refetch storms
          // collapse into no-ops.
          const ratios = new Map();
          function fixCam(img, v) {
            try {
              if (typeof v !== 'string' || v.indexOf('/api/camera_proxy/') === -1) return v;
              const u = new URL(v, location.href);
              if (u.origin !== location.origin) return v;
              if (img && !img.__hacCamHook) {
                img.__hacCamHook = true;
                img.addEventListener('load', function () {
                  try {
                    if (img.naturalWidth > 0)
                      ratios.set(new URL(img.currentSrc || img.src, location.href).pathname,
                                 img.naturalHeight / img.naturalWidth);
                  } catch (e) { }
                });
              }
              const r = ratios.get(u.pathname);
              if (r) {
                u.searchParams.set('width', '512');
                u.searchParams.set('height', String(Math.max(1, Math.round(512 * r))));
              } else {
                u.searchParams.delete('width');
                u.searchParams.delete('height');
              }
              return u.toString();
            } catch (e) { return v; }
          }
          try {
            const desc = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src');
            Object.defineProperty(HTMLImageElement.prototype, 'src', {
              configurable: true,
              get() { return desc.get.call(this); },
              set(v) { desc.set.call(this, fixCam(this, v)); }
            });
            const setAttr = Element.prototype.setAttribute;
            Element.prototype.setAttribute = function (name, value) {
              if (name === 'src' && this instanceof HTMLImageElement) value = fixCam(this, value);
              return setAttr.call(this, name, value);
            };
          } catch (e) { }
        })();
        """;

    /// <summary>
    /// Best-effort: hide HA's top toolbar and sidebar (across shadow roots) for a clean
    /// chrome-less embed. Used by the quick panel (which navigates via the app's own
    /// dashboard picker). The styles are per shadow root: selectors are matched inside
    /// each root, so a descendant prefix like <c>ha-drawer .mdc-drawer</c> would never
    /// match within ha-drawer's own shadow root. The drawer is hidden in EVERY layout
    /// mode — HA flips between the narrow modal drawer and the docked desktop sidebar
    /// when the viewport crosses its width threshold (which rapid panel show/hide cycles
    /// can trigger via DPI-scale changes on the hidden window) — and hiding is applied
    /// permanently, because HA recreates chrome elements long after load (SPA navigation,
    /// reconnect, layout flips).
    /// </summary>
    public const string HideChromeScript =
        """
        (function () {
          const drawerCss = `
            .mdc-drawer, .mdc-drawer-scrim { display: none !important; }
            .mdc-drawer-app-content { margin-inline-start: 0 !important; }
          `;
          const mainCss = `
            ha-sidebar { display: none !important; }
          `;
          const chromeCss = `
            .header, .mdc-top-app-bar { display: none !important; }
            .mdc-top-app-bar--fixed-adjust { padding-top: 0 !important; }
          `;
          function inject(root, css) {
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
              if (main && main.shadowRoot) inject(main.shadowRoot, mainCss);
              const drawer = main && main.shadowRoot && main.shadowRoot.querySelector('ha-drawer');
              if (drawer && drawer.shadowRoot) inject(drawer.shadowRoot, drawerCss);
              const ppr = main && main.shadowRoot && main.shadowRoot.querySelector('partial-panel-resolver');
              const lovelace = ppr && ppr.querySelector('ha-panel-lovelace');
              const hui = lovelace && lovelace.shadowRoot && lovelace.shadowRoot.querySelector('hui-root');
              if (hui && hui.shadowRoot) inject(hui.shadowRoot, chromeCss);
              inject(document.head, chromeCss);
            } catch (e) { }
          }
          let n = 0;
          const fast = setInterval(function () { walk(); if (++n > 60) clearInterval(fast); }, 250);
          setInterval(walk, 2000);
          window.addEventListener('resize', walk, true);
          document.addEventListener('visibilitychange', walk, true);
          window.addEventListener('location-changed', walk, true);
          walk();
        })();
        """;
}
