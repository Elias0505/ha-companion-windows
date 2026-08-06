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
    public static void Apply(CoreWebView2 core, Uri baseUri, bool allowCertErrorsForBase)
    {
        core.NavigationStarting += (_, args) =>
        {
            if (HaWebViewScripts.IsAllowedTopLevelNavigation(args.Uri, baseUri))
                return;
            args.Cancel = true;
            OpenExternally(args.Uri);
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        if (allowCertErrorsForBase)
        {
            core.ServerCertificateErrorDetected += (_, args) =>
                args.Action = HaWebViewScripts.IsSameOrigin(args.RequestUri, baseUri)
                    ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                    : CoreWebView2ServerCertificateErrorAction.Default;
        }
    }

    private static void OpenExternally(string? uri)
    {
        // Only real web links leave the sandbox; javascript:, data:, file: etc. are dropped.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

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
