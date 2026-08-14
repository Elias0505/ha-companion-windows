// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.App.Views;
using HaCompanion.Core.Configuration;
using HaCompanion.Core.Rest;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>An entry in the "default view when the panel opens" picker.</summary>
public sealed record StartViewOption(string Value, string Label);

/// <summary>An entry in the "which display docks the panel" picker.</summary>
public sealed record MonitorOption(string Value, string Label);

/// <summary>Backing view model for the settings page: connection, hotkey and panel behaviour.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ShellViewModel _shell;
    private readonly IHotkeyService _hotkeys;
    private readonly LocalizationService _localization;
    private readonly IQuickPanelController _quickPanel;
    private readonly IHaConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly IStartupService _startup;
    private readonly ISensorPublisher _sensors;
    private readonly INotificationService _notifications;
    private bool _loading;

    // The URL/token as they were loaded from the store. Needed to tell "the user typed a NEW
    // token for the new host" (keep it) from "this is the token stored for the OLD host"
    // (must be dropped before we talk to a different origin).
    private string _loadedBaseUrl = string.Empty;
    private string _loadedToken = string.Empty;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _ignoreCertificateErrors;

    /// <summary>Status line for the "search the network" (mDNS) button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscoverStatus))]
    private string _discoverStatus = string.Empty;

    public bool HasDiscoverStatus => !string.IsNullOrEmpty(DiscoverStatus);

    [ObservableProperty]
    private bool _isDiscovering;

    [ObservableProperty]
    private string _hotkey = "Win+Ctrl+H";

    [ObservableProperty]
    private bool _autoHideQuickPanel = true;

    [ObservableProperty]
    private double _quickPanelWidth = 400;

    /// <summary>Choices for what the quick panel shows on open (last / favourites / a dashboard).</summary>
    public ObservableCollection<StartViewOption> StartViewOptions { get; } = new();

    [ObservableProperty]
    private StartViewOption? _selectedStartView;

    [ObservableProperty]
    private bool _quickPanelDragResize = true;

    /// <summary>Choices for which display docks the quick panel (primary + each monitor).</summary>
    public ObservableCollection<MonitorOption> MonitorOptions { get; } = new();

    [ObservableProperty]
    private MonitorOption? _selectedMonitor;

    /// <summary>Start with Windows — backed directly by the registry Run key (not settings.json).</summary>
    [ObservableProperty]
    private bool _autostart;

    [ObservableProperty]
    private bool _showHaNotifications = true;

    /// <summary>Report the PC's state to HA as a mobile_app device (opt-in).</summary>
    [ObservableProperty]
    private bool _reportSensors;

    /// <summary>Device name shown in HA; empty = this PC's computer name (#8).</summary>
    [ObservableProperty]
    private string _haDeviceName = string.Empty;

    /// <summary>Placeholder for the device-name box — what "empty" resolves to.</summary>
#pragma warning disable CA1822 // bound from XAML; must be an instance property
    public string DeviceNamePlaceholder => Environment.MachineName;
