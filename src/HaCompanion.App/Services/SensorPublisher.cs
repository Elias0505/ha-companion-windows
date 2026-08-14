// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using HaCompanion.Core.Automations;
using HaCompanion.Core.MobileApp;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Publishes this PC's state to Home Assistant as a mobile_app device ("PC-Sensoren").
/// Opt-in via settings; pushes on every monitor transition (coalesced) plus a 60 s
/// heartbeat that self-heals missed edges.
/// </summary>
public interface ISensorPublisher
{
    /// <summary>Hook the monitor/connection; starts publishing if the toggle is on.</summary>
    void Initialize();

    /// <summary>Turn reporting on: create ids, register device + sensors, start pushing.</summary>
    Task<bool> EnableAsync();

    /// <summary>Turn reporting off (the HA registration is kept; entities go stale).</summary>
    void Disable();

    /// <summary>
    /// Push the current registration data (device name, app version) to HA now via
    /// update_registration. Used when the configurable device name changes (#8) — without it
    /// the rename would only reach HA on the next re-registration.
    /// </summary>
    Task RefreshRegistrationAsync();

    /// <summary>Last push time / error for the settings page status line.</summary>
    string StatusText { get; }

    event EventHandler? StatusChanged;
}

/// <inheritdoc cref="ISensorPublisher"/>
public sealed class SensorPublisher : ISensorPublisher, IDisposable
{
    private const int CoalesceMs = 500;      // a lock fires lock+display_off+app_stop at once
    private const int HeartbeatMs = 60_000;
    private const int RegisterBackoffMs = 300_000;

    private readonly ISettingsStore _settings;
    private readonly IMobileAppClient _client;
    private readonly IWindowsStateMonitor _monitor;
    private readonly IHaConnection _connection;
    private readonly LocalizationService _loc;
    private readonly ILogger<SensorPublisher> _logger;

    private readonly SemaphoreSlim _flight = new(1, 1);
    private Timer? _heartbeat;
    private CancellationTokenSource? _pending; // coalescing window for event pushes
    private long _registerBackoffUntil;
    private int _registering; // 0/1, claimed atomically via Interlocked
    private bool _audioSensorRegistered; // audio value arrives ~6s after start (hysteresis)
    // The webhook id the push channel was last pointed at (null = none). An ID, not a bool:
    // the webhook can change behind this class's back (origin change, config import), and a
    // boolean latch then skipped EnablePushChannel for the NEW id forever — notifications and
    // every HA→PC command stayed dead until restart.
    private string? _pushChannelWebhook;

    public string StatusText { get; private set; } = "";

    public event EventHandler? StatusChanged;

    public SensorPublisher(ISettingsStore settings, IMobileAppClient client, IWindowsStateMonitor monitor,
        IHaConnection connection, LocalizationService loc, ILogger<SensorPublisher> logger)
    {
        _settings = settings;
        _client = client;
        _monitor = monitor;
        _connection = connection;
        _loc = loc;
        _logger = logger;
    }

    public void Initialize()
    {
        _monitor.TriggerFired += OnTrigger;
        _monitor.IdleMinutesChanged += (_, _) => SchedulePush();
        _connection.StatusChanged += (_, status) =>
        {
            if (status == HaConnectionStatus.Connected && _settings.Load().ReportSensors)
                _ = EnsureRegisteredAndPushAsync();
        };
        // The heartbeat also repairs a missing registration (e.g. after a 410 mid-session) AND
        // a push channel that is not yet pointed at the stored webhook — without the stamp
        // check, one transient update_registration failure left notifications and every HA→PC
        // command dead until the next reconnect ("retry on the heartbeat" was a lie).
        _heartbeat = new Timer(_ =>
        {
            var s = _settings.Load();
            if (!s.ReportSensors)
                return;
            var needsRegistration = string.IsNullOrEmpty(s.MobileAppWebhookId)
                || !string.Equals(_pushChannelWebhook, s.MobileAppWebhookId, StringComparison.Ordinal);
            _ = needsRegistration ? EnsureRegisteredAndPushAsync() : PushAsync();
        }, null, HeartbeatMs, HeartbeatMs);

        if (_settings.Load().ReportSensors)
            _ = EnsureRegisteredAndPushAsync();
    }

    public async Task<bool> EnableAsync()
    {
        _settings.Update(s =>
        {
            s.ReportSensors = true;
            if (string.IsNullOrEmpty(s.MobileAppDeviceId))
                s.MobileAppDeviceId = Guid.NewGuid().ToString("N");
        });
        return await EnsureRegisteredAndPushAsync().ConfigureAwait(false);
    }

