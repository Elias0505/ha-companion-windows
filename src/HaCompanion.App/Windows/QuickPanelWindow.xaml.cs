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
using Windows.Graphics;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

/// <summary>
/// The Win+Ctrl+H quick panel: a borderless, always-on-top overlay pinned to the
/// right edge of the work area. The whole opaque panel slides in/out as one unit by
/// moving the window itself (like the Win11 notification centre), dismisses on focus
/// loss or Esc, and hosts the editable pinned-tile layout.
/// </summary>
public sealed partial class QuickPanelWindow : Window
{
    private const int DefaultPanelWidthDip = 400;
    private const int AnimDurationMs = 200;

    public QuickPanelViewModel ViewModel { get; }

    private readonly IntPtr _hwnd;
    private readonly ISettingsStore _settingsStore;
    private readonly DispatcherQueueTimer _animTimer;
    private readonly DispatcherQueueTimer _previewTimer;
    private Task? _webInitTask;
    private string _baseUrl = string.Empty;
    private int _panelWidthDip = DefaultPanelWidthDip;
    private bool _isOpen;
    private bool _previewing;

    // Slide geometry in physical pixels, recomputed from the work area on each show.
    private int _winY, _winW, _winH, _restX, _offX;
    private int _animFromX, _animToX;
    private long _animStartMs;
    private bool _hideAfterAnim;
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

    private void ComputeGeometry()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        _winW = (int)Math.Round(_panelWidthDip * scale);
        _winY = work.Y;
        _winH = work.Height;
        _restX = work.X + work.Width - _winW;
        _offX = work.X + work.Width; // fully off the right edge of the work area
    }

    /// <summary>
    /// Live width preview: shows/resizes the panel at the current width setting (bypassing
    /// auto-hide) so the user sees the size while dragging the slider; auto-hides shortly after.
    /// </summary>
    public void PreviewWidth()
    {
        _previewing = true;
        _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
        if (!_isOpen)
        {
            ShowAnimated();
        }
        else
        {
            ComputeGeometry();
            AppWindow.MoveAndResize(new RectInt32(_restX, _winY, _winW, _winH));
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    public void Toggle()
    {
        if (_isOpen)
            HideAnimated();
        else
            ShowAnimated();
    }

    public void ShowAnimated()
    {
        _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
        ComputeGeometry();

        // Park the window fully off the right edge, then slide the whole thing in.
        AppWindow.MoveAndResize(new RectInt32(_offX, _winY, _winW, _winH));
        AppWindow.Show();
        Activate();
        ApplyNoBorder();
        _isOpen = true;
        StartSlide(_offX, _restX, hideAfter: false);

        _ = ViewModel.EnsureDashboardsAsync();
        // FocusState.Pointer focuses the first control WITHOUT drawing the keyboard focus
        // rectangle, which previously looked like a thin "selected" outline on the panel.
        if (FocusManager.FindFirstFocusableElement(RootGrid) is Control focusable)
            focusable.Focus(FocusState.Pointer);
    }

    public void HideAnimated()
    {
        if (!_isOpen)
            return;
        _isOpen = false;

        // Leaving the panel also leaves edit mode (layout is already persisted live).
        ViewModel.Catalog.IsEditing = false;
        UpdateEditIcon();

        StartSlide(AppWindow.Position.X, _offX, hideAfter: true);
    }

    private void StartSlide(int fromX, int toX, bool hideAfter)
    {
        _animFromX = fromX;
        _animToX = toX;
        _hideAfterAnim = hideAfter;
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
        AppWindow.Move(new PointInt32(x, _winY));

        if (t >= 1.0)
        {
            _animTimer.Stop();
            if (_timerBoosted)
            {
                TimeEndPeriod(1);
                _timerBoosted = false;
            }
            if (_hideAfterAnim)
                AppWindow.Hide();
        }
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);
}
