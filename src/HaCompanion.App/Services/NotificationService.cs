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

    public event EventHandler<string>? ActionInvoked;

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
            _logger.LogInformation("Toast shown: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification");
        }
    }

    public void ShowWithActions(string title, string message, IReadOnlyList<(string Action, string Title)> actions)
    {
        if (!_available)
            return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);
            foreach (var (action, label) in actions.Take(5)) // Windows caps toast buttons at 5
                builder.AddButton(new AppNotificationButton(label).AddArgument("action", action));
            AppNotificationManager.Default.Show(builder.BuildNotification());
            _logger.LogInformation("Toast with {Count} action(s) shown: {Title}", actions.Count, title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification with actions");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // A button click carries its action id; a plain body click focuses the app.
        if (args.Arguments.TryGetValue("action", out var action) && !string.IsNullOrEmpty(action))
        {
            ActionInvoked?.Invoke(this, action);
            return;
        }
        var window = App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() => window.Activate());
    }
}
