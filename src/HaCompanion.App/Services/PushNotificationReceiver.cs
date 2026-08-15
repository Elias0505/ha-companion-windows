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
    // in arrival order, off the WebSocket dispatch thread. BOUNDED on purpose — each
    // queued item holds a private clone of the payload (up to the 32 MB receive cap),
    // and the WebSocket producer never waits for this reader, so an unbounded queue let
    // a push flood grow to gigabytes.
    private const int QueueCap = 64;

    // Under flood the OLDEST queued delivery is evicted — the newest is the command the user just
    // triggered. TryWrite cannot report this (it returns true for every Drop* mode), so the drop
    // is observed through the itemDropped callback: the evicted delivery's confirm id is released
    // again, otherwise it would stay recorded as "handled", HA's redelivery would be suppressed
    // as a duplicate and re-confirmed, and the command would be lost for good.
    private readonly Channel<(JsonElement Payload, PushMessage Message)> _queue;

    // Rate limits per command kind: a caller who can invoke notify.mobile_app_<device> in
    // a loop must not be able to spawn processes without bound. Volume/mute stay responsive
    // (a slider drags through many values), the destructive ones are throttled hard.
    private static readonly Dictionary<PcCommand, TimeSpan> MinInterval = new()
    {
        [PcCommand.Launch] = TimeSpan.FromSeconds(5),
        [PcCommand.CloseApp] = TimeSpan.FromSeconds(5),
        [PcCommand.Shutdown] = TimeSpan.FromSeconds(60),
        [PcCommand.Sleep] = TimeSpan.FromSeconds(10),
        [PcCommand.Lock] = TimeSpan.FromSeconds(2),
        [PcCommand.MonitorOff] = TimeSpan.FromSeconds(2),
    };

    private readonly Dictionary<PcCommand, long> _lastRun = new();
    private long _lastDropLogMs = -6000; // throttles the queue-full warning (one per burst); negative
                                         // start so a flood within the first seconds of uptime still logs

    /// <summary>False when this command came too soon after the previous one of its kind.</summary>
    private bool RateLimitAllows(PcCommand command)
    {
        if (!MinInterval.TryGetValue(command, out var minimum))
            return true; // volume/mute: not destructive, never throttled
        var now = Environment.TickCount64;
        if (_lastRun.TryGetValue(command, out var last) && now - last < minimum.TotalMilliseconds)
            return false;
        _lastRun[command] = now;
        return true;
    }

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

        // Built here rather than in a field initializer: the drop callback needs _logger and
        // _seenConfirmIds. It is the only way to learn that the queue evicted something —
        // TryWrite returns true for every Drop* mode.
        _queue = Channel.CreateBounded<(JsonElement, PushMessage)>(
            new BoundedChannelOptions(QueueCap)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            itemDropped: dropped =>
            {
                // Release the id so a redelivery is not suppressed as a duplicate. (Best effort:
                // if the drop follows a duplicate we already confirmed, HA considers it delivered
                // and won't retry — under flood, losing the oldest is the least-bad option.)
                if (dropped.Item2.ConfirmId is { } confirmId)
                    _seenConfirmIds.Forget(confirmId);
                // Eviction IS the flood path and this runs on the WebSocket dispatch thread —
                // log at most once per burst instead of once per dropped item.
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastDropLogMs);
                if (now - last > 5000 && Interlocked.CompareExchange(ref _lastDropLogMs, now, last) == last)
                    _logger.LogWarning("Push queue full; dropping oldest deliveries");
            });
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

    // A push notification has no business being anywhere near this size; the WebSocket cap is
    // 32 MB per frame, and QUEUING such clones is how a flood once grew to gigabytes.
    private const int MaxQueuedPayloadBytes = 512 * 1024;

    private void OnPush(object? sender, JsonElement payload)
    {
        try
        {
            if (!PushMessageParser.TryParse(payload, out var message))
                return;

            // Byte-bound BEFORE the clone: the count bound alone still allowed 64 × 32 MB.
            // Confirm (so HA stops redelivering the oversized blob) but never queue or run it.
            var rawLength = payload.GetRawText().Length;
            if (rawLength > MaxQueuedPayloadBytes)
            {
                _logger.LogWarning("Discarding an oversized push delivery ({Size} chars)", rawLength);
                if (message.ConfirmId is { } oversized)
                    _ = ConfirmSafeAsync(oversized);
                return;
            }

            if (message.ConfirmId is { } confirmId && !_seenConfirmIds.TryAdd(confirmId))
            {
                // Redelivery of something already handled (or in flight): the earlier
                // confirm was lost, so confirm again — but never execute again.
                _logger.LogInformation("Duplicate push delivery {ConfirmId} suppressed",
                    PcCommands.ForLog(confirmId));
                _ = ConfirmSafeAsync(confirmId);
                return;
            }

            // Clone: the payload's backing JsonDocument dies with this event handler.
            // If the queue is full this evicts the OLDEST entry; the drop callback in the
            // constructor releases that entry's confirm id so HA can redeliver it.
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

            if (!RateLimitAllows(command))
            {
                _logger.LogWarning("PC command {Command} rate-limited", command);
                AddHistory(new ReceivedItem(DateTimeOffset.Now, _loc["Pc_CmdReceived"],
                    $"{_loc["Cmd_" + PcCommands.ToKey(command)]} — {_loc["Pc_CmdThrottled"]}", IsCommand: true));
                return;
            }

            var result = _executor.Execute(command, param);
            var text = _loc["Cmd_" + PcCommands.ToKey(command)];
            if (!string.IsNullOrWhiteSpace(param))
                text += $" {PcCommands.ForLog(param, 60)}"; // control chars out, length capped
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
            _logger.LogWarning(ex, "Push confirm failed for {ConfirmId}", PcCommands.ForLog(confirmId));
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
        _logger.LogInformation("Notification action fired: {Action}", PcCommands.ForLog(e.Action));
    }

    private void AddHistory(ReceivedItem item) => _ui.Post(() =>
    {
        History.Insert(0, item);
        while (History.Count > HistoryCap)
            History.RemoveAt(History.Count - 1);
    });
}
