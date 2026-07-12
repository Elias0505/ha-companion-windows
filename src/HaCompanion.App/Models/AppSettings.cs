// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Configuration;

namespace HaCompanion.App.Models;

/// <summary>User-configurable application settings (persisted; token stored encrypted).</summary>
public sealed class AppSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Human-readable hotkey label, e.g. "Win+Ctrl+H".</summary>
    public string Hotkey { get; set; } = "Win+Ctrl+H";

    /// <summary>Hide the quick panel automatically when it loses focus (you click elsewhere).</summary>
    public bool AutoHideQuickPanel { get; set; } = true;

    /// <summary>Quick panel width in device-independent pixels (320–900).</summary>
    public int QuickPanelWidth { get; set; } = 400;

    /// <summary>UI language code (en, de, es, fr, zh, hi).</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// What the quick panel shows on every open: "last" (remember the last view),
    /// "favorites", or "dash:&lt;url_path&gt;" for a specific HA dashboard.
    /// </summary>
    public string QuickPanelStartView { get; set; } = "last";

    /// <summary>Allow resizing the quick panel by dragging the grip on its left edge.</summary>
    public bool QuickPanelDragResize { get; set; } = true;

    /// <summary>Sort quick-panel favourites by category (start-page order) instead of manual order.</summary>
    public bool QuickPanelSortByCategory { get; set; }

    /// <summary>Show Home Assistant persistent notifications as native Windows toasts.</summary>
    public bool ShowHaNotifications { get; set; } = true;

    public bool HasConnection =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public HaConnectionSettings ToConnectionSettings() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
    };
}
