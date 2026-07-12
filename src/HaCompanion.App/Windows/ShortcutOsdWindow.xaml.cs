// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

/// <summary>
/// The small shortcut OSD toast in the bottom-right corner of the primary display: shown
/// (without stealing focus) when an entity shortcut fires, it slides in from the right edge,
/// holds briefly and slides back out — the same single-target animation model as the quick
/// panel, so rapid triggers just retarget the slide and refresh the content.
/// </summary>
public sealed partial class ShortcutOsdWindow : Window
{
    private const int ToastWidthDip = 300;
    private const int ToastHeightDip = 68;
    private const int MarginDip = 20;
    private const int AnimDurationMs = 180;
    private const int HoldMs = 1600;

    private static readonly SolidColorBrush AccentBrush = new(Color.FromArgb(255, 10, 132, 255));

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _animTimer;
    private readonly DispatcherQueueTimer _holdTimer;

    private bool _isOpen;      // desired end state
    private bool _windowShown; // whether the OS window is currently shown
    private int _winY, _winW, _winH, _restX, _offX;
    private int _animFromX, _animToX;
    private long _animStartMs;
    private bool _timerBoosted;

    public ShortcutOsdWindow()
    {
        InitializeComponent();
        Title = "HA Companion — Shortcut";
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Rounded Win11 corners + a dark outline so the toast reads as a native flyout.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        var borderColor = 0x00202020;
        DwmSetWindowAttribute(_hwnd, 34, ref borderColor, sizeof(int));

        _animTimer = DispatcherQueue.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(6);
        _animTimer.Tick += OnAnimationTick;

        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(HoldMs);
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            _isOpen = false;
            StartSlide();
        };

        AppWindow.Hide();
    }

    /// <summary>Show (or refresh) the toast for a triggered shortcut. Never takes focus.</summary>
    public void ShowToast(string iconGlyph, string title, string subtitle)
    {
        OsdIcon.Glyph = iconGlyph;
        OsdTitle.Text = title;
        OsdSubtitle.Text = subtitle;
        IconCircle.Background = AccentBrush;

        ComputeGeometry();
        if (!_windowShown)
        {
            MoveWindowPx(_offX, _winY, _winW, _winH); // park just off the right edge
            AppWindow.Show(activateWindow: false);
            _windowShown = true;
        }
        _isOpen = true;
        StartSlide();
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void ComputeGeometry()
    {
        var mon = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi))
            mi.rcWork = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

        var dpi = 96u;
        if (GetDpiForMonitor(mon, 0, out var dpiX, out _) == 0 && dpiX > 0)
            dpi = dpiX;
        var scale = dpi / 96.0;

        _winW = (int)Math.Round(ToastWidthDip * scale);
        _winH = (int)Math.Round(ToastHeightDip * scale);
        var margin = (int)Math.Round(MarginDip * scale);
        _winY = mi.rcWork.Bottom - _winH - margin;
        _restX = mi.rcWork.Right - _winW - margin;
        _offX = mi.rcWork.Right;
    }

    private void StartSlide()
    {
        _animToX = _isOpen ? _restX : _offX;
        _animFromX = GetWindowRect(_hwnd, out var r) ? r.Left : _offX;
        _animStartMs = Environment.TickCount64;
        if (!_timerBoosted)
        {
            TimeBeginPeriod(1);
            _timerBoosted = true;
        }
        _animTimer.Stop();
        _animTimer.Start();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var t = Math.Clamp((Environment.TickCount64 - _animStartMs) / (double)AnimDurationMs, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - t, 3.0);
        MoveWindowPx((int)Math.Round(_animFromX + (_animToX - _animFromX) * eased), _winY, _winW, _winH);

        if (t < 1.0)
            return;

        _animTimer.Stop();
        if (_timerBoosted)
        {
            TimeEndPeriod(1);
            _timerBoosted = false;
        }
        if (!_isOpen)
        {
            _windowShown = false;
            AppWindow.Hide();
        }
    }

    private void MoveWindowPx(int x, int y, int w, int h) =>
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h,
            0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);
}
