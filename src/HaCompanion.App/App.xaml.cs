// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using HaCompanion.App.Windows;
using HaCompanion.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace HaCompanion.App;

public partial class App : Application
{
    /// <summary>Application-wide service provider (composition root).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>The main window; kept alive for the lifetime of the app (hidden to tray, not closed).</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = ConfigureServices();

        // Capture the UI DispatcherQueue on this (UI) thread before anything marshals to it.
        _ = Services.GetRequiredService<IUiDispatcher>();

        // App notifications must register before the first UI is shown (best-effort).
        Services.GetRequiredService<INotificationService>().Initialize();

        // Ensure the entity catalog is subscribed to the connection before we connect.
        _ = Services.GetRequiredService<EntityCatalogViewModel>();

        MainWindow = Services.GetRequiredService<MainWindow>();

        var hotkeys = Services.GetRequiredService<IHotkeyService>();
        var quickPanel = Services.GetRequiredService<IQuickPanelController>();
        hotkeys.Initialize(MainWindow);
        var storedHotkey = Services.GetRequiredService<ISettingsStore>().Load().Hotkey;
        hotkeys.Register(string.IsNullOrWhiteSpace(storedHotkey) ? "Win+Ctrl+H" : storedHotkey);
        hotkeys.HotkeyPressed += (_, _) => quickPanel.Toggle();

        MainWindow.Activate();

        // Auto-connect if we already have stored settings.
        _ = Services.GetRequiredService<ShellViewModel>().InitializeAsync();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHaCompanionCore();

        // Infrastructure + app services
        services.AddSingleton<IUiDispatcher, DispatcherQueueUiDispatcher>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ITileLayoutStore, TileLayoutStore>();
        services.AddSingleton<MdiIconProvider>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IQuickPanelController, QuickPanelController>();

        // View models
        services.AddSingleton<EntityCatalogViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<QuickPanelViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
