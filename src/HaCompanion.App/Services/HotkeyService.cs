// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
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

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ILogger<HotkeyService> _logger;
    private WndProc? _newProc; // held to prevent GC of the delegate
    private IntPtr _oldProc;
    private IntPtr _hwnd;

    public event EventHandler? HotkeyPressed;

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
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            IsRegistered = false;
        }
    }

    private static bool TryParse(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo))
            return false;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "win":
                case "windows":
                case "meta":
                    modifiers |= MOD_WIN;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                default:
                    return false;
            }
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
            vk = key[0];
        else if (key is "SPACE")
            vk = 0x20;
        else if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 12)
            vk = (uint)(0x70 + n - 1);
        else
            return false;

        return modifiers != 0;
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            // An exception must never escape into the window procedure — it would take the
            // whole process down. Log and carry on; the next press gets a fresh attempt.
            try
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
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
