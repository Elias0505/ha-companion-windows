// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.IO;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using HaCompanion.App.Windows;
using HaCompanion.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // Last-resort diagnostics: a tray app that dies silently is undebuggable. Log every
        // unhandled exception to %LOCALAPPDATA%\HaCompanion\crash.log; keep the app alive for
        // UI-thread exceptions (one failed handler shouldn't kill the tray + hotkey).
        UnhandledException += (_, e) =>
        {
            LogCrash("XAML UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single-instancing is handled in Program.Main via AppInstance; by the time we get here
        // we are guaranteed to be the one running instance.
        Services = ConfigureServices();

        // Apply the saved UI language and expose the localization service to XAML
        // (so {Binding [Key], Source={StaticResource Loc}} resolves) before any window loads.
        var localization = Services.GetRequiredService<LocalizationService>();
        localization.SetLanguage(Services.GetRequiredService<ISettingsStore>().Load().Language);
        Resources["Loc"] = localization;

        // Capture the UI DispatcherQueue on this (UI) thread before anything marshals to it.
        _ = Services.GetRequiredService<IUiDispatcher>();

        // App notifications must register before the first UI is shown (best-effort).
        Services.GetRequiredService<INotificationService>().Initialize();

        // Ensure the entity catalog is subscribed to the connection before we connect.
        _ = Services.GetRequiredService<EntityCatalogViewModel>();

        MainWindow = Services.GetRequiredService<MainWindow>();

        // BEFORE the hotkey goes live: a factory reset cannot delete the WebView2 profiles while
        // they are open, and the same window is the only safe moment to drop storage left by
        // older versions (whose persistent profiles wrote the HA token to disk in cleartext).
        // Registering the hotkey first would let a queued WM_HOTKEY open the panel — and create
        // the very profile folder being deleted — during any message pump in between.
        var reset = Services.GetRequiredService<IConfigResetService>();
        reset.CompletePending();
        reset.PurgeLegacyWebViewProfiles();

        var hotkeys = Services.GetRequiredService<IHotkeyService>();
        var quickPanel = Services.GetRequiredService<IQuickPanelController>();
        hotkeys.Initialize(MainWindow);
        var storedHotkey = Services.GetRequiredService<ISettingsStore>().Load().Hotkey;
        var combo = string.IsNullOrWhiteSpace(storedHotkey) ? "Win+Ctrl+H" : storedHotkey;
        if (!hotkeys.Register(combo))
        {
            // Another app owning the combo means the quick panel simply never opens. Only the
            // settings page showed that, so the failure was invisible to anyone who did not go
            // looking — say it once, where the user actually is.
            var loc = Services.GetRequiredService<LocalizationService>();
            Services.GetRequiredService<INotificationService>().Show(
                "HA Companion",
                string.Format(CultureInfo.CurrentCulture, loc["Set_HotkeyFailed"], combo));
        }
        hotkeys.HotkeyPressed += (_, _) => quickPanel.Toggle();

        // Entity shortcuts: register the stored hotkey->entity bindings (needs the hooked window).
        Services.GetRequiredService<IShortcutManager>().Initialize();

        // Windows-state monitor: second WndProc subclass on the main window — must come
        // AFTER hotkeys.Initialize and neither hook is ever removed (chains compose).
        Services.GetRequiredService<IWindowsStateMonitor>().Initialize(MainWindow);

        // Windows automation rules (WENN Windows-Ereignis -> DANN HA-Aktion).
        Services.GetRequiredService<IRulesEngine>().Initialize();

        // PC sensors -> HA (mobile_app device); inert until the settings toggle is on.
        Services.GetRequiredService<ISensorPublisher>().Initialize();

        // HA -> PC: pushed notifications (toasts with actions) + PC commands, and the
        // local "benachrichtige mich wenn ..." rules over the live entity stream.
        Services.GetRequiredService<IPushNotificationReceiver>().Initialize();
        Services.GetRequiredService<INotifyRulesEngine>().Initialize();

        // Keep an existing autostart entry pointing at the current exe (path may change on update).
        Services.GetRequiredService<IStartupService>().SelfHeal();

        // Retry the connection immediately when the network returns or the machine resumes.
        Services.GetRequiredService<IConnectivityWatcher>().Initialize();

        // HA persistent notifications -> native Windows toasts (toggle in Settings).
        var connection = Services.GetRequiredService<HaCompanion.Core.Services.IHaConnection>();
        var notifications = Services.GetRequiredService<INotificationService>();
        var settingsStore = Services.GetRequiredService<ISettingsStore>();
        connection.NotificationReceived += (_, n) =>
        {
            if (settingsStore.Load().ShowHaNotifications)
                notifications.Show(n.Title, n.Message);
        };

        // Taskbar jump list (Quick panel / Reconnect); localized, rebuilt on language change.
        Services.GetRequiredService<JumpListService>().Initialize();

        var log = Services.GetRequiredService<ILogger<App>>();
        var commandLine = Environment.GetCommandLineArgs();
        var autostarted = commandLine.Contains(StartupService.AutostartArg);
        var wantsPanel = commandLine.Contains(JumpListService.QuickPanelArg);
        log.LogInformation("HA Companion {Version} started{Mode}",
            typeof(App).Assembly.GetName().Version, autostarted ? " (autostart, tray only)" : "");

        // Launched by the autostart entry: stay silently in the tray (hotkeys/panel active),
        // don't pop the main window into the user's face at every boot. A jump-list
        // "quick panel" launch behaves the same — the user asked for the panel, not the window.
        if (!autostarted && !wantsPanel)
            MainWindow.Activate();

        // Auto-connect if we already have stored settings.
        _ = Services.GetRequiredService<ShellViewModel>().InitializeAsync();

        // Pre-warm the quick panel while nothing is visible yet: build the window and, once
        // HA is connected, pre-navigate the start dashboard in the hidden WebView — the first
        // Win+Ctrl+H must slide in instantly instead of visibly loading in front of the user.
        var ui = Services.GetRequiredService<IUiDispatcher>();
        _ = Task.Delay(1500).ContinueWith(_ => ui.Post(quickPanel.Prewarm));

        // Fresh start via the jump list's "quick panel" entry (the app was not running):
        // open the panel as soon as the message loop breathes.
        if (wantsPanel)
            ui.Post(quickPanel.Toggle);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // File sink: without a provider every ILogger warning silently vanished.
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion", "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(logPath)));
        services.AddHaCompanionCore();

        // Infrastructure + app services
        services.AddSingleton<IUiDispatcher, DispatcherQueueUiDispatcher>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ITileLayoutStore, TileLayoutStore>();
        services.AddSingleton<MdiIconProvider>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IQuickPanelController, QuickPanelController>();
        services.AddSingleton<JumpListService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IConnectivityWatcher, ConnectivityWatcher>();
        services.AddSingleton<IShortcutStore, ShortcutStore>();
        services.AddSingleton<IEntityActionService, EntityActionService>();
        services.AddSingleton<IShortcutManager, ShortcutManager>();
        services.AddSingleton<IWindowsStateMonitor, WindowsStateMonitor>();
        services.AddSingleton<IRulesStore, RulesStore>();
        services.AddSingleton<IAutomationStatsStore, AutomationStatsStore>();
        services.AddSingleton<IRulesEngine, RulesEngine>();
        services.AddSingleton<ISensorPublisher, SensorPublisher>();
        services.AddSingleton<IPcCommandExecutor, PcCommandExecutor>();
        services.AddSingleton<IPushNotificationReceiver, PushNotificationReceiver>();
        services.AddSingleton<INotifyRulesStore, NotifyRulesStore>();
        services.AddSingleton<INotifyRulesEngine, NotifyRulesEngine>();
        services.AddSingleton<IConfigBackupService, ConfigBackupService>();
        services.AddSingleton<IConfigResetService, ConfigResetService>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();

        // View models
        services.AddSingleton<EntityCatalogViewModel>();
        // Per-page category filter over the shared catalog (transient: one per page VM).
        services.AddTransient<DeviceBrowserViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<QuickPanelViewModel>();
        services.AddSingleton<ShortcutsViewModel>();
        services.AddSingleton<AutomationsViewModel>();
        services.AddSingleton<MyPcViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaCompanion");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "crash.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512_000)
                File.Move(file, file + ".1", overwrite: true); // rotate, don't wipe crash history
            File.AppendAllText(file, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}\n\n");
        }
        catch
        {
            // never let crash logging cause another crash
        }
    }

    /// <summary>
    /// A second launch was redirected to us by <see cref="Program"/>. With a jump-list argument
    /// the matching action runs (quick panel / reconnect); otherwise the existing window comes
    /// to the front on the UI thread — the same reliable path as a tray-icon click, so it works
    /// even when the window is currently hidden in the tray.
    /// </summary>
    public static void OnRedirected(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        // A second launch may carry a jump-list argument — honour it instead of only
        // resurfacing the window.
        var launchArguments =
            args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch &&
            args.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                ? launch.Arguments
                : string.Empty;

        var window = MainWindow;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (launchArguments.Contains(JumpListService.QuickPanelArg, StringComparison.OrdinalIgnoreCase))
                Services.GetRequiredService<IQuickPanelController>().Toggle();
            else if (launchArguments.Contains(JumpListService.ReconnectArg, StringComparison.OrdinalIgnoreCase))
                _ = Services.GetRequiredService<ShellViewModel>().InitializeAsync();
            else
                window.OpenCommand.Execute(null);
        });
    }

    /// <summary>
    /// Start a fresh copy of the app and quit this one — used after a factory reset, where
    /// every view model still holds the old configuration. The new process is told to wait
    /// for this process id first: it would otherwise hit the single-instance key we still
    /// hold, redirect into a dying instance and exit, leaving the user with nothing.
    /// </summary>
    public static void Relaunch()
    {
        // Same teardown as the tray's Exit: without it the close-to-tray handler cancels the
        // window close and Exit() never completes — the old process would still be holding the
        // single-instance key when the new one gives up waiting for it.
        MainWindow?.PrepareForExit();

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{Program.RelaunchAfterArg} {pid}",
                UseShellExecute = false,
            });
        }

        // Safety net: our successor is waiting for this process id, so a stalled shutdown would
        // leave the user with no app at all. If we are somehow still here in five seconds, go
        // the hard way — everything worth saving was already written before the reset.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Environment.Exit(0);
        });

        Current.Exit();
    }
}
