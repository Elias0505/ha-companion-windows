// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaCompanion.App.Views;

/// <summary>
/// The "Mein PC" tab: live PC status, local notification rules ("benachrichtige mich,
/// wenn ..."), HA→PC command permissions and the received-notifications history.
/// </summary>
public sealed partial class MyPcPage : Page
{
    public MyPcViewModel ViewModel { get; }

    public MyPcPage()
    {
        ViewModel = App.Services.GetRequiredService<MyPcViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        // "nothing received yet" hint tracks the history live
        UpdateRxEmpty();
        ViewModel.History.CollectionChanged += (_, _) => UpdateRxEmpty();
    }

    private void UpdateRxEmpty() =>
        RxEmptyText.Visibility = ViewModel.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ----- entity search (same trio as the shortcuts page) -----

    private void EntityBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.Search(sender.Text);
    }

    private void EntityBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EntityTileViewModel tile)
        {
            ViewModel.AssignEntity(tile);
            sender.Text = tile.FriendlyName;
        }
    }

    private void EntityBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var tile = args.ChosenSuggestion as EntityTileViewModel
                   ?? ViewModel.Search(args.QueryText).FirstOrDefault(t =>
                       string.Equals(t.FriendlyName, args.QueryText, StringComparison.OrdinalIgnoreCase))
                   ?? ViewModel.Search(args.QueryText).FirstOrDefault();
        if (tile is not null)
        {
            ViewModel.AssignEntity(tile);
            sender.Text = tile.FriendlyName;
        }
    }

    // ----- rule list -----

    private void RuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: NotifyRuleItemViewModel item } toggle)
            ViewModel.SetRuleEnabled(item, toggle.IsOn);
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NotifyRuleItemViewModel item)
            ViewModel.RemoveRuleCommand.Execute(item);
    }
}
