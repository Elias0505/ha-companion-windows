// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Controls;

/// <summary>
/// A horizontal, scrollable bar of category chips ("All" + one per domain present).
/// Host binds its <see cref="FrameworkElement.DataContext"/> to a
/// <see cref="DeviceBrowserViewModel"/>; tapping a chip re-filters that browser.
/// </summary>
public sealed partial class CategoryFilterBar : UserControl
{
    public CategoryFilterBar() => InitializeComponent();

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceBrowserViewModel vm
            && (sender as FrameworkElement)?.DataContext is CategoryChipViewModel chip)
            vm.SelectCategory(chip);
    }
}
