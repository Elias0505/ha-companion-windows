// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

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

        var key = e.Key;
        // Ignore lone modifier presses — keep waiting for the real key.
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;

        e.Handled = true;

        if (key == VirtualKey.Escape)
        {
            _recordingHotkey = false;
            ViewModel.RefreshHotkeyStatusPublic();
            return;
        }

        var mainKey = KeyToken(key);
        if (mainKey is null)
            return; // key we can't register — keep waiting

        var mods = new List<string>();
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods.Add("Win");
        if (IsDown(VirtualKey.Control)) mods.Add("Ctrl");
        if (IsDown(VirtualKey.Menu)) mods.Add("Alt");
        if (IsDown(VirtualKey.Shift)) mods.Add("Shift");

        if (mods.Count == 0)
            return; // a bare key can't be a global hotkey — keep waiting for a modifier

        _recordingHotkey = false;
        ViewModel.Hotkey = string.Join("+", mods) + "+" + mainKey;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Maps a virtual key to a token the hotkey parser understands (A–Z, 0–9, Space, F1–F12).</summary>
    private static string? KeyToken(VirtualKey key)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
            return key.ToString();
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return ((int)(key - VirtualKey.Number0)).ToString();
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return ((int)(key - VirtualKey.NumberPad0)).ToString();
        if (key == VirtualKey.Space)
            return "Space";
        if (key >= VirtualKey.F1 && key <= VirtualKey.F12)
            return "F" + ((int)(key - VirtualKey.F1) + 1);
        return null;
    }
}
