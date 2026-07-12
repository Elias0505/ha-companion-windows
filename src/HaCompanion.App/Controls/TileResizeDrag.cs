// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace HaCompanion.App.Controls;

/// <summary>
/// Pointer-drag state machine for resizing a tile by hand with the corner grip: the drag
/// delta is translated into column/row spans (snapping to whole grid cells) and applied
/// live so the grid re-flows while dragging. A release without meaningful movement counts
/// as a click (the caller then cycles the size presets instead).
/// </summary>
public sealed class TileResizeDrag
{
    private const double ClickThresholdDip = 4.0;

    private EntityTileViewModel? _tile;
    private Point _start;
    private int _startCols, _startRows;
    private bool _moved;

    /// <summary>True while a grip drag is in progress.</summary>
    public bool IsActive => _tile is not null;

    public void Begin(UIElement grip, PointerRoutedEventArgs e, EntityTileViewModel tile, UIElement reference)
    {
        _tile = tile;
        _start = e.GetCurrentPoint(reference).Position;
        _startCols = tile.ColSpan;
        _startRows = tile.RowSpan;
        _moved = false;
        grip.CapturePointer(e.Pointer);
    }

    /// <summary>Apply the current drag position; returns false when no drag is active.</summary>
    public bool Update(PointerRoutedEventArgs e, UIElement reference, double cellWidth, double cellHeight, VariableTileGridView grid)
    {
        if (_tile is null)
            return false;

        var pos = e.GetCurrentPoint(reference).Position;
        var dx = pos.X - _start.X;
        var dy = pos.Y - _start.Y;
        if (Math.Abs(dx) > ClickThresholdDip || Math.Abs(dy) > ClickThresholdDip)
            _moved = true;

        var maxCols = Math.Max(1, (int)(grid.ActualWidth / cellWidth));
        var cols = Math.Clamp((int)Math.Round(_startCols + dx / cellWidth), 1, maxCols);
        var rows = Math.Clamp((int)Math.Round(_startRows + dy / cellHeight), 1, 3);
        if (cols != _tile.ColSpan || rows != _tile.RowSpan)
        {
            _tile.SetSpans(cols, rows);
            grid.RefreshSpans(_tile); // live re-flow while dragging
        }
        return true;
    }

    /// <summary>Finish the drag; returns the tile and whether it was a real drag (vs. a click).</summary>
    public (EntityTileViewModel? Tile, bool Moved) End(UIElement? grip, PointerRoutedEventArgs e)
    {
        var result = (_tile, _moved);
        _tile = null;
        grip?.ReleasePointerCapture(e.Pointer);
        return result;
    }
}
