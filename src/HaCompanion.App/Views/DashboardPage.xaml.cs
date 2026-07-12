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

    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            ViewModel.Catalog.CycleTileSize(tile); // TileSizeChanged re-flows this grid
    }

    // ----- manual tile reorder (built-in GridView reorder doesn't work on a
    //       VariableSizedWrapGrid, so drag-and-drop is handled explicitly) -----

    private EntityTileViewModel? _draggedTile;

    private void PinnedGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedTile = e.Items.FirstOrDefault() as EntityTileViewModel;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void PinnedGrid_DragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = _draggedTile is null
            ? DataPackageOperation.None
            : DataPackageOperation.Move;

    private void PinnedGrid_Drop(object sender, DragEventArgs e)
    {
        var dragged = _draggedTile;
        _draggedTile = null;
        if (dragged is null)
            return;

        var source = ViewModel.Catalog.Pinned;
        var from = source.IndexOf(dragged);
        if (from < 0)
            return;

        var to = TileDragHelper.InsertionIndex(PinnedGrid, e.GetPosition(PinnedGrid), source.Count);
        if (to > from)
            to--;
        to = Math.Clamp(to, 0, source.Count - 1);
        if (to != from)
            source.Move(from, to); // persisted via the catalog's CollectionChanged handler
    }
}
