// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using HaCompanion.App.Infrastructure;
using HaCompanion.Core.MobileApp;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>One received notification/command for the "Mein PC" history list.</summary>
public sealed record ReceivedItem(DateTimeOffset At, string Title, string Message, bool IsCommand)
{
    public string TimeText => At.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
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

    // HA redelivers a push whose confirm never arrived; the copy carries the same
    // hass_confirm_id. Remembering recent ids stops a redelivered command_shutdown
    // from running twice — duplicates are re-confirmed but never re-executed.
    private readonly BoundedIdSet _seenConfirmIds = new(128);

    // Single-reader queue: commands and toasts are handled strictly one at a time,
    // in arrival order, off the WebSocket dispatch thread.
    private readonly Channel<(JsonElement Payload, PushMessage Message)> _queue =
        Channel.CreateUnbounded<(JsonElement, PushMessage)>(
            new UnboundedChannelOptions { SingleReader = true });

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
        _ = Task.Run(ProcessQueueAsync);

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

            if (message.ConfirmId is { } confirmId && !_seenConfirmIds.TryAdd(confirmId))
            {
                // Redelivery of something already handled (or in flight): the earlier
                // confirm was lost, so confirm again — but never execute again.
                _logger.LogInformation("Duplicate push delivery {ConfirmId} suppressed", confirmId);
                _ = ConfirmSafeAsync(confirmId);
                return;
            }

            // Clone: the payload's backing JsonDocument dies with this event handler.
            _queue.Writer.TryWrite((payload.Clone(), message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle pushed notification");
        }
    }

    private async Task ProcessQueueAsync()
    {
        // The id was recorded on arrival (so a redelivery racing a slow command is
        // suppressed) and the confirm goes out only after the work is done: if the
        // app dies mid-command the delivery stays unconfirmed and HA retries into
        // a fresh process — the one case where re-execution is correct.
        await foreach (var (payload, message) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Handle(payload, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process pushed notification");
            }
            if (message.ConfirmId is { } confirmId)
                await ConfirmSafeAsync(confirmId);
        }
    }

    private void Handle(JsonElement payload, PushMessage message)
    {
        if (PcCommands.TryParse(message.Message, out var command))
        {
            var param = PcCommands.ParamField(command) is { } field
                ? PushMessageParser.DataString(payload, field)
                : null;
            // The HA Android companion's command_volume_level carries the level in the
            // TITLE — honor that too so examples copied from the HA docs just work.
            if (command == PcCommand.Volume && string.IsNullOrWhiteSpace(param))
                param = message.Title;

            var result = _executor.Execute(command, param);
            var text = _loc["Cmd_" + PcCommands.ToKey(command)];
            if (!string.IsNullOrWhiteSpace(param))
                text += $" {param}";
            text += result switch
            {
                PcCommandResult.Ok => "",
                PcCommandResult.NotEnabled => $" — {_loc["Pc_CmdBlocked"]}",
                PcCommandResult.BadParameter => $" — {_loc["Pc_CmdBadParam"]}",
                _ => $" — {_loc["Pc_CmdFailed"]}",
            };
            AddHistory(new ReceivedItem(DateTimeOffset.Now, _loc["Pc_CmdReceived"], text, IsCommand: true));
            return;
        }

        var title = string.IsNullOrWhiteSpace(message.Title) ? "Home Assistant" : message.Title!;
        if (message.Actions.Count > 0)
        {
            _notifications.ShowWithActions(title, message.Message,
                message.Actions.Select(a => (a.Action, a.Title)).ToList(), message.Tag);
        }
        else
        {
            _notifications.Show(title, message.Message);
        }
        AddHistory(new ReceivedItem(DateTimeOffset.Now, title, message.Message, IsCommand: false));
    }

    private async Task ConfirmSafeAsync(string confirmId)
    {
        try
        {
            await _connection.ConfirmPushAsync(confirmId);
        }
        catch (Exception ex)
        {
            // Not fatal: HA will redeliver and the dedup set absorbs the copy.
            _logger.LogWarning(ex, "Push confirm failed for {ConfirmId}", confirmId);
        }
    }

    private void OnToastAction(object? sender, ToastActionInvokedArgs e)
    {
        // Same event the Android companion fires — HA automations trigger on it.
        _ = _connection.FireEventAsync("mobile_app_notification_action", new Dictionary<string, object?>
        {
            ["action"] = e.Action,
            ["tag"] = e.Tag,
            ["device_id"] = _settings.Load().MobileAppDeviceId,
        });
        _logger.LogInformation("Notification action fired: {Action}", e.Action);
    }

    private void AddHistory(ReceivedItem item) => _ui.Post(() =>
    {
        History.Insert(0, item);
        while (History.Count > HistoryCap)
            History.RemoveAt(History.Count - 1);
    });
}
