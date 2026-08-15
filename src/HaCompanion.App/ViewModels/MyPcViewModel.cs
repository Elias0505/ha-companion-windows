// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
using HaCompanion.Core.MobileApp;
using HaCompanion.Core.Notifications;

namespace HaCompanion.App.ViewModels;

/// <summary>One entry of the notify-mode picker ("… eingeschaltet wird" / "… sich ändert").</summary>
public sealed record NotifyModeOption(string Mode, string Label);

/// <summary>One local notification rule in the list.</summary>
public sealed partial class NotifyRuleItemViewModel : ObservableObject
{
    public required NotificationRule Rule { get; init; }

    public required string FriendlyName { get; init; }

    public required string IconGlyph { get; init; }  // MDI font

    public required string ModeText { get; init; }

    [ObservableProperty]
    private bool _isEnabled;
}

/// <summary>
/// Backing view model for the "Mein PC" tab: live status card, local notification
/// rules, HA→PC command permissions and the received-notifications history.
/// </summary>
public sealed partial class MyPcViewModel : ObservableObject
{
    private readonly IWindowsStateMonitor _monitor;
    private readonly INotifyRulesStore _rulesStore;
    private readonly INotifyRulesEngine _rulesEngine;
    private readonly IPushNotificationReceiver _receiver;
    private readonly ISettingsStore _settings;
    private readonly LocalizationService _loc;
    private readonly MdiIconProvider _icons;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    public EntityCatalogViewModel Catalog { get; }

    /// <summary>Category filter for the device browse (pick a notify-rule entity by tapping).</summary>
    public DeviceBrowserViewModel Browser { get; }

    // ----- status card (live) -----

    [ObservableProperty]
    private string _statusLine1 = "";

    [ObservableProperty]
    private string _statusLine2 = "";

    // ----- notify rules -----

    public ObservableCollection<NotifyRuleItemViewModel> Rules { get; } = new();

    public ObservableCollection<NotifyModeOption> Modes { get; } = new();

    [ObservableProperty]
    private EntityTileViewModel? _selectedTile;

    [ObservableProperty]
    private NotifyModeOption? _selectedMode;

    [ObservableProperty]
    private bool _hasRules;

    public bool CanAddRule => SelectedTile is not null && SelectedMode is not null;

    // ----- command permissions (two-way, saved on change) -----

    [ObservableProperty] private bool _allowLock;
    [ObservableProperty] private bool _allowMonitorOff;
    [ObservableProperty] private bool _allowVolume;
    [ObservableProperty] private bool _allowSleep;
    [ObservableProperty] private bool _allowShutdown;
    [ObservableProperty] private bool _allowLaunch;
    [ObservableProperty] private bool _allowCloseApp;

    [ObservableProperty]
    private string _launchWhitelistText = "";

    [ObservableProperty]
    private string _closeWhitelistText = "";

    /// <summary>Names the whitelist entries that were NOT saved (empty = all valid).</summary>
    [ObservableProperty]
    private string _whitelistError = "";

    /// <summary>Names the close-list entries that were NOT saved (empty = all valid).</summary>
    [ObservableProperty]
    private string _closeWhitelistError = "";

    /// <summary>
    /// notify.mobile_app_&lt;slug&gt; — shown in the mini docs so users can copy it. Derived from
    /// the name that was SENT AT REGISTRATION, not the current display name: HA fixes the
    /// service slug at that moment, and showing the renamed value would point users at a
    /// service that does not exist. Refreshed via <see cref="ReloadPermissions"/>.
    /// </summary>
    public string NotifyServiceName
    {
        get
        {
            var registered = _settings.Load().MobileAppRegisteredName;
            return "notify.mobile_app_" + Slugify(
                string.IsNullOrEmpty(registered) ? Environment.MachineName : registered);
        }
    }

    public bool ShowHttpWarning =>
        _settings.Load().BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Notifications/commands ride the mobile_app device — hint when it doesn't exist yet.</summary>
    public bool ShowDeviceHint => string.IsNullOrEmpty(_settings.Load().MobileAppWebhookId);

    // ----- received history -----

    public ObservableCollection<ReceivedItem> History => _receiver.History;

