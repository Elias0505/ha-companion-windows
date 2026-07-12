// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace HaCompanion.App.Controls;

/// <summary>
/// Pointer-drag state machine for resizing a tile by hand with the corner grip: the drag
/// delta is translated into column/row spans (snapping to whole grid cells) and applied
/// live so the grid re-flows while dragging. A release without meaningful movement counts
/// as a click (the caller then cycles the size presets instead). The owning grid is found
/// from the grip via the visual tree, so the same instance serves the flat favourites grid
/// and the per-category grids alike.
/// </summary>
public sealed class TileResizeDrag
{
    private const double ClickThresholdDip = 4.0;

    private EntityTileViewModel? _tile;
    private VariableTileGridView? _grid;
    private Point _start;
    private double _cellWidth, _cellHeight;
    private int _startCols, _startRows;
    private bool _moved;

    public void Begin(UIElement grip, PointerRoutedEventArgs e, EntityTileViewModel tile, double cellWidth, double cellHeight)
    {
        _grid = FindAncestorGrid(grip);
        if (_grid is null)
            return;
        _tile = tile;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _start = e.GetCurrentPoint(_grid).Position;
        _startCols = tile.ColSpan;
        _startRows = tile.RowSpan;
        _moved = false;
        grip.CapturePointer(e.Pointer);
    }

    /// <summary>Apply the current drag position; returns false when no drag is active.</summary>
    public bool Update(PointerRoutedEventArgs e)
    {
        if (_tile is null || _grid is null)
            return false;

        var pos = e.GetCurrentPoint(_grid).Position;
        var dx = pos.X - _start.X;
        var dy = pos.Y - _start.Y;
        if (Math.Abs(dx) > ClickThresholdDip || Math.Abs(dy) > ClickThresholdDip)
            _moved = true;

        var maxCols = Math.Max(1, (int)(_grid.ActualWidth / _cellWidth));
        var cols = Math.Clamp((int)Math.Round(_startCols + dx / _cellWidth), 1, maxCols);
        var rows = Math.Clamp((int)Math.Round(_startRows + dy / _cellHeight), 1, 3);
        if (cols != _tile.ColSpan || rows != _tile.RowSpan)
        {
            _tile.SetSpans(cols, rows);
            _grid.RefreshSpans(_tile); // live re-flow while dragging
        }
        return true;
    }

    /// <summary>Finish the drag; returns the tile and whether it was a real drag (vs. a click).</summary>
    public (EntityTileViewModel? Tile, bool Moved) End(UIElement? grip, PointerRoutedEventArgs e)
    {
        var result = (_tile, _moved);
        _tile = null;
        _grid = null;
        grip?.ReleasePointerCapture(e.Pointer);
        return result;
    }

    private static VariableTileGridView? FindAncestorGrid(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is VariableTileGridView grid)
                return grid;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
