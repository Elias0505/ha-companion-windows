// SPDX-License-Identifier: AGPL-3.0-only
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
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Catalog.IsEditing = !ViewModel.Catalog.IsEditing;
        UpdateEditIcon();
    }

    private void UpdateEditIcon() =>
        EditIcon.Glyph = ViewModel.Catalog.IsEditing ? "" : ""; // CheckMark / Edit

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
}
