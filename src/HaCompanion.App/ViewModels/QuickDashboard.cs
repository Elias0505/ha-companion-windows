// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.ViewModels;

/// <summary>An entry in the quick panel's dashboard picker.</summary>
public sealed record QuickDashboard(string Title, string? UrlPath, bool IsFavorites)
{
    public static QuickDashboard Favorites { get; } = new("★  Favourites", null, true);
}
