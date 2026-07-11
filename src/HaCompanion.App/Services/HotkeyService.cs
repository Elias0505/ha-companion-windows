// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="IHotkeyService"/>
/// <remarks>
/// Registers Win+Ctrl+H via the Win32 RegisterHotKey API and receives WM_HOTKEY by
/// subclassing the window procedure of the given window.
/// </remarks>
public sealed class HotkeyService : IHotkeyService
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB001;

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48;

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
        _hwnd = WindowNative.GetWindowHandle(window);
        _newProc = HandleMessage;
        _oldProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));

        IsRegistered = RegisterHotKey(_hwnd, HOTKEY_ID, MOD_WIN | MOD_CONTROL | MOD_NOREPEAT, VK_H);
        if (!IsRegistered)
            _logger.LogWarning("Could not register global hotkey Win+Ctrl+H (it may be reserved by Windows).");
    }

    public void Unregister()
    {
        if (IsRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            IsRegistered = false;
        }
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && (int)wParam == HOTKEY_ID)
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
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
