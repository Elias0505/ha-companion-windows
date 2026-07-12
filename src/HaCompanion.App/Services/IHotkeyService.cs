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

    /// <summary>Raised with the action key when one of the extra action hotkeys is pressed.</summary>
    event EventHandler<string>? ActionPressed;

    /// <summary>
    /// Register an additional global hotkey bound to an action key (e.g. an entity id).
    /// Returns false when the combo is invalid or already taken system-wide.
    /// </summary>
    bool RegisterAction(string combo, string actionKey);

    /// <summary>Unregister all action hotkeys (the main panel hotkey stays).</summary>
    void ClearActions();
}
