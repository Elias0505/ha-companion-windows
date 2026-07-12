// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Controls;

/// <summary>
/// A GridView whose items panel is a <see cref="VariableSizedWrapGrid"/> and whose item
/// containers take their row/column spans from the tile view model — this is what lets a
/// favourite tile be small (1×1), wide (2×1) or large (2×2), like Start-menu tiles.
/// </summary>
public sealed partial class VariableTileGridView : GridView
{
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is GridViewItem container && item is EntityTileViewModel tile)
        {
            VariableSizedWrapGrid.SetColumnSpan(container, tile.ColSpan);
            VariableSizedWrapGrid.SetRowSpan(container, tile.RowSpan);
        }
    }

    /// <summary>Re-apply a live tile's spans after its size mode changed and re-flow the grid.</summary>
    public void RefreshSpans(EntityTileViewModel tile)
    {
        if (ContainerFromItem(tile) is GridViewItem container)
        {
            VariableSizedWrapGrid.SetColumnSpan(container, tile.ColSpan);
            VariableSizedWrapGrid.SetRowSpan(container, tile.RowSpan);
            if (ItemsPanelRoot is VariableSizedWrapGrid panel)
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }
    }
}
