// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Controls;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HaCompanion.App.Views;

/// <summary>
/// The Shortcuts tab: bind any key combination to a device or script. Entities are picked
/// via a suggestion search, the combo is captured live from the keyboard, and each stored
/// shortcut shows whether its registration succeeded system-wide.
/// </summary>
public sealed partial class ShortcutsPage : Page
{
    private bool _recording;

    public ShortcutsViewModel ViewModel { get; }

    public ShortcutsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShortcutsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        UpdateRecordLabel();
    }

    private LocalizationService Loc => App.Services.GetRequiredService<LocalizationService>();

    private void UpdateRecordLabel() =>
        RecordButton.Content = _recording
            ? Loc["Set_RecordPrompt"]
            : ViewModel.CapturedCombo ?? Loc["Sc_Record"];

    // ----- entity search -----

    private void EntityBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        ViewModel.SelectedTile = null; // typing invalidates the previous pick
        sender.ItemsSource = ViewModel.Search(sender.Text);
    }

    private void EntityBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EntityTileViewModel tile)
        {
            ViewModel.SelectedTile = tile;
            sender.Text = tile.FriendlyName;
        }
    }

    // ----- combo recording (same capture logic as the settings page) -----

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        _recording = true;
        UpdateRecordLabel();
        RecordButton.Focus(FocusState.Programmatic);
    }

    private void RecordButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recording)
            return;
        _recording = false; // clicked away — cancel
        UpdateRecordLabel();
    }

    private void RecordButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
            return;

        switch (HotkeyCapture.Handle(e, out var combo))
        {
            case HotkeyCapture.Result.Captured:
                _recording = false;
                ViewModel.CapturedCombo = combo;
                UpdateRecordLabel();
                break;
            case HotkeyCapture.Result.Cancelled:
                _recording = false;
                UpdateRecordLabel();
                break;
        }
    }

    // ----- add / remove -----

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddCommand.Execute(null);
        EntityBox.Text = string.Empty;
        UpdateRecordLabel(); // captured combo was consumed
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutItemViewModel item)
            ViewModel.RemoveCommand.Execute(item);
    }
}
