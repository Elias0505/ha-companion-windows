// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.Core.Rest;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>An entry in the "default view when the panel opens" picker.</summary>
public sealed record StartViewOption(string Value, string Label);

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
    private bool _loading;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _ignoreCertificateErrors;

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

    /// <summary>Start with Windows — backed directly by the registry Run key (not settings.json).</summary>
    [ObservableProperty]
    private bool _autostart;

    [ObservableProperty]
    private bool _showHaNotifications = true;

    /// <summary>Report the PC's state to HA as a mobile_app device (opt-in).</summary>
    [ObservableProperty]
    private bool _reportSensors;

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

    public SettingsViewModel(ISettingsStore settingsStore, ShellViewModel shell, IHotkeyService hotkeys, LocalizationService localization, IQuickPanelController quickPanel, IHaConnection connection, IUiDispatcher ui, IStartupService startup, ISensorPublisher sensors)
    {
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
            _ = LoadStartViewDashboardsAsync();
        };
        _ = LoadStartViewDashboardsAsync();
    }

    private void Load()
    {
        _loading = true;
        var settings = _settingsStore.Load();
        BaseUrl = settings.BaseUrl;
        Token = settings.Token;
        IgnoreCertificateErrors = settings.IgnoreCertificateErrors;
        Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "Win+Ctrl+H" : settings.Hotkey;
        AutoHideQuickPanel = settings.AutoHideQuickPanel;
        QuickPanelWidth = settings.QuickPanelWidth;
        QuickPanelDragResize = settings.QuickPanelDragResize;
        Autostart = _startup.IsEnabled;
        ShowHaNotifications = settings.ShowHaNotifications;
        ReportSensors = settings.ReportSensors;
        IdleSensorMinutes = settings.IdleSensorThresholdMinutes;
        SensorStatus = _sensors.StatusText;
        SelectedLanguage = _localization.Languages.FirstOrDefault(l => l.Code == settings.Language)
                           ?? _localization.Languages[0];
        if (!HotkeyPresets.Contains(Hotkey))
            HotkeyPresets.Insert(0, Hotkey);
        RebuildStartViewOptions(settings.QuickPanelStartView);
        _loading = false;
        RefreshHotkeyStatus();
    }

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

    private AppSettings BuildSettings()
    {
        // Start from the stored settings and overwrite only what this page owns — fields
        // managed elsewhere (e.g. the panel's sort toggle) must survive a settings save.
        var settings = _settingsStore.Load();
        settings.BaseUrl = BaseUrl.Trim();
        settings.Token = Token.Trim();
        settings.IgnoreCertificateErrors = IgnoreCertificateErrors;
        settings.Hotkey = Hotkey;
        settings.AutoHideQuickPanel = AutoHideQuickPanel;
        settings.QuickPanelWidth = (int)QuickPanelWidth;
        settings.Language = SelectedLanguage?.Code ?? "en";
        settings.QuickPanelStartView = SelectedStartView?.Value ?? "last";
        settings.QuickPanelDragResize = QuickPanelDragResize;
        settings.ShowHaNotifications = ShowHaNotifications;
        return settings;
    }

    partial void OnAutoHideQuickPanelChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnQuickPanelWidthChanged(double value)
    {
        if (_loading)
            return;
        _settingsStore.Save(BuildSettings());
        _quickPanel.PreviewWidth(); // show the panel live so the user sees the size
    }

    partial void OnSelectedStartViewChanged(StartViewOption? value)
    {
        if (!_loading && value is not null)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnShowHaNotificationsChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
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
        var settings = _settingsStore.Load();
        settings.IdleSensorThresholdMinutes = (int)value;
        _settingsStore.Save(settings);
    }

    partial void OnQuickPanelDragResizeChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null)
            return;
        _localization.SetLanguage(value.Code);
        _settingsStore.Save(BuildSettings());
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_loading || string.IsNullOrWhiteSpace(value))
            return;
        if (!HotkeyPresets.Contains(value))
            HotkeyPresets.Insert(0, value); // keep a captured custom combo visible in the dropdown
        _settingsStore.Save(BuildSettings());
        _hotkeys.Register(value);
        RefreshHotkeyStatus();
    }

    /// <summary>App version shown at the bottom of the settings page.</summary>
    public string AppVersion =>
        "HA Companion " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    /// <summary>Localized prompt shown while the user is recording a custom hotkey.</summary>
    public string RecordPrompt => _localization["Set_RecordPrompt"];

    /// <summary>Re-derives the hotkey status text (used to cancel an in-progress capture).</summary>
    public void RefreshHotkeyStatusPublic() => RefreshHotkeyStatus();

    private void RefreshHotkeyStatus() =>
        HotkeyStatus = string.Format(
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

        IsBusy = true;
        StatusMessage = _localization["St_Connecting"];

        // test-before-configure: probe the candidate settings first — on failure NOTHING
        // is persisted and the previously working connection stays untouched.
        var check = await _connection.CheckAsync(settings.ToConnectionSettings());
        if (!check.IsOk)
        {
            StatusMessage = FormatCheck(check);
            IsBusy = false;
            return;
        }

        _settingsStore.Save(settings);
        var result = await _shell.ConnectAsync(settings);
        StatusMessage = FormatCheck(result);
        IsBusy = false;
        if (result.IsOk)
            _ = LoadStartViewDashboardsAsync(); // the default-view picker can now list real dashboards
    }

    private string FormatCheck(ConnectionCheckResult result) =>
        result.Status == ConnectionCheckStatus.HttpError
            ? string.Format(_localization[result.I18nKey], result.HttpStatusCode)
            : _localization[result.I18nKey];
}
