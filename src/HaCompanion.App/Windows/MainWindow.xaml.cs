// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Services;
using HaCompanion.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace HaCompanion.App.Windows;

public sealed partial class MainWindow : Window
{
    /// <summary>Left-click on the tray icon opens the window.</summary>
    public ICommand OpenCommand { get; }

    public MainWindow()
    {
        OpenCommand = new RelayCommand(ShowFromTray);
        InitializeComponent();

        Tray.LeftClickCommand = OpenCommand;
        Tray.ForceCreate(); // ensure the tray icon actually appears (esp. unpackaged)

        Title = "HA Companion";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1100, 720));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Force the native title bar (incl. the min/max/close caption buttons) into dark mode.
        var useDark = 1;
        DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref useDark, sizeof(int));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 160, 160, 160);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(24, 255, 255, 255);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        AppWindow.Closing += OnClosing;
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

    private void Tray_QuickPanel(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<IQuickPanelController>().Toggle();

    private void Tray_Exit(object sender, RoutedEventArgs e)
    {
        // Fully quit: stop hiding-to-tray, remove the icon, release the hotkey, exit.
        AppWindow.Closing -= OnClosing;
        try { App.Services.GetRequiredService<IHotkeyService>().Unregister(); } catch { }
        Tray.Dispose();
        Application.Current.Exit();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // The close button hides to the tray instead of exiting the app.
        args.Cancel = true;
        sender.Hide();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
