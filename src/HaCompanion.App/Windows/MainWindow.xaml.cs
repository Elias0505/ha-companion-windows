// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using HaCompanion.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

public sealed partial class MainWindow : Window
{
    private BitmapImage? _trayOnline;
    private BitmapImage? _trayOffline;

    private readonly ShellViewModel _shell;

    /// <summary>Left-click on the tray icon opens the window.</summary>
    public ICommand OpenCommand { get; }

    public MainWindow()
    {
        OpenCommand = new RelayCommand(ShowFromTray);
        InitializeComponent();

        Tray.LeftClickCommand = OpenCommand;
        Tray.ForceCreate(); // ensure the tray icon actually appears (esp. unpackaged)

        // Tray icon mirrors the connection: coloured when connected, grey otherwise,
        // with the live status text in the tooltip. Defer the first update — swapping the
        // icon during construction is exactly the kind of thing that can wedge H.NotifyIcon.
        _shell = App.Services.GetRequiredService<ShellViewModel>();
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.IsConnected) or nameof(ShellViewModel.StatusText))
                UpdateTrayStatus();
            if (e.PropertyName is nameof(ShellViewModel.IsRepairVisible)
                or nameof(ShellViewModel.RepairTitle) or nameof(ShellViewModel.RepairMessage))
                UpdateRepairBar();
        };
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrayStatus();
            UpdateRepairBar();
        });

        var loc = App.Services.GetRequiredService<LocalizationService>();
        ApplyFlowDirection(loc);
        loc.LanguageChanged += (_, _) => ApplyFlowDirection(loc);

        Title = "HA Companion";
        SystemBackdrop = new MicaBackdrop();

        // AppWindow.Resize takes physical pixels — scale the intended 1100x720 DIP size by
        // the monitor's DPI so the window isn't undersized on high-DPI (e.g. 150%) displays.
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)Math.Round(1100 * scale), (int)Math.Round(720 * scale)));

        // Stop the window from being dragged smaller than the layout can sensibly handle.
        // OverlappedPresenter.PreferredMinimum* is the Windows App SDK's first-class API for
        // this (values are physical pixels, so scale the intended DIP minimum by the DPI).
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinWidthDip * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinHeightDip * scale);
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Force the native title bar (incl. the min/max/close caption buttons) into dark mode.
        var useDark = 1;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref useDark, sizeof(int));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            // Solid dark title bar + caption buttons (min/max/close) so the button strip
            // matches the rest of the bar instead of rendering as a light/white block.
            var dark = Color.FromArgb(255, 32, 32, 32);
            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = dark;
            titleBar.InactiveBackgroundColor = dark;
            titleBar.ButtonBackgroundColor = dark;
            titleBar.ButtonInactiveBackgroundColor = dark;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 160, 160, 160);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 55, 55, 55);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 70, 70, 70);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        AppWindow.Closing += OnClosing;
    }

    private void ApplyFlowDirection(LocalizationService loc)
    {
        var dir = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root.FlowDirection = dir;
        // The tray flyout opens in its own second window (ContextMenuMode) and does
        // not inherit from Root — mirror its items explicitly.
        if (Tray.ContextFlyout is MenuFlyout menu)
            foreach (var item in menu.Items)
                item.FlowDirection = dir;
    }

    private void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag as string)
            {
                case "dashboard":
                    ContentFrame.Navigate(typeof(DashboardPage));
                    break;
                case "hadashboards":
                    ContentFrame.Navigate(typeof(HaDashboardsPage));
                    break;
                case "shortcuts":
                    ContentFrame.Navigate(typeof(ShortcutsPage));
                    break;
                case "automations":
                    ContentFrame.Navigate(typeof(AutomationsPage));
                    break;
                case "mypc":
                    ContentFrame.Navigate(typeof(MyPcPage));
                    break;
                case "settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
            }
        }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void Tray_Open(object sender, RoutedEventArgs e) => ShowFromTray();

    private void UpdateTrayStatus()
    {
        try
        {
            _trayOnline ??= new BitmapImage(new Uri("ms-appx:///Assets/tray.ico"));
            _trayOffline ??= new BitmapImage(new Uri("ms-appx:///Assets/tray_offline.ico"));
            Tray.ToolTipText = $"HA Companion \u2014 {_shell.StatusText}";
            Tray.IconSource = _shell.IsConnected ? _trayOnline : _trayOffline;
        }
        catch
        {
            // status display is best-effort — never let it disturb the window
        }
    }

    private void UpdateRepairBar()
    {
        RepairBar.Title = _shell.RepairTitle;
        RepairBar.Message = _shell.RepairMessage;
        RepairBar.IsOpen = _shell.IsRepairVisible;
    }

    // Dismiss hides the banner for THIS incident; the tray icon keeps showing offline.
#pragma warning disable CA1822
    private void RepairBar_CloseClick(InfoBar sender, object args) => sender.IsOpen = false;
#pragma warning restore CA1822

    private void RepairOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        foreach (var mi in Nav.MenuItems)
        {
            if (mi is NavigationViewItem { Tag: "settings" } item)
            {
                Nav.SelectedItem = item; // Nav_SelectionChanged does the navigation
                break;
            }
        }
    }

    private void Tray_Reconnect(object sender, RoutedEventArgs e) =>
        _ = _shell.InitializeAsync(); // re-runs the stored-settings connect

    private void Tray_QuickPanel(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<IQuickPanelController>().Toggle();

    private void Tray_Exit(object sender, RoutedEventArgs e)
    {
        PrepareForExit();
        Application.Current.Exit();
    }

    /// <summary>
    /// Undo everything that keeps this app alive in the background: the close-to-tray handler —
    /// it cancels the close, so <see cref="Application.Exit"/> would otherwise hang on it and
    /// the process would never go away — the global hotkeys and the tray icon. Every real quit
    /// goes through here, the tray's *Exit* as well as the relaunch after a factory reset.
    /// </summary>
    public void PrepareForExit()
    {
        AppWindow.Closing -= OnClosing;
        try
        {
            var hotkeys = App.Services.GetRequiredService<IHotkeyService>();
            hotkeys.Unregister();
            hotkeys.ClearActions(); // entity shortcuts too, not just the panel hotkey
        }
        catch { }
        Tray.Dispose();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // The close button hides to the tray instead of exiting the app.
        args.Cancel = true;
        sender.Hide();
    }

    // Smallest the window may be dragged, in DIPs — below this the nav + a page get cramped.
    private const int MinWidthDip = 640;
    private const int MinHeightDip = 540;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
