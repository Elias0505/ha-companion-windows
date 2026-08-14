// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using HaCompanion.Core.Web;
using Microsoft.Web.WebView2.Core;

namespace HaCompanion.App.Services;

/// <summary>
/// Locks a privileged WebView (one that carries the HA token) to the configured
/// HA origin: foreign top-level navigations and popups open in the system browser,
/// and the self-signed-certificate opt-out applies to the HA host only.
/// Child-frame navigation is deliberately left alone so Lovelace iframe cards keep
/// working — the auth script's own origin/frame guard protects the token there.
/// </summary>
public static class WebViewHardening
{
    // Handing a link to the real browser is a user-visible action, so it must originate
    // from a user gesture. Without this, a hostile Lovelace iframe card could loop
    // `top.location = …` / `window.open(…)` and drive the user's actual browser (with its
    // cookies) to arbitrary pages, unattended. The debounce additionally caps a burst that
    // slips through with genuine gestures.
    private static readonly TimeSpan MinExternalInterval = TimeSpan.FromSeconds(2);
    private static long _lastExternalMs;

    /// <param name="currentBaseUri">
    /// Read on every event, never captured: the user can change the HA URL at runtime, and a
    /// frozen origin would then treat the NEW instance as foreign (bouncing it to the system
    /// browser) while still trusting the OLD one's certificate.
    /// </param>
    /// <param name="allowCertErrorsForBase">
    /// Also read per event, for the same reason — and because it is a SECURITY setting: passing
    /// the bool by value meant the handler was only attached when the option was on, so turning
    /// it back off left the old handler accepting bad certificates until the app restarted.
    /// </param>
    public static void Apply(CoreWebView2 core, Func<Uri> currentBaseUri, Func<bool> allowCertErrorsForBase)
    {
        core.NavigationStarting += (_, args) =>
        {
            if (HaWebViewScripts.IsAllowedTopLevelNavigation(args.Uri, currentBaseUri()))
                return;
            // Foreign navigation is always cancelled; only a user-initiated one is handed
            // to the browser. A scripted redirect is simply dropped.
            args.Cancel = true;
            if (args.IsUserInitiated)
                OpenExternally(args.Uri);
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated)
                OpenExternally(args.Uri);
        };

        // Always subscribe; decide per event. Default = let WebView2 show its own warning.
        core.ServerCertificateErrorDetected += (_, args) =>
            args.Action = allowCertErrorsForBase()
                          && HaWebViewScripts.IsSameOrigin(args.RequestUri, currentBaseUri())
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Default;
    }

    private static void OpenExternally(string? uri)
    {
        // Only real web links leave the sandbox; javascript:, data:, file: etc. are dropped.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastExternalMs < MinExternalInterval.TotalMilliseconds)
            return;
        _lastExternalMs = now;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = parsed.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser — nothing safe left to do with the link.
        }
    }
}
