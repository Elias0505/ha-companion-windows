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

    public bool HasConnection =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public HaConnectionSettings ToConnectionSettings() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
    };
}
