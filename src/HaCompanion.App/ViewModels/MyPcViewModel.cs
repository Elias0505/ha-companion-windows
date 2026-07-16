// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
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

    [ObservableProperty]
    private string _launchWhitelistText = "";

    /// <summary>notify.mobile_app_&lt;slug&gt; — shown in the mini docs so users can copy it.</summary>
#pragma warning disable CA1822
    public string NotifyServiceName => "notify.mobile_app_" + Slugify(Environment.MachineName);
#pragma warning restore CA1822

    public bool ShowHttpWarning =>
        _settings.Load().BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Notifications/commands ride the mobile_app device — hint when it doesn't exist yet.</summary>
    public bool ShowDeviceHint => string.IsNullOrEmpty(_settings.Load().MobileAppWebhookId);

    // ----- received history -----

    public ObservableCollection<ReceivedItem> History => _receiver.History;

    public MyPcViewModel(IWindowsStateMonitor monitor, INotifyRulesStore rulesStore, INotifyRulesEngine rulesEngine,
        IPushNotificationReceiver receiver, ISettingsStore settings, EntityCatalogViewModel catalog,
        LocalizationService loc, MdiIconProvider icons, IUiDispatcher ui)
    {
        _monitor = monitor;
        _rulesStore = rulesStore;
        _rulesEngine = rulesEngine;
        _receiver = receiver;
        _settings = settings;
        Catalog = catalog;
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
                      + $" · {_loc["Pc_Program"]}: {(string.IsNullOrEmpty(s.ForegroundProcess) ? "—" : s.ForegroundProcess)}"
                      + $" · {_loc["Pc_Fullscreen"]}: {YesNo(s.IsFullscreen)}";
        StatusLine2 = $"{_loc["Pc_Mic"]}: {YesNo(s.MicInUse)}"
                      + $" · {_loc["Pc_Cam"]}: {YesNo(s.CamInUse)}"
                      + $" · {_loc["Pc_Audio"]}: {(s.AudioPlaying is null ? "—" : YesNo(s.AudioPlaying.Value))}"
                      + $" · {_loc["Pc_Display"]}: {YesNo(s.DisplayOn)}"
                      + $" · {_loc["Pc_Idle"]}: {s.IdleMinutes} {_loc["Pc_MinShort"]}";
    }

    // ----- command permissions -----

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
        LaunchWhitelistText = string.Join("; ", s.LaunchWhitelist);
        _loading = false;
    }

    private void SavePermissions()
    {
        if (_loading)
            return;
        var s = _settings.Load();
        s.AllowCmdLock = AllowLock;
        s.AllowCmdMonitorOff = AllowMonitorOff;
        s.AllowCmdVolume = AllowVolume;
        s.AllowCmdSleep = AllowSleep;
        s.AllowCmdShutdown = AllowShutdown;
        s.AllowCmdLaunch = AllowLaunch;
        s.LaunchWhitelist = LaunchWhitelistText
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.Save(s);
    }

    partial void OnAllowLockChanged(bool value) => SavePermissions();

    partial void OnAllowMonitorOffChanged(bool value) => SavePermissions();

    partial void OnAllowVolumeChanged(bool value) => SavePermissions();

    partial void OnAllowSleepChanged(bool value) => SavePermissions();

    partial void OnAllowShutdownChanged(bool value) => SavePermissions();

    partial void OnAllowLaunchChanged(bool value) => SavePermissions();

    partial void OnLaunchWhitelistTextChanged(string value) => SavePermissions();

    private static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }
}
