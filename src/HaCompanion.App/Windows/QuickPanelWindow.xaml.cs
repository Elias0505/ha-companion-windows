// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using HaCompanion.App.Controls;
using global::Windows.ApplicationModel.DataTransfer;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Web;
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
/// right edge of the work area. The panel slides in/out as one unit (like the Win11
/// notification centre) by moving the whole window — plain per-tick moves at constant
/// size, the only slide mechanism that stays butter-smooth with an embedded WebView
/// (region- and width-based variants both stalled the UI thread; see <see cref="MovePx"/>).
/// Dismisses on focus loss or Esc, and hosts the editable pinned-tile layout.
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
/// Geometry is always taken from the primary display, using that monitor's
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
    private const int MinPanelWidthDip = 320; // must match the Settings width slider bounds
    private const int MaxPanelWidthDip = 900;

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_CXVIRTUALSCREEN = 78;
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
    private bool _prewarmStarted;
    private bool _firstOpenVeiled; // the once-per-process activation scale dance is masked once
    private bool _warming;       // window is shown only off-screen for a warm pass, not open
    private Task _warmChain = Task.CompletedTask;

    // Slide geometry in absolute screen pixels, recomputed from the primary monitor per show.
    private int _winY, _winW, _winH, _restX, _offX;
    private double _scale = 1.0; // primary-monitor DPI scale (physical px per DIP)
    private int _animFromX, _animToX;
    private long _animStartMs;
    private bool _timerBoosted;
    // Lightweight jank telemetry: inter-event gaps on the UI thread reveal where frame
    // budget is burned (layout/render starves input); logged per slide / per drag.
    private long _perfLastMs;
    private int _perfEvents, _perfLate;
    private long _perfMaxGapMs;

    private void PerfReset()
    {
        _perfLastMs = 0;
        _perfEvents = 0;
        _perfLate = 0;
        _perfMaxGapMs = 0;
    }

    private void PerfSample()
    {
        var now = Environment.TickCount64;
        if (_perfLastMs > 0)
        {
            var gap = now - _perfLastMs;
            if (gap > _perfMaxGapMs) _perfMaxGapMs = gap;
            if (gap > 25) _perfLate++;
        }
        _perfLastMs = now;
        _perfEvents++;
    }

    // Live drag-to-resize state (grip on the left edge).
    private bool _dragResizing;
    private int _dragStartCursorX;
    private int _dragStartWidthPx;
    private int _dragMoveCount;

    public QuickPanelWindow(QuickPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
        InitializeComponent();
        RootGrid.DataContext = viewModel;

        var loc = App.Services.GetRequiredService<LocalizationService>();
        ApplyFlowDirection(loc);
        loc.LanguageChanged += (_, _) => ApplyFlowDirection(loc);
        ViewModel.DashboardRequested += OnDashboardRequested;
        // Size changes made on the start page must re-flow this view too (shared tiles).
        ViewModel.Catalog.TileSizeChanged += (_, tile) =>
        {
            if (ViewModel.SortByCategory)
                // Deferred: this event can originate from a grip's own PointerReleased inside a
                // category section — rebuilding the sections synchronously would tear down the
                // visual tree the event is still routing through.
                DispatcherQueue.TryEnqueue(() => ViewModel.RebuildGroups());
            else
                PinnedGrid.RefreshSpans(tile);
        };

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
            RootGrid.Width = _panelWidthDip; // settle on the final previewed width
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

        _scale = dpi / 96.0;
        _winW = (int)Math.Round(_panelWidthDip * _scale);
        _winY = mi.rcWork.Top;
        _winH = mi.rcWork.Bottom - mi.rcWork.Top;
        _restX = mi.rcWork.Right - _winW; // flush to the right edge of the primary display
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
            RootGrid.HorizontalAlignment = HorizontalAlignment.Right;
            RootGrid.Width = _panelWidthDip;
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
    /// Warms everything the first open would otherwise do in front of the user: loads the
    /// XAML tree (window shown once, parked beyond every display, never activated), and — as
    /// soon as HA reports connected — resolves the dashboard picker and pre-navigates the
    /// configured start dashboard in the still-hidden WebView. The first Win+Ctrl+H then
    /// slides in an already-rendered panel instead of visibly assembling it.
    /// </summary>
    public void Prewarm()
    {
        if (_prewarmStarted)
            return;
        _prewarmStarted = true;

        // The launch-time pass below usually runs before the WebSocket is up — re-run the
        // dashboard resolution once the connection is ready (it re-applies the start view,
        // which triggers the pre-navigation).
        ViewModel.Shell.PropertyChanged += OnShellConnectedWhileWarm;

        _ = WarmHiddenAsync(async () =>
        {
            ViewModel.ApplyStartView();
            await ViewModel.EnsureDashboardsAsync(); // no-op until connected (hook above)
            await WarmWebIfDashboardAsync();
        });
    }

    private async void OnShellConnectedWhileWarm(object? sender, PropertyChangedEventArgs e)
    {
        // async void on a NON-UI event (ViewModel PropertyChanged): an escaping exception
        // would rethrow on the caller's sync context instead of the XAML safety net, so this
        // handler must swallow its own failures (warming is best-effort anyway).
        try
        {
            if (e.PropertyName != nameof(ShellViewModel.IsConnected) || !ViewModel.Shell.IsConnected)
                return;
            ViewModel.Shell.PropertyChanged -= OnShellConnectedWhileWarm;
            await WarmHiddenAsync(async () =>
            {
                await ViewModel.EnsureDashboardsAsync(); // re-applies the start view -> pre-navigates
                await WarmWebIfDashboardAsync();
            });
        }
        catch (Exception ex)
        {
            Log("warm-on-connect failed: " + ex.Message);
        }
    }

    /// <summary>Spins up the WebView2 runtime while hidden when a dashboard view is active.</summary>
    private async Task WarmWebIfDashboardAsync()
    {
        if (!ViewModel.ShowDashboard)
            return; // favourites need no WebView; dashboard switches init it on demand
        try
        {
            await EnsureWebAsync();
            await Task.Delay(250); // let the just-issued pre-navigation take hold before re-hiding
        }
        catch
        {
            // WebView2 runtime missing — favourites still work; nothing to warm.
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the window shown but parked beyond the right edge of
    /// the entire virtual screen: WebView2 initialization needs a shown window, and parking it
    /// past every monitor keeps the pass invisible without stealing focus. Passes are chained
    /// so they never overlap; the hidden state is restored afterwards unless the user opened
    /// the panel mid-warm (SetTarget then upgrades the warm window with a full setup).
    /// </summary>
    private Task WarmHiddenAsync(Func<Task> work)
    {
        async Task Run(Task previous)
        {
            try { await previous; } catch { /* chain must survive a failed pass */ }

            var wasShown = _windowShown;
            if (!wasShown)
            {
                _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
                ComputeGeometry();
                LockContentWidth();
                MoveWindowPx(VirtualScreenRightPx() + 200, _winY, _winW, _winH);
                _warming = true;
                _windowShown = true;
                AppWindow.Show(activateWindow: false);
            }
            try
            {
                await work();
            }
            finally
            {
                _warming = false;
                if (!wasShown && !_isOpen)
                {
                    _windowShown = false;
                    AppWindow.Hide();
                }
                Log($"warm pass done shown={_windowShown} dash={ViewModel.ShowDashboard}");
            }
        }

        return _warmChain = Run(_warmChain);
    }

    private static int VirtualScreenRightPx() =>
        GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN);

    /// <summary>
    /// The very first Activate() of the process completes the XAML island's composition
    /// and briefly re-evaluates the rasterization scale — the embedded web content then
    /// visibly "zoom-pops" for a few frames. Masking the WebView for the first slide-in
    /// hides that once-per-process dance.
    /// </summary>
    private void VeilFirstActivation()
    {
        if (_firstOpenVeiled)
            return;
        _firstOpenVeiled = true;
        PanelWeb.Opacity = 0;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(300);
        t.IsRepeating = false;
        t.Tick += (_, _) => PanelWeb.Opacity = 1;
        t.Start();
    }

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
        // A window that is only shown off-screen for a warm pass still needs the full setup
        // (park at the slide edge, activate, focus), hence the _warming escape hatch.
        if (open && (!_windowShown || _warming) && !_inSetup)
        {
            _inSetup = true;
            try
            {
                var settings = _settingsStore.Load();
                _panelWidthDip = settings.QuickPanelWidth;
                ResizeGrip.Visibility = settings.QuickPanelDragResize ? Visibility.Visible : Visibility.Collapsed;
                ComputeGeometry();
                LockContentWidth();
                MovePx(_offX); // park just off the right edge
                _windowShown = true;
                AppWindow.Show();
                VeilFirstActivation();
                Activate();
                ApplyNoBorder();
            }
            finally
            {
                _inSetup = false;
            }

            ViewModel.ApplyStartView(); // configured default view, applied on every open
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
        if (!_animTimer.IsRunning)
            PerfReset();
        if (!_timerBoosted)
        {
            _ = TimeBeginPeriod(1); // request 1 ms timer cadence for a smooth slide
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
        PerfSample();

        if (t < 1.0)
            return;

        // Reached the target: stop the clock and snap exactly onto the target edge so rounding
        // can never leave a one-pixel sliver behind.
        _animTimer.Stop();
        if (_timerBoosted)
        {
            _ = TimeEndPeriod(1);
            _timerBoosted = false;
        }

        MovePx(_animToX);
        if (!_isOpen)
        {
            _windowShown = false;
            AppWindow.Hide();
        }
        Log($"settled open={_isOpen} x={_animToX} parked={!_isOpen} perf: ticks={_perfEvents} late={_perfLate} maxGap={_perfMaxGapMs}ms");
    }

    private void ApplyNoBorder()
    {
        // Paint the 1 px Win11 window outline in the panel's own dark colour so it can never
        // read as a thin white "selected" frame; keep crisp (non-rounded) corners.
        var borderColor = 0x00202020; // COLORREF 0x00BBGGRR ≈ dark panel base
        _ = DwmSetWindowAttribute(_hwnd, 34, ref borderColor, sizeof(int));
        var doNotRound = 1;
        _ = DwmSetWindowAttribute(_hwnd, 33, ref doNotRound, sizeof(int));
    }

    private void MoveWindowPx(int x, int y, int w, int h) =>
        _ = SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>
    /// Slide step: a plain MOVE at constant size — measured on the target machine, this is
    /// the only slide mechanism whose ticks stay on budget with an embedded WebView.
    /// For the record, both "cleverer" variants were built and reverted: animating the
    /// window WIDTH rebuilds the island swapchain per 6 ms tick (visible hang), and a
    /// static window with content-translate + SetWindowRgn cropping stalled the UI thread
    /// 250+ ms per slide, as did intercepting WM_DPICHANGED (it fights the island's own
    /// monitor tracking — repeated full re-rasterizations). Moving across the monitor edge
    /// can make a differently-scaled neighbour briefly re-rasterize the web content
    /// mid-slide; the all-mode sidebar CSS and the camera-URL normalization keep that
    /// residue harmless — smoothness wins the remaining trade-off (user decision).
    /// </summary>
    private void MovePx(int x) => MoveWindowPx(x, _winY, _winW, _winH);


    /// <summary>
    /// Fixes the content root to the full panel width, anchored to the window's RIGHT edge
    /// (which is pinned to the monitor edge), so intermediate window widths (live
    /// drag-resize between throttle steps) crop or reveal at the moving left edge while the
    /// content itself stands perfectly still — anchored left, the whole dashboard visibly
    /// rode along with every drag step.
    /// </summary>
    private void LockContentWidth()
    {
        RootGrid.HorizontalAlignment = HorizontalAlignment.Right;
        RootGrid.Width = _panelWidthDip;
    }

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

    private async Task EnsureWebAsync()
    {
        var task = _webInitTask ??= InitWebAsync();
        try
        {
            await task;
        }
        catch
        {
            // Don't cache a faulted init forever — a transient failure (e.g. WebView2
            // hiccup) would otherwise disable the dashboard view until app restart.
            if (_webInitTask == task)
                _webInitTask = null;
            throw;
        }
    }

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

        var baseUri = new Uri(_baseUrl, UriKind.Absolute);
        WebViewHardening.Apply(PanelWeb.CoreWebView2, baseUri, settings.IgnoreCertificateErrors);

        await PanelWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            HaWebViewScripts.BuildAuthScript(baseUri, settings.Token));
        await PanelWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            HaWebViewScripts.HideChromeScript);
        await PanelWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            HaWebViewScripts.CameraStillFixScript);
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
        // Explicit escapes — literal PUA glyph characters once got silently lost in a file
        // rewrite, leaving the button icon permanently blank after the first click.
        EditIcon.Glyph = editing ? "\uE73E" : "\uE70F"; // CheckMark / Edit (pencil)
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

    // ----- hand-drag tile resize (corner grip; click still cycles the presets) -----

    private const double TileCellWidth = 112;  // must match the VariableSizedWrapGrid ItemWidth
    private const double TileCellHeight = 108; // must match the VariableSizedWrapGrid ItemHeight

    private readonly TileResizeDrag _tileResize = new();

    private void ResizeGripTile_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
        {
            // The owning grid (flat or per-category) is found from the grip via the visual tree.
            _tileResize.Begin((UIElement)sender, e, tile, TileCellWidth, TileCellHeight);
            e.Handled = true; // keep the press from starting an item drag
        }
    }

    private void ResizeGripTile_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tileResize.Update(e))
            e.Handled = true;
    }

    private void ResizeGripTile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var (tile, moved) = _tileResize.End((UIElement)sender, e);
        if (tile is null)
            return;
        if (moved)
            ViewModel.Catalog.SetTileSpans(tile, tile.ColSpan, tile.RowSpan); // persist the dragged size
        else
            ViewModel.Catalog.CycleTileSize(tile); // plain click: next preset
        e.Handled = true;
    }

    private void ResizeGripTile_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var (tile, moved) = _tileResize.End(null, e);
        if (tile is not null && moved)
            ViewModel.Catalog.SetTileSpans(tile, tile.ColSpan, tile.RowSpan);
    }

    // ----- manual tile reorder (built-in GridView reorder doesn't work on a
    //       VariableSizedWrapGrid, so drag-and-drop is handled explicitly). While the
    //       drag is in flight the other tiles rearrange reactively: every DragOver maps
    //       the pointer to an insertion slot and live-moves the dragged tile there. -----

    private EntityTileViewModel? _draggedTile;
    private long _lastLiveMoveMs;

    private void PinnedGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedTile = e.Items.FirstOrDefault() as EntityTileViewModel;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void PinnedGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        _draggedTile = null; // also covers drops outside the grid

    private void PinnedGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTile is null || ViewModel.SortByCategory)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        LiveReorder(e.GetPosition(PinnedGrid));
    }

    private void PinnedGrid_Drop(object sender, DragEventArgs e)
    {
        // Final settle (usually a no-op — the tile was already live-moved while dragging).
        if (_draggedTile is not null && !ViewModel.SortByCategory)
        {
            _lastLiveMoveMs = 0; // bypass the debounce for the definitive drop position
            LiveReorder(e.GetPosition(PinnedGrid));
        }
        _draggedTile = null;
    }

    /// <summary>
    /// Move the dragged tile to the slot under the pointer so the grid re-flows while the drag
    /// is still in progress. Debounced: right after a move the geometry shifts under the pointer,
    /// and recomputing immediately would oscillate between the old and new slot.
    /// </summary>
    private void LiveReorder(global::Windows.Foundation.Point position)
    {
        if (Environment.TickCount64 - _lastLiveMoveMs < 150)
            return;

        var source = ViewModel.Catalog.Pinned;
        var from = source.IndexOf(_draggedTile!);
        if (from < 0)
            return;

        var to = TileDragHelper.InsertionIndex(PinnedGrid, position, source.Count);
        if (to > from)
            to--;
        to = Math.Clamp(to, 0, source.Count - 1);
        if (to != from)
        {
            source.Move(from, to); // persisted via the catalog's CollectionChanged handler
            _lastLiveMoveMs = Environment.TickCount64;
        }
    }

    private void ApplyFlowDirection(LocalizationService loc)
    {
        RootGrid.FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        // The width grip must stay on the inner (screen-left) edge. Mirroring flips
        // what Left/Right mean, so RTL needs Right to land on the same visual edge;
        // the drag math itself uses raw screen coordinates and is direction-agnostic.
        ResizeGrip.HorizontalAlignment = loc.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    // ----- live drag-to-resize (left-edge grip) -----

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement grip)
            return;
        _ = GetCursorPos(out var pt);
        _dragStartCursorX = pt.X;
        _dragStartWidthPx = _winW;
        _dragMoveCount = 0;
        PerfReset();
        _dragResizing = grip.CapturePointer(e.Pointer);
        Log($"grip pressed captured={_dragResizing} startWpx={_dragStartWidthPx} cursorX={pt.X}");
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragResizing)
            return;
        _dragMoveCount++;
        _ = GetCursorPos(out var pt);
        // Screen-space delta keeps the drag stable even though the grabbed edge moves under it.
        // Dragging the left grip leftwards (negative delta) widens the panel.
        var widthPx = _dragStartWidthPx - (pt.X - _dragStartCursorX);
        var dip = Math.Clamp((int)Math.Round(widthPx / _scale), MinPanelWidthDip, MaxPanelWidthDip);
        _panelWidthDip = dip;
        _winW = (int)Math.Round(dip * _scale);
        _restX = _offX - _winW; // the right edge stays docked to the monitor edge
        RootGrid.Width = dip;   // native content re-flows per move — instant
        MoveWindowPx(_restX, _winY, _winW, _winH);
        PerfSample();
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDragResize(sender, e);

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDragResize(sender, e);

    private void EndDragResize(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragResizing)
            return;
        _dragResizing = false;
        if (sender is UIElement grip)
            grip.ReleasePointerCapture(e.Pointer);
        RootGrid.Width = _panelWidthDip;
        Log($"grip released moves={_dragMoveCount} finalWpx={_winW} dip={_panelWidthDip} perf: moves={_perfEvents} late={_perfLate} maxGap={_perfMaxGapMs}ms");

        // Persist the new width so it survives reopening and the Settings slider reflects it.
        var settings = _settingsStore.Load();
        settings.QuickPanelWidth = _panelWidthDip;
        _settingsStore.Save(settings);
        e.Handled = true;
    }


    // ----- Ctrl+K quick launcher: search any actionable entity and trigger it -----

    private void OnLauncherAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.ShowFavorites)
            LauncherFlyout.ShowAt(LauncherButton);
    }

    private void LauncherFlyout_Opened(object sender, object e)
    {
        LauncherBox.Text = string.Empty;
        LauncherBox.ItemsSource = SearchActionable(string.Empty);
        LauncherBox.Focus(FocusState.Programmatic);
    }

    private IReadOnlyList<EntityTileViewModel> SearchActionable(string query) =>
        ViewModel.Catalog.SearchTiles(query, actionableOnly: true);

    private void LauncherBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = SearchActionable(sender.Text);
    }

    // Only QuerySubmitted (it also fires after a suggestion click, with ChosenSuggestion set) —
    // handling SuggestionChosen as well would trigger the entity twice.
    private void LauncherBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var tile = args.ChosenSuggestion as EntityTileViewModel
                   ?? (SearchActionable(args.QueryText) is { Count: > 0 } results ? results[0] : null);
        if (tile is null)
            return;
        LauncherFlyout.Hide();
        App.Services.GetRequiredService<IEntityActionService>().Trigger(tile.EntityId);
    }

    // ----- tile context flyout (stage-2 controls: brightness / temperature / media) -----