#pragma warning restore CA1822

    /// <summary>Opt-in: device tracker "home" while connected, "not_home" on lock/suspend (#11).</summary>
    [ObservableProperty]
    private bool _reportTrackerHome;

    /// <summary>Heading (attribution) of Windows toasts; empty = "HA Companion" (#9).</summary>
    [ObservableProperty]
    private string _toastAppName = string.Empty;

    [ObservableProperty]
    private double _idleSensorMinutes = 5;

    [ObservableProperty]
    private string _sensorStatus = string.Empty;

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private string _hotkeyStatus = string.Empty;

    public IReadOnlyList<LanguageOption> Languages => _localization.Languages;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> HotkeyPresets { get; } = new()
    {
        "Win+Ctrl+H", "Ctrl+Alt+H", "Ctrl+Shift+H", "Ctrl+Alt+Space", "Win+Ctrl+Space", "Ctrl+Alt+A",
    };

    public SettingsViewModel(ISettingsStore settingsStore, ShellViewModel shell, IHotkeyService hotkeys, LocalizationService localization, IQuickPanelController quickPanel, IHaConnection connection, IUiDispatcher ui, IStartupService startup, ISensorPublisher sensors, INotificationService notifications)
    {
        _notifications = notifications;
        _settingsStore = settingsStore;
        _shell = shell;
        _hotkeys = hotkeys;
        _localization = localization;
        _quickPanel = quickPanel;
        _connection = connection;
        _ui = ui;
        _startup = startup;
        _sensors = sensors;
        _sensors.StatusChanged += (_, _) => _ui.Post(() => SensorStatus = _sensors.StatusText);
        Load();
        // Keep localized texts in sync when the UI language changes.
        _localization.LanguageChanged += (_, _) =>
        {
            RefreshHotkeyStatus();
            RebuildStartViewOptions(_settingsStore.Load().QuickPanelStartView);
            RebuildMonitorOptions(_settingsStore.Load().QuickPanelMonitor);
            _ = LoadStartViewDashboardsAsync();
        };
        _ = LoadStartViewDashboardsAsync();
    }

    /// <summary>Re-read all settings from the store (e.g. after a config import) so the
    /// page never shows values that no longer match settings.json.</summary>
    public void ReloadFromSettings() => Load();

    private void Load()
    {
        _loading = true;
        var settings = _settingsStore.Load();
        BaseUrl = settings.BaseUrl;
        Token = settings.Token;
        _loadedBaseUrl = settings.BaseUrl;
        _loadedToken = settings.Token;
        IgnoreCertificateErrors = settings.IgnoreCertificateErrors;
        Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "Win+Ctrl+H" : settings.Hotkey;
        AutoHideQuickPanel = settings.AutoHideQuickPanel;
        QuickPanelWidth = settings.QuickPanelWidth;
        QuickPanelDragResize = settings.QuickPanelDragResize;
        Autostart = _startup.IsEnabled;
        ShowHaNotifications = settings.ShowHaNotifications;
        ReportSensors = settings.ReportSensors;
        HaDeviceName = settings.HaDeviceName;
        ReportTrackerHome = settings.ReportTrackerHome;
        ToastAppName = settings.ToastAppName;
        IdleSensorMinutes = settings.IdleSensorThresholdMinutes;
        SensorStatus = _sensors.StatusText;
        SelectedLanguage = _localization.Languages.FirstOrDefault(l => l.Code == settings.Language)
                           ?? _localization.Languages[0];
        if (!HotkeyPresets.Contains(Hotkey))
            HotkeyPresets.Insert(0, Hotkey);
        RebuildStartViewOptions(settings.QuickPanelStartView);
        RebuildMonitorOptions(settings.QuickPanelMonitor);
        _loading = false;
        RefreshHotkeyStatus();
    }

    /// <summary>
    /// (Re)build the display picker: "primary display" plus every attached monitor.
    /// A stored display that is currently absent selects the primary entry — the
    /// runtime falls back the same way, and re-attaching the screen brings the entry
    /// back on the next settings visit (<see cref="RefreshMonitorOptions"/>).
    /// </summary>
    private void RebuildMonitorOptions(string currentValue)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            MonitorOptions.Clear();
            MonitorOptions.Add(new MonitorOption(MonitorCatalog.PrimaryKey, _localization["Set_MonitorPrimary"]));
            foreach (var m in MonitorCatalog.Enumerate())
            {
                // \\.\DISPLAY3 -> "3"; fall back to the raw device name if the shape changes.
                var number = m.DeviceKey.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase)
                    ? m.DeviceKey[@"\\.\DISPLAY".Length..]
                    : m.DeviceKey;
                MonitorOptions.Add(new MonitorOption(m.DeviceKey,
                    string.Format(CultureInfo.CurrentCulture, _localization["Set_MonitorItem"],
                        number, $"{m.Width}×{m.Height}")));
            }
            SelectedMonitor = MonitorOptions.FirstOrDefault(o => o.Value == currentValue) ?? MonitorOptions[0];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>Re-enumerate displays (topology may have changed while the page was cached).</summary>
    public void RefreshMonitorOptions() =>
        RebuildMonitorOptions(_settingsStore.Load().QuickPanelMonitor);

    /// <summary>
    /// (Re)build the "default view" picker: last / favourites, plus the stored dashboard value
    /// as a placeholder until <see cref="LoadStartViewDashboardsAsync"/> fills in real titles.
    /// </summary>
    private void RebuildStartViewOptions(string currentValue, IReadOnlyList<Core.Models.HaDashboardInfo>? dashboards = null)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            StartViewOptions.Clear();
            StartViewOptions.Add(new StartViewOption("last", _localization["Set_StartLast"]));
            StartViewOptions.Add(new StartViewOption("favorites", _localization["Fav_Title"]));
            if (dashboards is not null)
            {
                foreach (var d in dashboards)
                    StartViewOptions.Add(new StartViewOption($"dash:{d.UrlPath ?? ""}", d.Title));
            }

            var selected = StartViewOptions.FirstOrDefault(o => o.Value == currentValue);
            if (selected is null && currentValue.StartsWith("dash:", StringComparison.Ordinal))
            {
                // Keep an unknown stored dashboard selectable until the real list arrives.
                selected = new StartViewOption(currentValue, currentValue["dash:".Length..]);
                StartViewOptions.Add(selected);
            }
            SelectedStartView = selected ?? StartViewOptions[0];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private IReadOnlyList<Core.Models.HaDashboardInfo>? _startViewDashboards;

    /// <summary>Re-read the stored default view (the panel's pin button may have changed it).</summary>
    public void RefreshStartViewSelection() =>
        RebuildStartViewOptions(_settingsStore.Load().QuickPanelStartView, _startViewDashboards);

    /// <summary>Fill the picker with the real HA dashboards (best-effort; needs a connection).</summary>
    private async Task LoadStartViewDashboardsAsync()
    {
        try
        {
            var dashboards = await _connection.ListDashboardsAsync();
            _startViewDashboards = dashboards;
            _ui.Post(() => RebuildStartViewOptions(_settingsStore.Load().QuickPanelStartView, dashboards));
        }
        catch
        {
            // Not connected yet — the picker still offers last/favourites; retried after connect.
        }
    }

    /// <summary>
    /// The candidate connection settings for "Save &amp; Connect" — the ONLY fields this page
    /// owns as a group. Everything else is persisted field-by-field by its own handler.
    ///
    /// This used to write every page field from view-model state, which silently reverted
    /// settings the view model had never reloaded: a panel width the user had dragged (the
    /// panel stores it directly), or the chosen display after undocking, where the picker
    /// falls back to "primary" because the stored display is momentarily absent.
    /// </summary>
    private AppSettings BuildSettings()
    {
        var settings = _settingsStore.Load();
        settings.BaseUrl = BaseUrl.Trim();
        settings.Token = Token.Trim();
        settings.IgnoreCertificateErrors = IgnoreCertificateErrors;
        return settings;
    }

    partial void OnAutoHideQuickPanelChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Update(s => s.AutoHideQuickPanel = value);
    }

    partial void OnQuickPanelWidthChanged(double value)
    {
        if (_loading)
            return;
        _settingsStore.Update(s => s.QuickPanelWidth = (int)value);
        _quickPanel.PreviewWidth(); // show the panel live so the user sees the size
    }

    partial void OnSelectedStartViewChanged(StartViewOption? value)
    {
        if (!_loading && value is not null)
            _settingsStore.Update(s => s.QuickPanelStartView = value.Value);
    }

    partial void OnSelectedMonitorChanged(MonitorOption? value)
    {
        if (_loading || value is null)
            return;
        _settingsStore.Update(s => s.QuickPanelMonitor = value.Value);
        _quickPanel.PreviewWidth(); // slide the panel in on the newly chosen display
    }

    partial void OnShowHaNotificationsChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Update(s => s.ShowHaNotifications = value);
    }

    partial void OnAutostartChanged(bool value)
    {
        if (!_loading)
            _startup.SetEnabled(value);
    }

    partial void OnReportSensorsChanged(bool value)
    {
        if (_loading)
            return;
        // The publisher persists the toggle itself (it also owns device/webhook ids).
        if (value)
            _ = _sensors.EnableAsync();
        else
            _sensors.Disable();
    }

    partial void OnIdleSensorMinutesChanged(double value)
    {
        if (_loading)
            return;
        _settingsStore.Update(s => s.IdleSensorThresholdMinutes = (int)value);
    }

    partial void OnHaDeviceNameChanged(string value)
    {
        if (_loading)
            return;
        _settingsStore.Update(s => s.HaDeviceName = value);
        // Push the rename to HA right away (update_registration carries device_name);
        // without this it would only arrive on the next re-registration.
        if (ReportSensors)
            _ = _sensors.RefreshRegistrationAsync();
    }

    partial void OnReportTrackerHomeChanged(bool value)
    {
        if (_loading)
            return;
        _settingsStore.Update(s => s.ReportTrackerHome = value);
        // Turning it ON should mark "home" promptly rather than at the next heartbeat.
        if (value && ReportSensors)
            _ = _sensors.RefreshRegistrationAsync();
    }

    partial void OnToastAppNameChanged(string value)
    {
        if (_loading)
            return;
        _settingsStore.Update(s => s.ToastAppName = value);
        _notifications.ApplyDisplayName(value); // re-registers; applies to NEW toasts
    }

    partial void OnQuickPanelDragResizeChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Update(s => s.QuickPanelDragResize = value);
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null)
            return;
        _localization.SetLanguage(value.Code);
        _settingsStore.Update(s => s.Language = value.Code);
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_loading || string.IsNullOrWhiteSpace(value))
            return;
        if (!HotkeyPresets.Contains(value))
            HotkeyPresets.Insert(0, value); // keep a captured custom combo visible in the dropdown
        _settingsStore.Update(s => s.Hotkey = value);
        _hotkeys.Register(value);
        RefreshHotkeyStatus();
    }

    /// <summary>App version shown at the bottom of the settings page.</summary>
