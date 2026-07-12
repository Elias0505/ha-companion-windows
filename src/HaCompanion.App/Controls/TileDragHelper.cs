// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace HaCompanion.App.Controls;

/// <summary>Shared drop-position math for the manually implemented tile reorder.</summary>
public static class TileDragHelper
{
    /// <summary>
    /// Maps a drop point inside the grid to an insertion index (0..count): walking the realized
    /// containers in display order, the drop lands before the first tile whose row is below the
    /// point, or — within the drop row — before the first tile whose horizontal centre is past it.
    /// </summary>
    public static int InsertionIndex(GridView grid, Point position, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (grid.ContainerFromIndex(i) is not GridViewItem container)
                continue;
            var bounds = container.TransformToVisual(grid)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (position.Y < bounds.Top)
                return i;
            if (position.Y <= bounds.Bottom && position.X < bounds.Left + bounds.Width / 2)
                return i;
        }
        return count;
    }
}
