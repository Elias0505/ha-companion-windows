// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Controls;

/// <summary>
/// A category-filtered, tappable device list. Host binds its
/// <see cref="FrameworkElement.DataContext"/> to a <see cref="DeviceBrowserViewModel"/>
/// and handles <see cref="EntityInvoked"/> (select, add-as-action, …). Tapping a tile
/// never mutates HA — the host decides what a tap means on its page.
/// </summary>
public sealed partial class DeviceBrowser : UserControl
{
    public DeviceBrowser()
    {
        InitializeComponent();
        UpdateHintVisibility();
    }

    /// <summary>Raised when a device tile is tapped, with its entity tile.</summary>
    public event EventHandler<EntityTileViewModel>? EntityInvoked;

    /// <summary>Optional one-line hint shown above the filter bar (empty = hidden).</summary>
    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText), typeof(string), typeof(DeviceBrowser),
        new PropertyMetadata(string.Empty, OnHintChanged));

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DeviceBrowser)d).UpdateHintVisibility();

    // The UserControl root can't be x:Name'd (it carries x:Class), so we can't bind
    // HintBlock via ElementName — push the text/visibility from here instead.
    private void UpdateHintVisibility()
    {
        HintBlock.Text = HintText ?? string.Empty;
        HintBlock.Visibility = string.IsNullOrEmpty(HintText) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            EntityInvoked?.Invoke(this, tile);
    }
}
