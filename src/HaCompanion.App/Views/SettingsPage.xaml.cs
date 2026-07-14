// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Controls;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HaCompanion.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _recordingHotkey;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        TokenBox.Password = ViewModel.Token; // one-time init; updates flow via PasswordChanged
        // The page is cached: re-sync the default-view picker on every visit — the quick
        // panel's pin button changes the stored value behind this page's back.
        Loaded += (_, _) => ViewModel.RefreshStartViewSelection();
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Token = TokenBox.Password;

    public bool IsNotBusy(bool isBusy) => !isBusy;

    // --- Custom hotkey capture: let the user press any Ctrl/Alt/Shift(+Win)+key combo ---

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        ViewModel.HotkeyStatus = ViewModel.RecordPrompt;
        RecordHotkeyButton.Focus(FocusState.Programmatic);
    }

    private void RecordHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recordingHotkey)
            return;
        _recordingHotkey = false; // clicked away without pressing a key — cancel
        ViewModel.RefreshHotkeyStatusPublic();
    }

    private void RecordHotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingHotkey)
            return;

        switch (HotkeyCapture.Handle(e, out var combo))
        {
            case HotkeyCapture.Result.Captured:
                _recordingHotkey = false;
                ViewModel.Hotkey = combo;
                break;
            case HotkeyCapture.Result.Cancelled:
                _recordingHotkey = false;
                ViewModel.RefreshHotkeyStatusPublic();
                break;
        }
    }
}
