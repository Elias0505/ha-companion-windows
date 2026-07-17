// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using HaCompanion.Core.Automations;
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
        // The trigger button face + process box are set imperatively, so re-sync them
        // whenever the builder is (re)seeded — e.g. when editing an existing rule.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutomationsViewModel.SelectedTrigger) or nameof(AutomationsViewModel.IsEditing))
                SyncBuilderFace();
        };
    }

    private static LocalizationService Loc => App.Services.GetRequiredService<LocalizationService>();

    private void SyncBuilderFace()
    {
        var trigger = ViewModel.SelectedTrigger;
        TriggerIcon.Glyph = trigger?.Glyph ?? "";
        TriggerLabel.Text = trigger?.Label ?? Loc["Au_PickTrigger"];
        ProcessBox.Text = ViewModel.ProcessParam;
    }

    // ----- WENN: trigger picker -----

    private void TriggerPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TriggerOption option)
            return;
        ViewModel.SelectedTrigger = option; // SyncBuilderFace runs via PropertyChanged
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

    // ----- NUR WENN: condition rows -----

    private Flyout? _openCondFlyout;

    private void AddCondTime_Click(object sender, RoutedEventArgs e) => ViewModel.AddCondition(RuleCondition.TypeTime);
    private void AddCondPc_Click(object sender, RoutedEventArgs e) => ViewModel.AddCondition(RuleCondition.TypePc);
    private void AddCondNumeric_Click(object sender, RoutedEventArgs e) => ViewModel.AddCondition(RuleCondition.TypeNumeric);
    private void AddCondEntity_Click(object sender, RoutedEventArgs e) => ViewModel.AddCondition(RuleCondition.TypeEntity);

    private void RemoveCond_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConditionRowViewModel row)
            ViewModel.RemoveConditionRowCommand.Execute(row);
    }

    private void CondEntityFlyout_Opened(object? sender, object e) => _openCondFlyout = sender as Flyout;

    private void CondRowEntity_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = ViewModel.Catalog.SearchTiles(sender.Text, actionableOnly: false);
    }

    private void CondRowEntity_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EntityTileViewModel tile
            && (sender as FrameworkElement)?.DataContext is ConditionRowViewModel row)
        {
            row.EntityTile = tile;
            ViewModel.NotifyBuilderChanged();
            _openCondFlyout?.Hide();
        }
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

    private static AutomationItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as AutomationItemViewModel;

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            ViewModel.BeginEditCommand.Execute(item);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            ViewModel.RunTestCommand.Execute(item);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            ViewModel.DuplicateCommand.Execute(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc["Au_DeleteTitle"],
            Content = Loc["Au_DeleteBody"],
            PrimaryButtonText = Loc["Au_Delete"],
            CloseButtonText = Loc["Au_Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.RemoveCommand.Execute(item);
    }
}
