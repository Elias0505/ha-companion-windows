// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HaCompanion.App.Views;

/// <summary>
/// The Automationen tab: an n8n-inspired flow builder (WENN node → optional condition
/// node → DANN action cards) plus the list of existing rules as flow cards with live
/// state dots. All flyout plumbing lives here; the logic is in AutomationsViewModel.
/// </summary>
public sealed partial class AutomationsPage : Page
{
    public AutomationsViewModel ViewModel { get; }

    private Flyout? _openEntityFlyout; // the draft picker currently on screen (to close it)

    public AutomationsPage()
    {
        ViewModel = App.Services.GetRequiredService<AutomationsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private static LocalizationService Loc => App.Services.GetRequiredService<LocalizationService>();

    // ----- WENN: trigger picker -----

    private void TriggerPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TriggerOption option)
            return;
        ViewModel.SelectedTrigger = option;
        TriggerIcon.Glyph = option.Glyph;
        TriggerLabel.Text = option.Label;
        TriggerFlyout.Hide();
    }

    private void ProcessBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.ProcessParam = sender.Text;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = AutomationsViewModel.RunningProcessNames(sender.Text);
    }

    private void ProcessBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string name)
            ViewModel.ProcessParam = name;
    }

    // ----- NUR WENN: condition editor -----

    private void ConditionNode_Click(object sender, RoutedEventArgs e) =>
        ConditionFlyout.ShowAt((FrameworkElement)sender);

    private void CondType_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return; // fires during InitializeComponent
        ViewModel.ConditionIsTime = ReferenceEquals(sender, CondTypeTime);
    }

    private void CondOn_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ConditionWantedOn = true;
        CondOnChip.IsChecked = true;
        CondOffChip.IsChecked = false;
    }

    private void CondOff_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ConditionWantedOn = false;
        CondOnChip.IsChecked = false;
        CondOffChip.IsChecked = true;
    }

    private void CondEntityBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.Catalog.SearchTiles(sender.Text, actionableOnly: false);
    }

    private void CondEntityBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EntityTileViewModel tile)
        {
            ViewModel.ConditionTile = tile;
            sender.Text = tile.FriendlyName;
        }
    }

    private void CondApply_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.HasCondition = true;
        ViewModel.NotifyBuilderChanged();
        ConditionFlyout.Hide();
    }

    // ----- DANN: entity pickers + action chips -----

    private void EntityFlyout_Opened(object? sender, object e) => _openEntityFlyout = sender as Flyout;

    private static ActionDraftViewModel? DraftOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ActionDraftViewModel;

    private void EntityBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.Search(sender.Text);
    }

    private void EntityBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EntityTileViewModel tile && DraftOf(sender) is { } draft)
        {
            ViewModel.AssignEntity(draft, tile);
            _openEntityFlyout?.Hide();
        }
    }

    private void EntityBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (DraftOf(sender) is not { } draft)
            return;
        var tile = args.ChosenSuggestion as EntityTileViewModel
                   ?? ViewModel.Search(args.QueryText).FirstOrDefault(t =>
                       string.Equals(t.FriendlyName, args.QueryText, StringComparison.OrdinalIgnoreCase))
                   ?? (ViewModel.Search(args.QueryText) is { Count: > 0 } results ? results[0] : null);
        if (tile is not null)
        {
            ViewModel.AssignEntity(draft, tile);
            _openEntityFlyout?.Hide();
        }
    }

    private void ActionChips_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list && DraftOf(sender) is { } draft)
            list.SelectedItem = draft.SelectedAction;
    }

    private void ActionChips_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && DraftOf(sender) is { } draft
            && list.SelectedItem is ActionOption option)
        {
            draft.SelectedAction = option;
            ViewModel.NotifyBuilderChanged();
        }
    }

    private void RemoveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (DraftOf(sender) is { } draft)
            ViewModel.RemoveActionDraftCommand.Execute(draft);
    }

    // ----- rule list -----

    private void RuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: AutomationItemViewModel item } toggle)
            ViewModel.SetEnabled(item, toggle.IsOn);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AutomationItemViewModel item)
            ViewModel.RemoveCommand.Execute(item);
    }

    private void CtxToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AutomationItemViewModel item)
            ViewModel.SetEnabled(item, !item.Rule.IsEnabled);
    }

    private void CtxDelete_Click(object sender, RoutedEventArgs e) => Remove_Click(sender, e);

    // ----- quick pick -----

    private void QuickPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            ViewModel.AssignEntityToNextFreeDraft(tile);
    }
}
