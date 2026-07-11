// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml;

namespace HaCompanion.App.Services;

/// <summary>Registers a configurable global system hotkey and raises an event when pressed.</summary>
public interface IHotkeyService
{
    event EventHandler? HotkeyPressed;

    /// <summary>True if the global hotkey was successfully registered with Windows.</summary>
    bool IsRegistered { get; }

    /// <summary>Hook the given window's message loop (call once at startup).</summary>
    void Initialize(Window window);

    /// <summary>(Re)register a hotkey from a combo string like "Win+Ctrl+H". Returns success.</summary>
    bool Register(string combo);

    void Unregister();
}