    public MyPcViewModel(IWindowsStateMonitor monitor, INotifyRulesStore rulesStore, INotifyRulesEngine rulesEngine,
        IPushNotificationReceiver receiver, ISettingsStore settings, EntityCatalogViewModel catalog,
        LocalizationService loc, MdiIconProvider icons, IUiDispatcher ui, DeviceBrowserViewModel browser)
    {
        _monitor = monitor;
        _rulesStore = rulesStore;
        _rulesEngine = rulesEngine;
        _receiver = receiver;
        _settings = settings;
        Catalog = catalog;
        Browser = browser;
        _loc = loc;
        _icons = icons;
        _ui = ui;

        LoadPermissions();
        BuildModes();
        RebuildRules();
        RefreshStatus();

        _monitor.TriggerFired += (_, _) => RefreshStatus();      // already on the UI thread
        _monitor.IdleMinutesChanged += (_, _) => RefreshStatus();
        _loc.LanguageChanged += (_, _) => _ui.Post(() =>
        {
            BuildModes();
            RebuildRules();
            RefreshStatus();
        });
    }

    public IReadOnlyList<EntityTileViewModel> Search(string query) =>
        Catalog.SearchTiles(query, actionableOnly: false); // doors/sensors are prime candidates

    // ----- notify rules -----

    [RelayCommand]
    private void AddRule()
    {
        if (SelectedTile is null || SelectedMode is null)
            return;
        var rule = new NotificationRule(SelectedTile.EntityId, SelectedMode.Mode);
        if (!rule.IsValid())
            return;
        // one rule per entity+mode: re-adding replaces instead of duplicating
        var rules = _rulesStore.Load()
            .Where(r => !(r.EntityId == rule.EntityId && r.Mode == rule.Mode))
            .Append(rule)
            .ToList();
        _rulesStore.Save(rules);
        _rulesEngine.Reload();
        SelectedTile = null;
        RebuildRules();
    }

    [RelayCommand]
    private void RemoveRule(NotifyRuleItemViewModel item)
    {
        _rulesStore.Save(_rulesStore.Load().Where(r => r != item.Rule).ToList());
        _rulesEngine.Reload();
        RebuildRules();
    }

    public void SetRuleEnabled(NotifyRuleItemViewModel item, bool enabled)
    {
        if (item.Rule.IsEnabled == enabled)
            return;
        _rulesStore.Save(_rulesStore.Load()
            .Select(r => r == item.Rule ? r with { IsEnabled = enabled } : r)
            .ToList());
        _rulesEngine.Reload();
        RebuildRules();
    }

    /// <summary>Quick assignment from the entity search box.</summary>
    public void AssignEntity(EntityTileViewModel tile)
    {
        SelectedTile = tile;
        OnPropertyChanged(nameof(CanAddRule));
    }

    partial void OnSelectedTileChanged(EntityTileViewModel? value) => OnPropertyChanged(nameof(CanAddRule));

    partial void OnSelectedModeChanged(NotifyModeOption? value) => OnPropertyChanged(nameof(CanAddRule));

    private void BuildModes()
    {
        var selected = SelectedMode?.Mode ?? NotificationRule.TurnedOn;
        Modes.Clear();
        foreach (var mode in NotificationRule.Modes)
            Modes.Add(new NotifyModeOption(mode, _loc["Nr_" + mode]));
        SelectedMode = Modes.FirstOrDefault(m => m.Mode == selected) ?? Modes[0];
    }

    private void RebuildRules()
    {
        Rules.Clear();
        foreach (var rule in _rulesStore.Load())
        {
            var tile = Catalog.FindTile(rule.EntityId);
            Rules.Add(new NotifyRuleItemViewModel
            {
                Rule = rule,
                FriendlyName = tile?.FriendlyName ?? rule.EntityId,
                IconGlyph = tile?.IconGlyph ?? _icons.DomainGlyph(rule.EntityId.Split('.')[0]),
                ModeText = _loc["Nr_" + rule.Mode],
                IsEnabled = rule.IsEnabled,
            });
        }
        HasRules = Rules.Count > 0;
    }

    // ----- status card -----

    private void RefreshStatus()
    {
        var s = _monitor.Current;
        string YesNo(bool b) => _loc[b ? "Pc_Yes" : "Pc_No"];
        StatusLine1 = $"{_loc[s.IsLocked ? "Pc_Locked" : "Pc_Unlocked"]}"
                      + $" · {_loc["Pc_Program"]}: {(s.IsOwnAppForeground ? "hacompanion" : string.IsNullOrEmpty(s.ForegroundProcess) ? "—" : s.ForegroundProcess)}"
                      + $" · {_loc["Pc_Fullscreen"]}: {YesNo(s.IsFullscreen)}";
        StatusLine2 = $"{_loc["Pc_Mic"]}: {YesNo(s.MicInUse)}"
                      + $" · {_loc["Pc_Cam"]}: {YesNo(s.CamInUse)}"
                      + $" · {_loc["Pc_Audio"]}: {(s.AudioPlaying is null ? "—" : YesNo(s.AudioPlaying.Value))}"
                      + $" · {_loc["Pc_Display"]}: {YesNo(s.DisplayOn)}"
                      + $" · {_loc["Pc_Idle"]}: {s.IdleMinutes} {_loc["Pc_MinShort"]}";
    }

