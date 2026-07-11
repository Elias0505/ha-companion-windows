// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Runtime.InteropServices;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

/// <summary>
/// The Win+Ctrl+H quick panel: a borderless, always-on-top overlay pinned to the
/// right edge of the work area. The whole opaque panel slides in/out as one unit by
/// moving the window itself (like the Win11 notification centre), dismisses on focus
/// loss or Esc, and hosts the editable pinned-tile layout.
/// </summary>
/// <remarks>
/// Animation model: a single desired-state field (<see cref="_isOpen"/>) is the sole
/// source of truth. Every show/hide request just sets that target and (re)starts a slide
/// from the window's CURRENT position toward the target edge; the timer runs on pure
/// elapsed time and only stops at the target, so the panel always converges to its last
/// requested state no matter how fast the hotkey is spammed. Reentrancy (Show()/Activate()
/// pump the message loop, so a queued WM_HOTKEY can call back mid-setup) is contained by
/// <see cref="_inSetup"/>: nested calls only flip the target; the outer frame runs one slide.
///
/// Geometry is always taken from the monitor under the mouse cursor, using that monitor's
/// own effective DPI, and the window is positioned with absolute-pixel <c>SetWindowPos</c>
/// calls. This is deliberate: <c>AppWindow.Move</c> reinterprets its coordinates when the
/// window crosses a per-monitor-DPI boundary, which on a multi-monitor rig let each rapid
/// open/close walk the window onto the neighbouring display until it parked half-off. Fixing
/// the target monitor per open (never from the window's own drifted position) and moving in
/// raw screen pixels removes that feedback loop.
/// </remarks>
public sealed partial class QuickPanelWindow : Window
{
    private const int DefaultPanelWidthDip = 400;
    private const int AnimDurationMs = 200;

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    public QuickPanelViewModel ViewModel { get; }

    private readonly IntPtr _hwnd;
    private readonly ISettingsStore _settingsStore;
    private readonly DispatcherQueueTimer _animTimer;
    private readonly DispatcherQueueTimer _previewTimer;
    private Task? _webInitTask;
    private string _baseUrl = string.Empty;
    private int _panelWidthDip = DefaultPanelWidthDip;
    private bool _isOpen;        // desired end state (target), also the sole intent flag
    private bool _windowShown;   // whether the OS window is currently shown (vs. Hide())
    private bool _inSetup;       // reentrancy guard around the message-pumping Show()/Activate()
    private bool _previewing;

    // Slide geometry in absolute screen pixels, recomputed from the cursor's monitor per show.
    private int _winY, _winW, _winH, _restX, _offX;
    private int _animFromX, _animToX;
    private long _animStartMs;
    private bool _timerBoosted;

    public QuickPanelWindow(QuickPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
        InitializeComponent();
        RootGrid.DataContext = viewModel;
        ViewModel.DashboardRequested += OnDashboardRequested;

        Title = "HA Companion — Quick Panel";
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        ApplyNoBorder();

        // The opaque window is moved as a whole to slide the panel in/out — there is no
        // separate background layer to flash, and nothing lags behind the content. A short
        // high-resolution timer drives the eased motion.
        _animTimer = DispatcherQueue.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(6);
        _animTimer.Tick += OnAnimationTick;

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _previewing = false;
            HideAnimated();
        };

