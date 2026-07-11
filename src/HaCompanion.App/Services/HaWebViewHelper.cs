// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;

namespace HaCompanion.App.Services;

/// <summary>Shared scripts for embedding the Home Assistant frontend in a WebView2.</summary>
public static class HaWebViewHelper
{
    /// <summary>
    /// Pre-seed <c>localStorage["hassTokens"]</c> so the HA frontend logs in with the
    /// long-lived token without any prompt. Inject before the first navigation.
    /// </summary>
    public static string BuildAuthScript(string baseUrl, string token)
    {
        var authJson = JsonSerializer.Serialize(new
        {
            hassUrl = baseUrl,
            clientId = (string?)null,
            expires = 9999999999999,
            refresh_token = "",
            access_token = token,
            expires_in = 315360000,
        });
        return $"window.localStorage.setItem('hassTokens', {JsonSerializer.Serialize(authJson)});";
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
            .header, app-header, ha-top-app-bar-fixed, .mdc-top-app-bar { display: none !important; }
            .mdc-top-app-bar--fixed-adjust { padding-top: 0 !important; }
            ha-drawer .mdc-drawer, ha-drawer aside, ha-sidebar { display: none !important; }
            ha-drawer .mdc-drawer-app-content { margin-inline-start: 0 !important; }
            #view { padding-top: 0 !important; }
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
