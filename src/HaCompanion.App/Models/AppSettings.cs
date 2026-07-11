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

    /// <summary>Open the quick panel on your HA dashboard instead of Favourites.</summary>
    public bool QuickPanelStartOnDashboard { get; set; }

    public bool HasConnection =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public HaConnectionSettings ToConnectionSettings() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
    };
}
