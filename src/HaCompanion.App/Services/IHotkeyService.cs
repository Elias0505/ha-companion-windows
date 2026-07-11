// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Xaml;

namespace HaCompanion.App.Services;

/// <summary>Registers a global system hotkey (default Win+Ctrl+H) and raises an event when pressed.</summary>
public interface IHotkeyService
{
    event EventHandler? HotkeyPressed;

    /// <summary>True if the global hotkey was successfully registered with Windows.</summary>
    bool IsRegistered { get; }

    /// <summary>Hook the given window's message loop and register the default hotkey.</summary>
    void Initialize(Window window);

    void Unregister();
}
