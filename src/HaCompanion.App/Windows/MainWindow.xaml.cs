// SPDX-License-Identifier: AGPL-3.0-only
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Services;
using HaCompanion.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

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
}