#pragma warning disable CA1822
    private void TileFlyout_Opening(object sender, object e)
    {
        // No controls for this domain (switch, script, sensor, ...): don't show an empty flyout.
        if (sender is Flyout flyout && flyout.Target is FrameworkElement fe
            && fe.DataContext is EntityTileViewModel tile
            && !(tile.HasBrightness || tile.HasColor || tile.HasColorTemp || tile.HasClimate || tile.HasMedia))
            flyout.Hide();
    }
#pragma warning restore CA1822

    private void BrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Programmatic updates (WebSocket refresh) leave the slider unfocused — only a user
        // interaction may fire the service call, otherwise every state echo would re-send.
        if (sender is Slider slider && slider.FocusState != FocusState.Unfocused
            && slider.DataContext is EntityTileViewModel tile)
            tile.SetBrightness(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState != FocusState.Unfocused
            && slider.DataContext is EntityTileViewModel tile)
            tile.SetVolume(e.NewValue);
    }

    private void TempDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.NudgeTemperature(-0.5);
    }

    private void TempUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.NudgeTemperature(+0.5);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.PlayPause();
    }

    private void TilePlayPause_Click(object sender, RoutedEventArgs e)
    {
        // The inline overlay button; Button swallows the pointer, so the item click
        // (ToggleCommand) does not fire — same mechanics as the unpin button.
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.PlayPause();
    }

    private void PrevTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.PreviousTrack();
    }

    private void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntityTileViewModel tile)
            tile.NextTrack();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ColorSwatchViewModel swatch)
            swatch.Apply();
    }

    private void ColorTempSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState != FocusState.Unfocused
            && slider.DataContext is EntityTileViewModel tile)
            tile.SetColorTemp(e.NewValue);
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
            var file = Path.Combine(dir, "panel.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512_000)
                File.Delete(file); // cap the diagnostics log instead of growing forever
            File.AppendAllText(file, $"{Environment.TickCount64,12} {message}\n");
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
    private static extern bool GetCursorPos(out POINT lpPoint);

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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);
}
