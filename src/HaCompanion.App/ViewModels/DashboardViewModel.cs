// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.ViewModels;

/// <summary>View model for the full dashboard page (auto-detected tiles + status).</summary>
public sealed class DashboardViewModel
{
    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Category filter for the "all devices" list (chips + visible groups).</summary>
    public DeviceBrowserViewModel Browser { get; }

    public DashboardViewModel(EntityCatalogViewModel catalog, ShellViewModel shell, DeviceBrowserViewModel browser)
    {
        Catalog = catalog;
        Shell = shell;
        Browser = browser;
    }
}