    public void Disable()
    {
        _settings.Update(s => s.ReportSensors = false);
        _pushChannelWebhook = null; // a fresh Enable must re-establish the push channel
        SetStatus("");
        // Best-effort: hide the entities in HA instead of leaving them stale forever
        // (stale-devices analog). Re-enabling registers them back as enabled.
        _ = DisableRemoteAsync();
    }

    private async Task DisableRemoteAsync()
    {
        try
        {
            var settings = _settings.Load();
            if (string.IsNullOrEmpty(settings.MobileAppWebhookId)
                || _connection.Status != HaConnectionStatus.Connected)
                return;
            await _client.RegisterSensorsAsync(
                settings.MobileAppWebhookId, BuildDefinitions(disabled: true)).ConfigureAwait(false);
            // The tracker would otherwise freeze at its last value ("home") forever.
            if (settings.ReportTrackerHome)
                await PushLocationAsync("not_home", settings.MobileAppWebhookId).ConfigureAwait(false);
            _logger.LogInformation("PC sensors disabled in Home Assistant (reporting turned off)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not disable the PC sensors in Home Assistant");
        }
    }

    // ----- event intake -----

    private void OnTrigger(object? sender, WindowsStateEvent e)
    {
        if (!_settings.Load().ReportSensors)
            return;

        // The machine is going away — push the final state bounded instead of coalesced,
        // or the update loses the race against the network teardown.
        if (e.Trigger is WindowsTrigger.Lock or WindowsTrigger.Suspend or WindowsTrigger.Shutdown)
        {
            try
            {
                PushAsync().Wait(2000);
                // Tracker opt-in: the machine is going away — mark it not_home on the same
                // bounded budget. (A crash or pulled cable cannot run this; the tracker then
                // stays "home" until the next reconnect — documented limitation.)
                var s = _settings.Load();
                if (s.ReportTrackerHome && !string.IsNullOrEmpty(s.MobileAppWebhookId))
                    PushLocationAsync("not_home", s.MobileAppWebhookId).Wait(2000);
            }
            catch
            {
                // best effort by design; the heartbeat corrects after resume
            }
            return;
        }
        SchedulePush();
    }