#pragma warning disable CA1822
    public string AppVersion =>
        "HA Companion " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");
#pragma warning restore CA1822

    /// <summary>Localized prompt shown while the user is recording a custom hotkey.</summary>
    public string RecordPrompt => _localization["Set_RecordPrompt"];

    /// <summary>Re-derives the hotkey status text (used to cancel an in-progress capture).</summary>
    public void RefreshHotkeyStatusPublic() => RefreshHotkeyStatus();

    private void RefreshHotkeyStatus() =>
        HotkeyStatus = string.Format(CultureInfo.CurrentCulture,
            _localization[_hotkeys.IsRegistered ? "Set_HotkeyActive" : "Set_HotkeyFailed"], Hotkey);

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        var settings = BuildSettings();
        if (!settings.HasConnection)
        {
            StatusMessage = _localization["Set_MsgNeedBoth"];
            return;
        }

        // Check the URL shape BEFORE anything touches it: HaConnection.CheckAsync throws on a
        // schemeless value ("homeassistant.local:8123"), which used to escape into the global
        // handler and leave the page stuck on "Connecting…" forever.
        if (!HaConnectionSettings.IsUsableBaseUrl(settings.BaseUrl))
        {
            StatusMessage = _localization["Set_MsgBadUrl"];
            return;
        }

        // A different origin means a different Home Assistant. The stored token belongs to the
        // OLD one, so it must never ride along on the probe — that single request is how a
        // spoofed LAN instance (or a mistyped host) would capture it. The webhook id and device
        // id are host-scoped credentials too and go with it.
        // The loaded snapshot can be empty for the wrong reason: a transient read failure while
        // the page loaded handed out defaults. Re-read before judging, or saving the SAME host
        // again would look like an origin change and needlessly drop webhook/device identity.
        if (string.IsNullOrEmpty(_loadedBaseUrl))
        {
            var stored = _settingsStore.Load();
            _loadedBaseUrl = stored.BaseUrl;
            if (string.IsNullOrEmpty(_loadedToken))
                _loadedToken = stored.Token;
        }

        // An http→https upgrade of the same host is strictly safer and keeps the credentials;
        // anything else that changes scheme/host/port is a different Home Assistant.
        var originChanged = !HaConnectionSettings.IsSameOrigin(_loadedBaseUrl, settings.BaseUrl)
                            && !HaConnectionSettings.IsSchemeUpgrade(_loadedBaseUrl, settings.BaseUrl);
        if (originChanged && string.Equals(settings.Token, _loadedToken.Trim(), StringComparison.Ordinal))
        {
            // The token field still holds the previous host's secret. Refuse to send it — that
            // single probe request is how a spoofed LAN instance (or a typo'd host) would
            // capture it — but destroy NOTHING: a typo is fixed by correcting the URL, with the
            // stored setup intact. Connecting to a genuinely new instance requires pasting the
            // token that belongs to it.
            StatusMessage = _localization["Set_MsgHostChanged"];
            return;
        }
        // (A changed origin with a NEWLY typed token is allowed through; the webhook/device
        // identity of the old host is dropped when the new settings are persisted below.)

        IsBusy = true;
        StatusMessage = _localization["St_Connecting"];
        try
        {
            // test-before-configure: probe the candidate settings first — on failure NOTHING
            // is persisted and the previously working connection stays untouched.
            var check = await _connection.CheckAsync(settings.ToConnectionSettings());
            if (!check.IsOk)
            {
                StatusMessage = FormatCheck(check);
                return;
            }

            // Persist field-by-field, not as the snapshot taken before the probe: the await above
            // gives background components (sensor heartbeat) time to write their own fields, and
            // writing the stale snapshot back would revert them.
            var newBaseUrl = settings.BaseUrl;
            var newToken = settings.Token;
            var newIgnoreCert = settings.IgnoreCertificateErrors;
            var dropHostCredentials = originChanged;
            if (dropHostCredentials)
            {
                // BEFORE the store write, kill everything that could act on the old host in
                // the switch window: the still-connected session would let the sensor
                // heartbeat register a FRESH webhook on the old host mid-switch (which the
                // very next push would then hand to the new one), and the WebSocket layer
                // holds a live copy of the old webhook id that the next connect would
                // otherwise subscribe against the new host.
                _connection.Disconnect();
                _connection.EnablePushChannel(null);
                _settingsStore.DiscardPreservedSecrets();
            }
            _settingsStore.Update(s =>
            {
                s.BaseUrl = newBaseUrl;
                s.Token = newToken;
                s.IgnoreCertificateErrors = newIgnoreCert;
                if (dropHostCredentials)
                {
                    // The webhook/device identity belongs to the previous host.
                    s.MobileAppWebhookId = string.Empty;
                    s.MobileAppDeviceId = string.Empty;
                }
            });
            // Update() declines to persist while settings.json is unreadable (AV/backup lock).
            // Connecting anyway would run the app on settings the disk does not hold — and on
            // an origin change it would leave the OLD credentials stored while we talk to the
            // NEW host. Verify BOTH fields (a same-origin token rotation changes only the
            // token), and stop honestly if the write did not land.
            var persistedNow = _settingsStore.Load();
            if (!string.Equals(persistedNow.BaseUrl, settings.BaseUrl, StringComparison.Ordinal)
                || !string.Equals(persistedNow.Token, settings.Token, StringComparison.Ordinal))
            {
                StatusMessage = _localization["Set_MsgSaveFailed"];
                return;
            }
            _loadedBaseUrl = settings.BaseUrl;
            _loadedToken = settings.Token;
            // The WebViews captured the previous URL/token in their navigation, certificate and
            // auth handlers — rebuild them, or they keep enforcing the old origin's rules (and
            // carrying the old token) until the app restarts.
            _quickPanel.ResetWebView();
            HaDashboardsPage.RequestReset();
            var result = await _shell.ConnectAsync(settings);
            StatusMessage = FormatCheck(result);
            if (result.IsOk)
                _ = LoadStartViewDashboardsAsync(); // the picker can now list real dashboards
        }
        finally
        {
            // Always release the busy state: an exception here used to leave the page stuck.
            IsBusy = false;
        }
    }

    private string FormatCheck(ConnectionCheckResult result) =>
        result.Status == ConnectionCheckStatus.HttpError
            ? string.Format(CultureInfo.CurrentCulture, _localization[result.I18nKey], result.HttpStatusCode)
            : _localization[result.I18nKey];

    /// <summary>Drop control characters from an mDNS-supplied string before it reaches the UI.</summary>
    private static string Sanitize(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>Search the local network for Home Assistant (mDNS) and fill in the base URL.</summary>
    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsDiscovering)
            return;
        IsDiscovering = true;
        DiscoverStatus = _localization["Set_DiscoverBusy"];
        try
        {
            var found = await HaCompanion.Core.Discovery.MdnsDiscovery
                .DiscoverAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            // Anyone on the LAN can answer an mDNS query, so a response is untrusted input:
            // keep only absolute http(s) URLs (no file:/javascript:/garbage) and strip control
            // characters before anything reaches the UI.
            var withUrl = found
                .Where(i => !string.IsNullOrWhiteSpace(i.BaseUrl))
                .Select(i => Sanitize(i.BaseUrl!))
                // Validate AFTER sanitising: the string offered to the UI must be the exact
                // one that passed the http(s) check, not a pre-cleanup variant of it.
                .Where(HaConnectionSettings.IsUsableBaseUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (withUrl.Count == 0)
            {
                DiscoverStatus = _localization["Set_DiscoverNone"];
            }
            else
            {
                // One hit fills the box directly; several list their URLs for the user to copy.
                // Filling the box is safe on its own (no request is sent here) — EXCEPT when the
                // token field already holds a secret and the discovered URL points somewhere
                // else: the connect path only protects the STORED token, so silently swapping
                // the URL under a freshly pasted one would aim that secret at whichever LAN
                // device answered first. In that case only list the URL; the user copies it
                // deliberately. The token field itself is never wiped here.
                var candidate = withUrl[0];
                var tokenAtRisk = !string.IsNullOrWhiteSpace(Token)
                                  && !HaConnectionSettings.IsSameOrigin(BaseUrl, candidate);
                if (withUrl.Count == 1 && !tokenAtRisk)
                {
                    BaseUrl = candidate;
                    DiscoverStatus = string.Format(CultureInfo.CurrentCulture, _localization["Set_DiscoverFound"], candidate);
                }
                else
                {
                    DiscoverStatus = string.Join("  •  ", withUrl);
                }
            }
        }
        finally
        {
            IsDiscovering = false;
        }
    }
}