        Activated += OnActivated;
        AppWindow.Hide();
    }

    /// <summary>
    /// Recomputes the slide geometry from the primary display (the user's "main" monitor), using
    /// that monitor's effective DPI. Absolute screen pixels; the target monitor is fixed (never
    /// derived from the window's own, possibly drifted, position), so the panel always opens on
    /// the main display and repeated opens can't walk across displays.
    /// </summary>
    private void ComputeGeometry()
    {
        var mon = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi))
        {
            // Extremely unlikely; fall back to a primary-ish 1080p rectangle so we never divide
            // by a zero-sized work area.
            mi.rcWork = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }

        var dpi = 96u;
        if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            dpi = dpiX;

        var scale = dpi / 96.0;
        _winW = (int)Math.Round(_panelWidthDip * scale);
        _winY = mi.rcWork.Top;
        _winH = mi.rcWork.Bottom - mi.rcWork.Top;
        _restX = mi.rcWork.Right - _winW; // flush to the right edge of the cursor's monitor
        _offX = mi.rcWork.Right;          // just off that right edge
    }

    /// <summary>
    /// Live width preview: shows/resizes the panel at the current width setting (bypassing
    /// auto-hide) so the user sees the size while dragging the slider; auto-hides shortly after.
    /// </summary>
    public void PreviewWidth()
    {
        _previewing = true;
        _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
        if (!_windowShown)
        {
            SetTarget(true);
        }
        else
        {
            ComputeGeometry();
            MoveWindowPx(_restX, _winY, _winW, _winH);
            _isOpen = true;
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    public void Toggle() => SetTarget(!_isOpen);

    public void ShowAnimated() => SetTarget(true);

    public void HideAnimated() => SetTarget(false);

    /// <summary>
    /// Sets the desired open/closed state and drives the panel toward it. Safe to call at any
    /// time, from anywhere (including reentrantly), and any number of times in quick succession:
    /// the last call wins and the panel always finishes settling within one animation duration.
    /// </summary>
    private void SetTarget(bool open)
    {
        _isOpen = open;

        if (!open)
        {
            // Leaving the panel also leaves edit mode (layout is already persisted live).
            ViewModel.Catalog.IsEditing = false;
            UpdateEditIcon();
        }

        Log($"target open={open} shown={_windowShown} setup={_inSetup} x={SafePositionX()} rest={_restX} off={_offX}");

        // Bring the OS window up (once) before the first slide-in. Show()/Activate() pump the
        // message loop, so a queued hotkey can reenter SetTarget here — the guard makes that
        // reentrant call only update the target and return; this outer frame starts the slide.
        if (open && !_windowShown && !_inSetup)
        {
            _inSetup = true;
            try
            {
                _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
                ComputeGeometry();
                MoveWindowPx(_offX, _winY, _winW, _winH); // park just off the right edge
                _windowShown = true;
                AppWindow.Show();
                Activate();
                ApplyNoBorder();
            }
            finally
            {
                _inSetup = false;
            }

            _ = ViewModel.EnsureDashboardsAsync();
            // FocusState.Pointer focuses the first control WITHOUT drawing the keyboard focus
            // rectangle, which previously looked like a thin "selected" outline on the panel.
            if (FocusManager.FindFirstFocusableElement(RootGrid) is Control focusable)
                focusable.Focus(FocusState.Pointer);
        }

        if (_inSetup)
            return; // reentrant call: the outer frame will start the slide toward the final target

        StartOrRetargetSlide();
    }

    /// <summary>
    /// (Re)aims the slide at the current target edge, always starting from the window's actual
    /// present position so an interrupted slide reverses smoothly instead of jumping.
    /// </summary>
    private void StartOrRetargetSlide()
    {
        _animToX = _isOpen ? _restX : _offX;
        _animFromX = SafePositionX();
        _animStartMs = Environment.TickCount64;
        if (!_timerBoosted)
        {
            TimeBeginPeriod(1); // request 1 ms timer cadence for a smooth slide
            _timerBoosted = true; // paired with TimeEndPeriod when the slide completes
        }
        _animTimer.Stop();
        _animTimer.Start();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = Environment.TickCount64 - _animStartMs;
        var t = Math.Clamp(elapsed / (double)AnimDurationMs, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - t, 3.0); // ease-out cubic
        var x = (int)Math.Round(_animFromX + (_animToX - _animFromX) * eased);
        MovePx(x);

        if (t < 1.0)
            return;

        // Reached the target: stop the clock and snap exactly onto the target edge so rounding
        // can never leave a one-pixel sliver behind.
        _animTimer.Stop();
        if (_timerBoosted)
        {
            TimeEndPeriod(1);
            _timerBoosted = false;
        }
        MovePx(_animToX);

        if (!_isOpen)
        {
            _windowShown = false;
            AppWindow.Hide();
        }
        Log($"settled open={_isOpen} x={_animToX} hidden={!_isOpen}");
    }

    private void ApplyNoBorder()
    {
        // Paint the 1 px Win11 window outline in the panel's own dark colour so it can never
        // read as a thin white "selected" frame; keep crisp (non-rounded) corners.
        var borderColor = 0x00202020; // COLORREF 0x00BBGGRR ≈ dark panel base
        DwmSetWindowAttribute(_hwnd, 34, ref borderColor, sizeof(int));
        var doNotRound = 1;
        DwmSetWindowAttribute(_hwnd, 33, ref doNotRound, sizeof(int));
    }

    private void MoveWindowPx(int x, int y, int w, int h) =>
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    private void MovePx(int x) => MoveWindowPx(x, _winY, _winW, _winH);

    private int SafePositionX()
    {
        if (GetWindowRect(_hwnd, out var r))
            return r.Left;
        return _isOpen ? _offX : _restX;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && _isOpen
            && !_previewing
            && _settingsStore.Load().AutoHideQuickPanel)
        {
            HideAnimated();
        }
    }

    // ----- embedded HA dashboard (WebView) -----

    private async void OnDashboardRequested(object? sender, QuickDashboard dashboard)
    {
        try
        {
            await EnsureWebAsync();
            // Empty url_path = the default dashboard → navigate to the site root.
            var url = string.IsNullOrEmpty(dashboard.UrlPath) ? _baseUrl : $"{_baseUrl}/{dashboard.UrlPath}";
            PanelWeb.CoreWebView2?.Navigate(url);
        }
        catch
        {
            // If the WebView runtime is missing the panel simply stays on Favourites.
        }
    }

    private Task EnsureWebAsync() => _webInitTask ??= InitWebAsync();

    private async Task InitWebAsync()
    {
        var settings = _settingsStore.Load();
        _baseUrl = settings.BaseUrl.TrimEnd('/');

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion", "WebView2Panel");
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, userDataFolder, new CoreWebView2EnvironmentOptions());
        await PanelWeb.EnsureCoreWebView2Async(env);

        if (settings.IgnoreCertificateErrors)
        {
            PanelWeb.CoreWebView2.ServerCertificateErrorDetected += (_, e) =>
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }

        await PanelWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            HaWebViewHelper.BuildAuthScript(_baseUrl, settings.Token));
        await PanelWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            HaWebViewHelper.HideChromeScript);
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HideAnimated();
    }

    // ----- edit mode -----

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Catalog.IsEditing = !ViewModel.Catalog.IsEditing;
        UpdateEditIcon();
    }

    private void UpdateEditIcon()
    {
        var editing = ViewModel.Catalog.IsEditing;
        EditIcon.Glyph = editing ? "" : ""; // check / pencil
        var label = App.Services.GetRequiredService<LocalizationService>()[editing ? "Tip_Done" : "Tip_Edit"];
        ToolTipService.SetToolTip(EditButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EditButton, label);
    }

    private void AddTileButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ViewModel.Catalog.FilterCandidates(string.Empty);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.Catalog.FilterCandidates(SearchBox.Text);

    private void SearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntityTileViewModel tile)
        {
            ViewModel.Catalog.TogglePin(tile);
            ViewModel.Catalog.FilterCandidates(SearchBox.Text);
        }
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            ViewModel.Catalog.TogglePin(tile);
    }

    private async void Pinned_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.Catalog.IsEditing)
            return; // in edit mode taps are for arranging, not switching
        if (e.ClickedItem is EntityTileViewModel tile)
            await tile.ToggleCommand.ExecuteAsync(null);
    }

    // ----- diagnostics -----

    /// <summary>
    /// Appends a timestamped line to %LOCALAPPDATA%\HaCompanion\panel.log. Best-effort and
    /// never throws — used to capture the show/hide/settle sequence when reproducing the
    /// rapid-toggle behaviour. Kept tiny (two events per open/close cycle).
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaCompanion");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "panel.log"), $"{Environment.TickCount64,12} {message}\n");
        }
        catch
        {
            // diagnostics only — losing a log line must never affect the panel
        }
    }

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