    private void SchedulePush()
    {
        if (!_settings.Load().ReportSensors)
            return;
        var previous = _pending;
        var cts = _pending = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Delay(CoalesceMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _ = PushAsync();
        }, TaskScheduler.Default);
    }

    // ----- registration + push -----

    private async Task<bool> EnsureRegisteredAndPushAsync()
    {
        if (Environment.TickCount64 < _registerBackoffUntil
            || _connection.Status != HaConnectionStatus.Connected)
            return false; // retried on the next StatusChanged -> Connected

        // Atomic claim: the heartbeat timer, StatusChanged and EnableAsync can all reach here
        // concurrently — a plain check-then-set could register the device twice.
        if (Interlocked.Exchange(ref _registering, 1) == 1)
            return false;
        try
        {
            // Two attempts: a stored-but-dead webhook id costs one round (gone -> cleared),
            // the second registers fresh. Never more — a broken HA answer must not loop.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var settings = _settings.Load();
                if (!settings.ReportSensors)
                    return false;

                if (string.IsNullOrEmpty(settings.MobileAppWebhookId))
                {
                    if (string.IsNullOrEmpty(settings.MobileAppDeviceId))
                    {
                        var newDeviceId = Guid.NewGuid().ToString("N");
                        _settings.Update(s =>
                        {
                            if (string.IsNullOrEmpty(s.MobileAppDeviceId))
                                s.MobileAppDeviceId = newDeviceId;
                        });
                        // Register with what was actually STORED — never a throwaway local id.
                        // The store may refuse the write (settings.json momentarily unreadable);
                        // registering anyway would orphan one HA device per retry.
                        var storedDeviceId = _settings.Load().MobileAppDeviceId;
                        if (string.IsNullOrEmpty(storedDeviceId))
                        {
                            _registerBackoffUntil = Environment.TickCount64 + RegisterBackoffMs;
                            SetStatus(_loc["Set_SensorsError"]);
                            return false;
                        }
                        settings.MobileAppDeviceId = storedDeviceId;
                    }
                    var result = await _client.RegisterAsync(BuildRegistrationRequest(settings.MobileAppDeviceId))
                        .ConfigureAwait(false);
                    if (result is null || string.IsNullOrEmpty(result.WebhookId))
                    {
                        _registerBackoffUntil = Environment.TickCount64 + RegisterBackoffMs;
                        SetStatus(_loc["Set_SensorsError"]);
                        return false;
                    }
                    var webhookId = result.WebhookId;
                    var registeredName = EffectiveDeviceName();
                    _settings.Update(s =>
                    {
                        s.MobileAppWebhookId = webhookId;
                        // HA derives notify.mobile_app_<slug> and the entity_ids from the name
                        // AT THIS MOMENT; remember it so the UI keeps showing the real service
                        // even after a later display rename.
                        s.MobileAppRegisteredName = registeredName;
                    });
                    _logger.LogInformation("mobile_app device registered as {Name}", registeredName);
                }

                // Always the STORED id from here on. If the store refused the write above
                // (settings.json momentarily unreadable) this is empty — back off rather than
                // posting to api/webhook/<empty> and pointing the push channel at nothing.
                var storedWebhook = _settings.Load().MobileAppWebhookId;
                if (string.IsNullOrEmpty(storedWebhook))
                {
                    // The HA-side registration DID happen; that device is now orphaned there.
                    // Name it so the user can clean it up, then back off.
                    _logger.LogWarning(
                        "Registered mobile_app device could not be stored locally (settings.json unwritable); "
                        + "an orphaned device may remain in Home Assistant");
                    _registerBackoffUntil = Environment.TickCount64 + RegisterBackoffMs;
                    SetStatus(_loc["Set_SensorsError"]);
                    return false;
                }

                var outcome = await _client.RegisterSensorsAsync(
                    storedWebhook, BuildDefinitions()).ConfigureAwait(false);
                if (outcome.Outcome == WebhookOutcome.RegistrationGone)
                {
                    MarkGone();
                    continue;
                }
                _audioSensorRegistered = BuildValues().AudioPlaying is not null;

                // Push channel (HA -> PC notifications/commands): older registrations lack
                // app_data — upgrading once per session is idempotent and cheap. Then
                // subscribe the websocket channel so notify.mobile_app_<device> arrives here.
                // Compared by ID, not a done-bool: the webhook can change behind our back
                // (origin change, import, concurrent MarkGone), and only an ID comparison
                // notices and re-points the channel.
                var channelWebhook = storedWebhook;
                if (!string.Equals(_pushChannelWebhook, channelWebhook, StringComparison.Ordinal))
                {
                    // update_registration accepts only a subset of the registration keys —
                    // sending the full request made HA reject (and silently drop) the update.
                    var update = MobileAppRegistrationUpdate.FromRegistration(
                        BuildRegistrationRequest(_settings.Load().MobileAppDeviceId));
                    var updateResult = await _client.UpdateRegistrationAsync(channelWebhook, update).ConfigureAwait(false);
                    if (updateResult.Outcome == WebhookOutcome.RegistrationGone)
                    {
                        // The registration died between RegisterSensors and here — treat it like
                        // every other 410 instead of pointing the channel at a dead webhook.
                        MarkGone();
                        continue;
                    }
                    _connection.EnablePushChannel(channelWebhook);
                    if (updateResult.Outcome == WebhookOutcome.Success)
                    {
                        // Only a successful app_data upgrade makes the channel subscribable for
                        // registrations from older builds — on failure, leave the stamp unset so
                        // the next heartbeat retries the upgrade instead of latching a dead state.
                        _pushChannelWebhook = channelWebhook;
                    }
                    else
                    {
                        _logger.LogWarning("update_registration failed: {Outcome} (HTTP {Status}) — retrying on the next heartbeat",
                            updateResult.Outcome, updateResult.StatusCode);
                    }
                }

                await PushCoreAsync().ConfigureAwait(false);
                await PushLocationAsync("home", storedWebhook).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _registering, 0);
        }
    }

    private async Task PushAsync()
    {
        var gone = false;
        await PushCoreAsync(g => gone = g).ConfigureAwait(false);
        if (gone)
            await EnsureRegisteredAndPushAsync().ConfigureAwait(false); // registers fresh (id cleared)
    }

    private async Task PushCoreAsync(Action<bool>? goneCallback = null)
    {
        var settings = _settings.Load();
        if (!settings.ReportSensors || string.IsNullOrEmpty(settings.MobileAppWebhookId)
            || _connection.Status != HaConnectionStatus.Connected)
            return;

        await _flight.WaitAsync().ConfigureAwait(false);
        try
        {
            var values = BuildValues();

            // The audio probe needs a few seconds before its first value — register the
            // audio sensor late, once, as soon as it reports (registration is idempotent).
            if (!_audioSensorRegistered && values.AudioPlaying is not null)
            {
                _audioSensorRegistered = true;
                await _client.RegisterSensorsAsync(
                    settings.MobileAppWebhookId, BuildDefinitions()).ConfigureAwait(false);
            }

            var result = await _client.UpdateStatesAsync(
                settings.MobileAppWebhookId,
                WindowsSensorCatalog.BuildStates(values)).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case WebhookOutcome.Success:
                    SetStatus(string.Format(CultureInfo.CurrentCulture, _loc["Set_SensorsSent"],
                        DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)));
                    break;
                case WebhookOutcome.RegistrationGone:
                    MarkGone();
                    goneCallback?.Invoke(true);
                    break;
                default:
                    SetStatus(_loc["Set_SensorsError"]);
                    break;
            }
        }
        finally
        {
            _flight.Release();
        }
    }

    /// <summary>The registration was deleted in HA: forget the webhook id; the caller
    /// (or the next heartbeat) registers fresh.</summary>
    private void MarkGone()
    {
        _logger.LogWarning("mobile_app registration gone — re-registering");
        _settings.Update(s => s.MobileAppWebhookId = string.Empty);
        _audioSensorRegistered = false;
        // The push channel was bound to the now-dead webhook id. Clear the one-shot latch so the
        // re-registration below actually re-subscribes it — otherwise notifications and every
        // HA→PC command stay dead until the app restarts.
        _pushChannelWebhook = null;
    }

    /// <summary>
    /// Opt-in device-tracker feed (#11): "home" while connected, "not_home" when the machine
    /// goes away. Deliberately NEVER interprets <see cref="WebhookOutcome.RegistrationGone"/>:
    /// that outcome is derived from HA's empty-200 anti-enumeration answer, and if any HA
    /// version answered update_location with a truly empty body, reacting to it here would
    /// wipe the webhook and loop re-registrations. The sensor push right before this call is
    /// the authoritative liveness check.
    /// </summary>
    private async Task PushLocationAsync(string locationName, string webhookId)
    {
        if (!_settings.Load().ReportTrackerHome)
            return;
        try
        {
            var result = await _client.UpdateLocationAsync(webhookId, locationName).ConfigureAwait(false);
            if (result.Outcome == WebhookOutcome.Success)
                _logger.LogInformation("Device tracker set to {Location}", locationName);
            else
                _logger.LogWarning("update_location ({Location}) failed: {Outcome} (HTTP {Status})",
                    locationName, result.Outcome, result.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "update_location ({Location}) failed", locationName);
        }
    }

    /// <summary>The name this PC registers/updates under in HA — the setting, or the computer name.</summary>
    private string EffectiveDeviceName() =>
        MobileAppDeviceName.Resolve(_settings.Load().HaDeviceName, Environment.MachineName);

    public async Task RefreshRegistrationAsync()
    {
        // Clearing the channel stamp makes EnsureRegisteredAndPushAsync re-run its
        // update_registration block, which carries the (renamed) device_name. Never clear the
        // webhook id for a rename — that would orphan the HA device and register a new one.
        _pushChannelWebhook = null;
        await EnsureRegisteredAndPushAsync().ConfigureAwait(false);
    }

    private MobileAppRegistrationRequest BuildRegistrationRequest(string deviceId) => new(
        deviceId,
        "hacompanion.windows",
        "HA Companion",
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        EffectiveDeviceName(),
        "HA Companion",
        "Windows PC",
        "Windows",
        Environment.OSVersion.Version.ToString(),
        SupportsEncryption: false,
        AppData: MobileAppRegistrationRequest.WebsocketPushAppData);

    private WindowsSensorValues BuildValues()
    {
        var s = _monitor.Current;
        var threshold = _settings.Load().IdleSensorThresholdMinutes;
        return new WindowsSensorValues(
            s.IsLocked, s.SessionState,
            IsIdle: s.IdleMinutes >= threshold, s.IdleMinutes,
            s.ForegroundProcess, s.IsFullscreen, s.MicInUse, s.CamInUse,
            s.DisplayOn, s.AudioPlaying, s.AppStartedAt,
            IsOwnAppForeground: s.IsOwnAppForeground);
    }

    private IReadOnlyList<SensorDefinition> BuildDefinitions(bool disabled = false)
    {
        var names = new Dictionary<string, string>();
        foreach (var id in new[]
                 {
                     "is_locked", "session_state", "is_idle", "idle_minutes", "active_program",
                     "fullscreen", "microphone_in_use", "camera_in_use", "display_on",
                     "audio_playing", "last_start",
                 })
            names[id] = _loc["Sensor_" + id];
        return WindowsSensorCatalog.BuildDefinitions(names, BuildValues(), disabled);
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _pending?.Cancel();
        _flight.Dispose();
    }
}
