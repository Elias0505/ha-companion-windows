// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Controls;
using global::Windows.ApplicationModel.DataTransfer;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        SmoothScroll.Attach(PageScroll);
        UpdateEditIcon();
        // Size changes made in the quick panel must re-flow this grid too (shared tiles).
        ViewModel.Catalog.TileSizeChanged += (_, tile) => PinnedGrid.RefreshSpans(tile);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Catalog.IsEditing = !ViewModel.Catalog.IsEditing;
        UpdateEditIcon();
    }

    private void UpdateEditIcon()
    {
        var editing = ViewModel.Catalog.IsEditing;
        EditIcon.Glyph = editing ? "\uE73E" : "\uE70F"; // CheckMark / Edit
        var label = App.Services.GetRequiredService<LocalizationService>()[editing ? "Tip_Done" : "Tip_Edit"];
        ToolTipService.SetToolTip(EditButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EditButton, label);
    }

    private void AddTileButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ViewModel.Catalog.FilterCandidates(string.Empty);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.Catalog.FilterCandidates(SearchBox.Text);

    private void SearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntityTileViewModel tile)
        {
            ViewModel.Catalog.TogglePin(tile);
            ViewModel.Catalog.FilterCandidates(SearchBox.Text);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile && !tile.IsPinned)
            ViewModel.Catalog.TogglePin(tile);
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            ViewModel.Catalog.TogglePin(tile);
    }

    private async void Pinned_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Catalog.IsEditing)
            return;
        if (e.ClickedItem is EntityTileViewModel tile)
            await tile.ToggleCommand.ExecuteAsync(null);
    }


    // ----- tile context flyout (stage-2 controls: brightness / temperature / media) -----

    private void TileFlyout_Opening(object sender, object e)
    {
        // No controls for this domain (switch, script, sensor, ...): don't show an empty flyout.
        if (sender is Flyout flyout && flyout.Target is FrameworkElement fe
            && fe.DataContext is EntityTileViewModel tile
            && !(tile.HasBrightness || tile.HasClimate || tile.HasMedia))
            flyout.Hide();
    }

    private void BrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Programmatic updates (WebSocket refresh) leave the slider unfocused — only a user
        // interaction may fire the service call, otherwise every state echo would re-send.
        if (sender is Slider slider && slider.FocusState != FocusState.Unfocused
            && slider.DataContext is EntityTileViewModel tile)
            tile.SetBrightness(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState != FocusState.Unfocused
            && slider.DataContext is EntityTileViewModel tile)
            tile.SetVolume(e.NewValue);
    }

    private void TempDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.NudgeTemperature(-0.5);
    }

    private void TempUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.NudgeTemperature(+0.5);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.PlayPause();
    }

    // ----- hand-drag tile resize (corner grip; click still cycles the presets) -----

    private const double TileCellWidth = 160;  // must match the VariableSizedWrapGrid ItemWidth
    private const double TileCellHeight = 116; // must match the VariableSizedWrapGrid ItemHeight

    private readonly TileResizeDrag _tileResize = new();

    private void ResizeGripTile_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
        {
            _tileResize.Begin((UIElement)sender, e, tile, TileCellWidth, TileCellHeight);
            e.Handled = true; // keep the press from starting an item drag
        }
    }

    private void ResizeGripTile_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_tileResize.Update(e))
            e.Handled = true;
    }

    private void ResizeGripTile_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var (tile, moved) = _tileResize.End((UIElement)sender, e);
        if (tile is null)
            return;
        if (moved)
            ViewModel.Catalog.SetTileSpans(tile, tile.ColSpan, tile.RowSpan); // persist the dragged size
        else
            ViewModel.Catalog.CycleTileSize(tile); // plain click: next preset
        e.Handled = true;
    }

    private void ResizeGripTile_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var (tile, moved) = _tileResize.End(null, e);
        if (tile is not null && moved)
            ViewModel.Catalog.SetTileSpans(tile, tile.ColSpan, tile.RowSpan);
    }

    // ----- manual tile reorder (built-in GridView reorder doesn't work on a
    //       VariableSizedWrapGrid, so drag-and-drop is handled explicitly). While the
    //       drag is in flight the other tiles rearrange reactively: every DragOver maps
    //       the pointer to an insertion slot and live-moves the dragged tile there. -----

    private EntityTileViewModel? _draggedTile;
    private long _lastLiveMoveMs;

    private void PinnedGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedTile = e.Items.FirstOrDefault() as EntityTileViewModel;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void PinnedGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        _draggedTile = null; // also covers drops outside the grid

    private void PinnedGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTile is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        LiveReorder(e.GetPosition(PinnedGrid));
    }

    private void PinnedGrid_Drop(object sender, DragEventArgs e)
    {
        // Final settle (usually a no-op — the tile was already live-moved while dragging).
        if (_draggedTile is not null)
        {
            _lastLiveMoveMs = 0; // bypass the debounce for the definitive drop position
            LiveReorder(e.GetPosition(PinnedGrid));
        }
        _draggedTile = null;
    }

    /// <summary>
    /// Move the dragged tile to the slot under the pointer so the grid re-flows while the drag
    /// is still in progress. Debounced: right after a move the geometry shifts under the pointer,
    /// and recomputing immediately would oscillate between the old and new slot.
    /// </summary>
    private void LiveReorder(global::Windows.Foundation.Point position)
    {
        if (Environment.TickCount64 - _lastLiveMoveMs < 150)
            return;

        var source = ViewModel.Catalog.Pinned;
        var from = source.IndexOf(_draggedTile!);
        if (from < 0)
            return;

        var to = TileDragHelper.InsertionIndex(PinnedGrid, position, source.Count);
        if (to > from)
            to--;
        to = Math.Clamp(to, 0, source.Count - 1);
        if (to != from)
        {
            source.Move(from, to); // persisted via the catalog's CollectionChanged handler
            _lastLiveMoveMs = Environment.TickCount64;
        }
    }
}
