// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private DispatcherQueueTimer? _previewTimer;
    private Task? _webInitTask;
    private string _baseUrl = string.Empty;
    private int _panelWidthDip = DefaultPanelWidthDip;
    private bool _isOpen;
    private bool _previewing;

    // How far the content is pushed off the right edge before it slides in.
    private float SlideDistance => _panelWidthDip + 48f;

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

        // GPU-driven content slide (the window never moves — so it can never stick half-out).
        ElementCompositionPreview.SetIsTranslationEnabled(PanelSurface, true);
        ElementCompositionPreview.GetElementVisual(PanelSurface)
            .Properties.InsertVector3("Translation", new Vector3(SlideDistance, 0, 0));

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        ApplyNoBorder();

        // Glass: extend the DWM frame across the whole client area so wherever the XAML is
        // transparent (everything except the sliding PanelSurface) the desktop shows through
        // — the opaque panel truly slides over the desktop, no window rectangle behind it.
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer!.Stop();
            _previewing = false;
            HideAnimated();
        };

        Activated += OnActivated;
        AppWindow.Hide();
    }

    /// <summary>
    /// Live width preview: shows/resizes the panel at the current width setting (bypassing
    /// auto-hide) so the user sees the size while dragging the slider; auto-hides shortly after.
    /// </summary>
    public void PreviewWidth()
    {
        _previewing = true;
        if (!_isOpen)
        {
            ShowAnimated();
        }
        else
        {
            // Already open — just resize in place to the new width.
            _panelWidthDip = _settingsStore.Load().QuickPanelWidth;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var scale = GetDpiForWindow(_hwnd) / 96.0;
            var width = (int)Math.Round(_panelWidthDip * scale);
            AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - width, work.Y, width, work.Height));
        }
        _previewTimer!.Stop();
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
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Round(_panelWidthDip * scale);
        var x = work.X + work.Width - width;

        // Place the window at its resting spot (it never moves after this) and start with
        // the content pushed off the right edge, then slide it in on the GPU.
        AppWindow.MoveAndResize(new RectInt32(x, work.Y, width, work.Height));
        var visual = ElementCompositionPreview.GetElementVisual(PanelSurface);
        visual.Properties.InsertVector3("Translation", new Vector3(SlideDistance, 0, 0));

        AppWindow.Show();
        Activate();
        ApplyNoBorder();
        _isOpen = true;
        Animate(SlideDistance, 0f, hideOnComplete: false);

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

        Animate(0f, SlideDistance, hideOnComplete: true);
    }

    private void Animate(float fromX, float toX, bool hideOnComplete)
    {
        var visual = ElementCompositionPreview.GetElementVisual(PanelSurface);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, fromX);
        slide.InsertKeyFrame(1f, toX, ease);
        slide.Duration = TimeSpan.FromMilliseconds(260);
        slide.Target = "Translation.X";

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation.X", slide);
        batch.End();
        if (hideOnComplete)
            batch.Completed += (_, _) => AppWindow.Hide();
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

    private void UpdateEditIcon()
    {
        var editing = ViewModel.Catalog.IsEditing;
        EditIcon.Glyph = editing ? "" : ""; // check / pencil
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
