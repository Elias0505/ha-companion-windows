// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using HaCompanion.Core.MobileApp;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="INotificationService"/>
public sealed class NotificationService : INotificationService
{
    /// <summary>Toast heading when the user configured none. NOT the exe name — the
    /// parameterless Register() derived "HaCompanion" from it, which is what issue #9
    /// complained about.</summary>
    private const string DefaultDisplayName = "HA Companion";

    private readonly ILogger<NotificationService> _logger;
    private readonly ISettingsStore _settings;
    private bool _available;
    private int _toastSeq; // unique tag per toast so Windows shows each as its own banner

    public NotificationService(ILogger<NotificationService> logger, ISettingsStore settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public void Initialize()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                _logger.LogInformation("App notifications are not supported in this deployment.");
                return;
            }

            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked; // subscribe BEFORE Register()
            RegisterIdentity(manager, _settings.Load().ToastAppName);
            _available = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize app notifications; continuing without them");
        }
    }

    public void ApplyDisplayName(string? displayName)
    {
        if (!_available)
            return;
        try
        {
            // Re-register WITHOUT Unregister: Register() only rewrites the HKCU registry
            // values (DisplayName/IconUri), and the NotificationInvoked subscription stays —
            // an Unregister/Register cycle would risk killing toast-button activation, which
            // drives PC commands and HA action feedback.
            RegisterIdentity(AppNotificationManager.Default, displayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply the new toast display name");
        }
    }

    private void RegisterIdentity(AppNotificationManager manager, string? configuredName)
    {
        // Same normalization rules as the HA device name (trim, control chars, 64 cap).
        var name = MobileAppDeviceName.Resolve(configuredName, DefaultDisplayName);
        try
        {
            var icon = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
            manager.Register(name, icon);
        }
        catch (Exception ex)
        {
            // The overload rejected the name/icon — better an ugly default heading than none.
            _logger.LogDebug(ex, "Register with display name failed; using the default identity");
            manager.Register();
        }
    }

    public event EventHandler<ToastActionInvokedArgs>? ActionInvoked;

    public void Show(string title, string message)
    {
        if (!_available)
            return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            // A unique tag stops Windows from collapsing consecutive toasts that share a title
            // (e.g. a light's "on" then "off") into a single silently-updated notification.
            toast.Tag = $"hac-{Interlocked.Increment(ref _toastSeq)}";
            AppNotificationManager.Default.Show(toast);
            _logger.LogInformation("Toast shown: {Title}", PcCommands.ForLog(title));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification");
        }
    }

    public void ShowWithActions(string title, string message, IReadOnlyList<(string Action, string Title)> actions, string? haTag)
    {
        if (!_available)
            return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);
            foreach (var (action, label) in actions.Take(5)) // Windows caps toast buttons at 5
            {
                var button = new AppNotificationButton(label).AddArgument("action", action);
                // The HA tag rides in the button's own arguments — a click on an old
                // toast must report that toast's tag, not the most recent one.
                if (!string.IsNullOrEmpty(haTag))
                    button.AddArgument("tag", haTag);
                builder.AddButton(button);
            }
            var toast = builder.BuildNotification();
            toast.Tag = $"hac-{Interlocked.Increment(ref _toastSeq)}";
            AppNotificationManager.Default.Show(toast);
            _logger.LogInformation("Toast with {Count} action(s) shown: {Title}", actions.Count, PcCommands.ForLog(title));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification with actions");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // A button click carries its action id (+ the originating toast's tag);
        // a plain body click focuses the app.
        if (args.Arguments.TryGetValue("action", out var action) && !string.IsNullOrEmpty(action))
        {
            args.Arguments.TryGetValue("tag", out var tag);
            ActionInvoked?.Invoke(this, new ToastActionInvokedArgs(action, string.IsNullOrEmpty(tag) ? null : tag));
            return;
        }
        var window = App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() => window.Activate());
    }
}
