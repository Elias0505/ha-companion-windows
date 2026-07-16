// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using HaCompanion.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="IHotkeyService"/>
/// <remarks>
/// Registers a global hotkey via the Win32 RegisterHotKey API and receives WM_HOTKEY
/// by subclassing the window procedure of the given window.
/// </remarks>
public sealed class HotkeyService : IHotkeyService
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB001;

    private const uint MOD_NOREPEAT = 0x4000;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ILogger<HotkeyService> _logger;
    private readonly Dictionary<int, string> _actionsByHotkeyId = new();
    private WndProc? _newProc; // held to prevent GC of the delegate
    private IntPtr _oldProc;
    private IntPtr _hwnd;
    private int _nextActionId = 0xB100; // panel hotkey stays at HOTKEY_ID

    public event EventHandler? HotkeyPressed;

    public event EventHandler<string>? ActionPressed;

    public bool IsRegistered { get; private set; }

    public HotkeyService(ILogger<HotkeyService> logger) => _logger = logger;

    public void Initialize(Window window)
    {
        if (_hwnd != IntPtr.Zero)
            return; // already hooked — never subclass twice

        _hwnd = WindowNative.GetWindowHandle(window);
        _newProc = HandleMessage;
        _oldProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));
    }

    public bool Register(string combo)
    {
        Unregister();
        if (_hwnd == IntPtr.Zero)
            return false;

        if (!TryParse(combo, out var modifiers, out var vk))
        {
            _logger.LogWarning("Could not parse hotkey '{Combo}'", combo);
            IsRegistered = false;
            return false;
        }

        IsRegistered = RegisterHotKey(_hwnd, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk);
        if (!IsRegistered)
            _logger.LogWarning("Could not register hotkey '{Combo}' (it may be reserved or already in use).", combo);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (IsRegistered)
        {
            _ = UnregisterHotKey(_hwnd, HOTKEY_ID);
            IsRegistered = false;
        }
    }

    public bool RegisterAction(string combo, string actionKey)
    {
        if (_hwnd == IntPtr.Zero || !TryParse(combo, out var modifiers, out var vk))
            return false;

        var id = _nextActionId++;
        if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
        {
            _logger.LogWarning("Could not register action hotkey '{Combo}' (reserved or already in use).", combo);
            return false;
        }
        _actionsByHotkeyId[id] = actionKey;
        return true;
    }

    public void ClearActions()
    {
        foreach (var id in _actionsByHotkeyId.Keys)
            _ = UnregisterHotKey(_hwnd, id);
        _actionsByHotkeyId.Clear();
    }

    private static bool TryParse(string combo, out uint modifiers, out uint vk) =>
        HotkeyCombo.TryParse(combo, out modifiers, out vk); // parsing rules live (tested) in Core

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            // An exception must never escape into the window procedure — it would take the
            // whole process down. Log and carry on; the next press gets a fresh attempt.
            try
            {
                var hotkeyId = unchecked((int)wParam.ToInt64());
                if (hotkeyId == HOTKEY_ID)
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                else if (_actionsByHotkeyId.TryGetValue(hotkeyId, out var actionKey))
                    ActionPressed?.Invoke(this, actionKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hotkey handler failed");
            }
        }
        return CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
