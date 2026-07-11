// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.ViewModels;

/// <summary>View model for the slide-in quick panel (Win+Ctrl+H).</summary>
public sealed class QuickPanelViewModel
{
    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    public QuickPanelViewModel(EntityCatalogViewModel catalog, ShellViewModel shell)
    {
        Catalog = catalog;
        Shell = shell;
    }
}
