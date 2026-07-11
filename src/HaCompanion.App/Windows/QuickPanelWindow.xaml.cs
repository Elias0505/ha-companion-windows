// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
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
/// right edge of the work area that slides in/out with a Composition animation,
/// dismisses on focus loss or Esc, and hosts the editable pinned-tile layout.
/// </summary>
public sealed partial class QuickPanelWindow : Window
{
    private const int DefaultPanelWidthDip = 400;

    public QuickPanelViewModel ViewModel { get; }

    private readonly IntPtr _hwnd;
    private readonly ISettingsStore _settingsStore;
    private readonly Stopwatch _slideClock = new();
    private Task? _webInitTask;
    private string _baseUrl = string.Empty;
    private int _panelWidthDip = DefaultPanelWidthDip;
    private double _fromX;
    private double _toX;
    private int _slideY;
    private bool _slideShowing;
    private bool _isOpen;

    public QuickPanelWindow(QuickPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
        InitializeComponent();
        RootGrid.DataContext = viewModel;
        ViewModel.DashboardRequested += OnDashboardRequested;

        Title = "HA Companion — Quick Panel";
        // No window-level backdrop: the panel's background lives inside RootGrid so it
        // slides in together with the content (no lag) and shows no window border.

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

        Activated += OnActivated;
        AppWindow.Hide();
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
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)(_panelWidthDip * scale);
        _slideY = work.Y;
        var finalX = work.X + work.Width - width;
        var offX = work.X + work.Width; // fully off the right edge

        // Park the window off-screen, reveal it, then slide the WHOLE window in — so
        // background, content and the embedded WebView all move together as one unit.
        AppWindow.MoveAndResize(new RectInt32(offX, work.Y, width, work.Height));
        AppWindow.Show();
        Activate();
        ApplyNoBorder();
        _isOpen = true;
        StartSlide(offX, finalX, showing: true);

        _ = ViewModel.EnsureDashboardsAsync();
        if (FocusManager.FindFirstFocusableElement(RootGrid) is Control focusable)
            focusable.Focus(FocusState.Programmatic);
    }

    public void HideAnimated()
    {
        if (!_isOpen)
            return;
        _isOpen = false;

        // Leaving the panel also leaves edit mode (layout is already persisted live).
        ViewModel.Catalog.IsEditing = false;
        UpdateEditIcon();

        var currentX = AppWindow.Position.X;
        StartSlide(currentX, currentX + AppWindow.Size.Width, showing: false);
    }

    private void StartSlide(double fromX, double toX, bool showing)
    {
        _fromX = fromX;
        _toX = toX;
        _slideShowing = showing;
        _slideClock.Restart();
        CompositionTarget.Rendering -= OnSlideFrame;
        CompositionTarget.Rendering += OnSlideFrame;
    }

    private void OnSlideFrame(object? sender, object e)
    {
        const double durationMs = 240;
        var t = _slideClock.Elapsed.TotalMilliseconds / durationMs;
        if (t > 1)
            t = 1;
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        var x = (int)Math.Round(_fromX + (_toX - _fromX) * eased);
        AppWindow.Move(new PointInt32(x, _slideY));

        if (t >= 1)
        {
            _slideClock.Stop();
            CompositionTarget.Rendering -= OnSlideFrame;
            if (!_slideShowing)
                AppWindow.Hide();
        }
    }

    private void ApplyNoBorder()
    {
        // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE removes the thin Win11 window outline;
        // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND makes it a crisp flush rectangle.
        var none = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_hwnd, 34, ref none, sizeof(int));
        var doNotRound = 1;
        DwmSetWindowAttribute(_hwnd, 33, ref doNotRound, sizeof(int));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && _isOpen
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
            // Empty url_path = the default/Overview dashboard → navigate to the site root,
            // which is exactly what HA's "Overview" sidebar entry does.
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

    private void UpdateEditIcon() =>
        EditIcon.Glyph = ViewModel.Catalog.IsEditing ? "" : ""; // check / pencil

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
}
