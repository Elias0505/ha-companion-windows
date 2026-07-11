// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="INotificationService"/>
public sealed class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private bool _available;

    public NotificationService(ILogger<NotificationService> logger) => _logger = logger;

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
            manager.Register();
            _available = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize app notifications; continuing without them");
        }
    }

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
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Bring the app to the foreground when the user clicks a toast.
        var window = App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() => window.Activate());
    }
}
