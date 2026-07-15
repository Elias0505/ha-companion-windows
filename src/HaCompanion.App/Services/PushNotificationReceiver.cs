// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Text.Json;
using HaCompanion.App.Infrastructure;
using HaCompanion.Core.MobileApp;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>One received notification/command for the "Mein PC" history list.</summary>
public sealed record ReceivedItem(DateTimeOffset At, string Title, string Message, bool IsCommand)
{
    public string TimeText => At.ToLocalTime().ToString("HH:mm");
}

/// <summary>
/// Receives everything HA pushes to this PC via notify.mobile_app_&lt;device&gt;:
/// ordinary notifications become Windows toasts (with clickable action buttons that
/// fire mobile_app_notification_action back to HA), command messages go to the
/// command executor. Keeps a bounded in-memory history for the "Mein PC" tab.
/// </summary>
public interface IPushNotificationReceiver
{
    /// <summary>Hook the connection + toast activation. Call once at startup.</summary>
    void Initialize();

    /// <summary>Newest first, capped; bound by the "Mein PC" tab (UI thread only).</summary>
    ObservableCollection<ReceivedItem> History { get; }
}

/// <inheritdoc cref="IPushNotificationReceiver"/>
public sealed class PushNotificationReceiver : IPushNotificationReceiver
{
    private const int HistoryCap = 100;

    private readonly IHaConnection _connection;
    private readonly INotificationService _notifications;
    private readonly IPcCommandExecutor _executor;
    private readonly ISettingsStore _settings;
    private readonly LocalizationService _loc;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<PushNotificationReceiver> _logger;
    private string? _lastTag; // tag of the toast whose buttons are currently live

    public ObservableCollection<ReceivedItem> History { get; } = new();

    public PushNotificationReceiver(IHaConnection connection, INotificationService notifications,
        IPcCommandExecutor executor, ISettingsStore settings, LocalizationService loc,
        IUiDispatcher ui, ILogger<PushNotificationReceiver> logger)
    {
        _connection = connection;
        _notifications = notifications;
        _executor = executor;
        _settings = settings;
        _loc = loc;
        _ui = ui;
        _logger = logger;
    }

    public void Initialize()
    {
        _connection.PushNotificationReceived += OnPush;
        _notifications.ActionInvoked += OnToastAction;

        var webhookId = _settings.Load().MobileAppWebhookId;
        if (!string.IsNullOrEmpty(webhookId))
            _connection.EnablePushChannel(webhookId);
    }

    /// <summary>Called by the sensor publisher after a (re-)registration created a new webhook id.</summary>
    public void OnWebhookChanged(string webhookId) => _connection.EnablePushChannel(webhookId);

    private void OnPush(object? sender, JsonElement payload)
    {
        try
        {
            if (!PushMessageParser.TryParse(payload, out var message))
                return;

            // Always confirm first — HA retries unconfirmed deliveries.
            if (message.ConfirmId is { } confirmId)
                _ = _connection.ConfirmPushAsync(confirmId);

            if (PcCommands.TryParse(message.Message, out var command))
            {
                var param = PcCommands.ParamField(command) is { } field
                    ? PushMessageParser.DataString(payload, field)
                    : null;
                var ok = _executor.Execute(command, param);
                AddHistory(new ReceivedItem(DateTimeOffset.Now,
                    _loc["Pc_CmdReceived"],
                    _loc["Cmd_" + PcCommands.ToKey(command)] + (ok ? "" : $" — {_loc["Pc_CmdBlocked"]}"),
                    IsCommand: true));
                return;
            }

            var title = string.IsNullOrWhiteSpace(message.Title) ? "Home Assistant" : message.Title!;
            if (message.Actions.Count > 0)
            {
                _lastTag = message.Tag;
                _notifications.ShowWithActions(title, message.Message,
                    message.Actions.Select(a => (a.Action, a.Title)).ToList());
            }
            else
            {
                _notifications.Show(title, message.Message);
            }
            AddHistory(new ReceivedItem(DateTimeOffset.Now, title, message.Message, IsCommand: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle pushed notification");
        }
    }

    private void OnToastAction(object? sender, string action)
    {
        // Same event the Android companion fires — HA automations trigger on it.
        _ = _connection.FireEventAsync("mobile_app_notification_action", new Dictionary<string, object?>
        {
            ["action"] = action,
            ["tag"] = _lastTag,
            ["device_id"] = _settings.Load().MobileAppDeviceId,
        });
        _logger.LogInformation("Notification action fired: {Action}", action);
    }

    private void AddHistory(ReceivedItem item) => _ui.Post(() =>
    {
        History.Insert(0, item);
        while (History.Count > HistoryCap)
            History.RemoveAt(History.Count - 1);
    });
}