    // ----- command permissions -----

    /// <summary>Re-read the command permissions from the store (e.g. after a config import
    /// rewrote them) so the toggles never show a stale state.</summary>
    public void ReloadPermissions()
    {
        LoadPermissions();
        OnPropertyChanged(nameof(NotifyServiceName)); // may change after (re-)registration/import
    }

    private void LoadPermissions()
    {
        _loading = true;
        var s = _settings.Load();
        AllowLock = s.AllowCmdLock;
        AllowMonitorOff = s.AllowCmdMonitorOff;
        AllowVolume = s.AllowCmdVolume;
        AllowSleep = s.AllowCmdSleep;
        AllowShutdown = s.AllowCmdShutdown;
        AllowLaunch = s.AllowCmdLaunch;
        AllowCloseApp = s.AllowCmdCloseApp;
        LaunchWhitelistText = string.Join("; ", s.LaunchWhitelist);
        CloseWhitelistText = string.Join("; ", s.CloseAppWhitelist);
        _loading = false;
    }

    private void SavePermissions()
    {
        if (_loading)
            return;
        // Validate OUTSIDE the store's lock (TryValidateEntry probes the filesystem; a dead
        // UNC path can stall for seconds), then persist only the fields this page owns.
        // Writing a whole Load()ed snapshot here could revert a webhook id the sensor
        // heartbeat stored in the meantime — and other snapshot writers could revive an
        // AllowCmd* toggle the user just switched off.
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var entry in LaunchWhitelistText
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Entries may carry ARGUMENTS after the path (#17); the canonical stored form
            // quotes the path when arguments are present, so later parses are deterministic.
            if (LaunchWhitelist.TryParseEntry(entry, out var fullPath, out var args))
                valid.Add(LaunchWhitelist.CanonicalEntry(fullPath, args));
            else
                invalid.Add(entry);
        }
        WhitelistError = invalid.Count == 0
            ? ""
            : string.Format(CultureInfo.CurrentCulture, _loc["Cmd_WhitelistInvalid"], string.Join("; ", invalid));

        var validClose = new List<string>();
        var invalidClose = new List<string>();
        foreach (var entry in CloseWhitelistText
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CloseAppWhitelist.TryValidateName(entry, out var normalized))
            {
                if (!validClose.Contains(normalized))
                    validClose.Add(normalized);
            }
            else
            {
                invalidClose.Add(entry);
            }
        }
        CloseWhitelistError = invalidClose.Count == 0
            ? ""
            : string.Format(CultureInfo.CurrentCulture, _loc["Cmd_CloseWhitelistInvalid"], string.Join("; ", invalidClose));

        _settings.Update(s =>
        {
            s.AllowCmdLock = AllowLock;
            s.AllowCmdMonitorOff = AllowMonitorOff;
            s.AllowCmdVolume = AllowVolume;
            s.AllowCmdSleep = AllowSleep;
            s.AllowCmdShutdown = AllowShutdown;
            s.AllowCmdLaunch = AllowLaunch;
            s.AllowCmdCloseApp = AllowCloseApp;
            s.LaunchWhitelist = valid;
            s.CloseAppWhitelist = validClose;
        });
    }

    partial void OnAllowLockChanged(bool value) => SavePermissions();

    partial void OnAllowMonitorOffChanged(bool value) => SavePermissions();

    partial void OnAllowVolumeChanged(bool value) => SavePermissions();

    partial void OnAllowSleepChanged(bool value) => SavePermissions();

    partial void OnAllowShutdownChanged(bool value) => SavePermissions();

    partial void OnAllowLaunchChanged(bool value) => SavePermissions();

    partial void OnLaunchWhitelistTextChanged(string value) => SavePermissions();

    partial void OnAllowCloseAppChanged(bool value) => SavePermissions();

    partial void OnCloseWhitelistTextChanged(string value) => SavePermissions();

    private static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }
}
