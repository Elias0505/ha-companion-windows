// SPDX-License-Identifier: AGPL-3.0-only
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
    private bool _pushChannelReady;      // update_registration + channel subscribe, once per session

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
        // The heartbeat also repairs a missing registration (e.g. after a 410 mid-session).
        _heartbeat = new Timer(_ =>
        {
            var s = _settings.Load();
            if (!s.ReportSensors)
                return;
            _ = string.IsNullOrEmpty(s.MobileAppWebhookId) ? EnsureRegisteredAndPushAsync() : PushAsync();
        }, null, HeartbeatMs, HeartbeatMs);

        if (_settings.Load().ReportSensors)
            _ = EnsureRegisteredAndPushAsync();
    }

    public async Task<bool> EnableAsync()
    {
        var settings = _settings.Load();
        settings.ReportSensors = true;
        if (string.IsNullOrEmpty(settings.MobileAppDeviceId))
            settings.MobileAppDeviceId = Guid.NewGuid().ToString("N");
        _settings.Save(settings);
        return await EnsureRegisteredAndPushAsync().ConfigureAwait(false);
    }

    public void Disable()
    {
        var settings = _settings.Load();
        settings.ReportSensors = false;
        _settings.Save(settings);
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
                        settings.MobileAppDeviceId = Guid.NewGuid().ToString("N");
                        _settings.Save(settings);
                    }
                    var result = await _client.RegisterAsync(BuildRegistrationRequest(settings.MobileAppDeviceId))
                        .ConfigureAwait(false);
                    if (result is null || string.IsNullOrEmpty(result.WebhookId))
                    {
                        _registerBackoffUntil = Environment.TickCount64 + RegisterBackoffMs;
                        SetStatus(_loc["Set_SensorsError"]);
                        return false;
                    }
                    settings = _settings.Load();
                    settings.MobileAppWebhookId = result.WebhookId;
                    _settings.Save(settings);
                    _logger.LogInformation("mobile_app device registered as {Name}", Environment.MachineName);
                }

                var outcome = await _client.RegisterSensorsAsync(
                    _settings.Load().MobileAppWebhookId, BuildDefinitions()).ConfigureAwait(false);
                if (outcome.Outcome == WebhookOutcome.RegistrationGone)
                {
                    MarkGone();
                    continue;
                }
                _audioSensorRegistered = BuildValues().AudioPlaying is not null;

                // Push channel (HA -> PC notifications/commands): older registrations lack
                // app_data — upgrading once per session is idempotent and cheap. Then
                // subscribe the websocket channel so notify.mobile_app_<device> arrives here.
                if (!_pushChannelReady)
                {
                    _pushChannelReady = true;
                    var webhookId = _settings.Load().MobileAppWebhookId;
                    await _client.UpdateRegistrationAsync(webhookId,
                        BuildRegistrationRequest(_settings.Load().MobileAppDeviceId)).ConfigureAwait(false);
                    _connection.EnablePushChannel(webhookId);
                }

                await PushCoreAsync().ConfigureAwait(false);
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
                    SetStatus(string.Format(_loc["Set_SensorsSent"], DateTime.Now.ToString("HH:mm:ss")));
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
        var settings = _settings.Load();
        settings.MobileAppWebhookId = string.Empty;
        _settings.Save(settings);
        _audioSensorRegistered = false;
    }

    private static MobileAppRegistrationRequest BuildRegistrationRequest(string deviceId) => new(
        deviceId,
        "hacompanion.windows",
        "HA Companion",
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Environment.MachineName,
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
            s.DisplayOn, s.AudioPlaying, s.AppStartedAt);
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
