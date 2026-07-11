// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.ViewModels;

/// <summary>View model for the full dashboard page (auto-detected tiles + status).</summary>
public sealed class DashboardViewModel
{
    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    public DashboardViewModel(EntityCatalogViewModel catalog, ShellViewModel shell)
    {
        Catalog = catalog;
        Shell = shell;
    }
}
