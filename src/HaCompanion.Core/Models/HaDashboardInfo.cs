// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Models;

/// <summary>A Lovelace dashboard descriptor. <see cref="UrlPath"/> is null for the default dashboard.</summary>
public sealed record HaDashboardInfo(string? UrlPath, string Title, string? Icon)
{
    /// <summary>The URL segment to navigate to (default dashboard lives at /lovelace).</summary>
    public string NavigationPath => UrlPath ?? "lovelace";
}
