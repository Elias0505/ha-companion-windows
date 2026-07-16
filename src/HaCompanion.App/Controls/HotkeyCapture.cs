// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace HaCompanion.App.Controls;

/// <summary>
/// Shared key-capture logic for "record a hotkey" buttons: turns a KeyDown into a combo
/// string like "Ctrl+Alt+K". Lone modifiers and unregistrable keys keep the capture open.
/// </summary>
public static class HotkeyCapture
{
    public enum Result
    {
        /// <summary>Not a usable combo yet (lone modifier / unsupported key) — keep recording.</summary>
        Pending,

        /// <summary>Escape pressed — abort the recording.</summary>
        Cancelled,

        /// <summary>A full modifier+key combo was captured (in <c>combo</c>).</summary>
        Captured,
    }

    public static Result Handle(KeyRoutedEventArgs e, out string combo)
    {
        combo = string.Empty;
        var key = e.Key;

        // Ignore lone modifier presses — keep waiting for the real key.
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return Result.Pending;

        e.Handled = true;

        if (key == VirtualKey.Escape)
            return Result.Cancelled;

        var mainKey = KeyToken(key);
        if (mainKey is null)
            return Result.Pending; // key we can't register — keep waiting

        var mods = new List<string>();
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods.Add("Win");
        if (IsDown(VirtualKey.Control)) mods.Add("Ctrl");
        if (IsDown(VirtualKey.Menu)) mods.Add("Alt");
        if (IsDown(VirtualKey.Shift)) mods.Add("Shift");

        if (mods.Count == 0)
            return Result.Pending; // a bare key can't be a global hotkey

        combo = string.Join("+", mods) + "+" + mainKey;
        return Result.Captured;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Maps a virtual key to a token the hotkey parser understands (A–Z, 0–9, Space, F1–F12).</summary>
    private static string? KeyToken(VirtualKey key)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
            return key.ToString();
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return ((int)(key - VirtualKey.Number0)).ToString(CultureInfo.InvariantCulture);
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return ((int)(key - VirtualKey.NumberPad0)).ToString(CultureInfo.InvariantCulture);
        if (key == VirtualKey.Space)
            return "Space";
        if (key >= VirtualKey.F1 && key <= VirtualKey.F12)
            return "F" + ((int)(key - VirtualKey.F1) + 1);
        return null;
    }
}
