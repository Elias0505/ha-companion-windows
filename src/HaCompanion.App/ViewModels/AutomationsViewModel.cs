// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
using HaCompanion.Core.Automations;

namespace HaCompanion.App.ViewModels;

/// <summary>One selectable trigger in the WENN picker.</summary>
public sealed record TriggerOption(WindowsTrigger Trigger, string Key, string Label, string Glyph, TriggerParamKind ParamKind);

/// <summary>A titled group of triggers (Sitzung/Anzeige/Aktivität/Programme/Medien).</summary>
public sealed class TriggerGroupViewModel
{
    public required string Title { get; init; }

    public ObservableCollection<TriggerOption> Items { get; } = new();
}

/// <summary>One selectable action for the current entity's domain.</summary>
public sealed record ActionOption(string Action, string Label);

/// <summary>One DANN card in the builder: entity plus the chosen action.</summary>
public sealed partial class ActionDraftViewModel : ObservableObject
{
    [ObservableProperty]
    private EntityTileViewModel? _tile;

    public ObservableCollection<ActionOption> Actions { get; } = new();

    [ObservableProperty]
    private ActionOption? _selectedAction;

    public bool HasTile => Tile is not null;

    public bool IsComplete => Tile is not null && SelectedAction is not null;

    partial void OnTileChanged(EntityTileViewModel? value) => OnPropertyChanged(nameof(HasTile));
}

/// <summary>One executed action inside a rule card ("💡 Name · Aus").</summary>
public sealed record RuleActionView(string Glyph, string Name, string ActionLabel);

/// <summary>One rule card in the list.</summary>
public sealed partial class AutomationItemViewModel : ObservableObject
{
    public required AutomationRule Rule { get; init; }

    public required string TriggerGlyph { get; init; }

    public required string TriggerText { get; init; }   // incl. param ("Programm wird aktiv: powerpnt")

    public string? ConditionText { get; init; }         // null = no condition chip

    public bool HasConditionChip => ConditionText is not null;

    public ObservableCollection<RuleActionView> Actions { get; } = new();

    /// <summary>Accent dot: the trigger's state is true right now (state-like triggers only).</summary>
    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private string _lastFiredText = "";

    // Two-way from the card's ToggleSwitch; the page routes changes to the view model.
    [ObservableProperty]
    private bool _isEnabled;
}

/// <summary>Backing view model for the Automationen tab (flow-card builder + rule list).</summary>
public sealed partial class AutomationsViewModel : ObservableObject
{
    private readonly IRulesStore _store;
    private readonly IRulesEngine _engine;
    private readonly IWindowsStateMonitor _monitor;
    private readonly LocalizationService _loc;
    private readonly MdiIconProvider _icons;
    private readonly IUiDispatcher _ui;

    public EntityCatalogViewModel Catalog { get; }

    public ObservableCollection<AutomationItemViewModel> Items { get; } = new();

    public ObservableCollection<TriggerGroupViewModel> TriggerGroups { get; } = new();

    public ObservableCollection<ActionDraftViewModel> ActionDrafts { get; } = new();

    // ----- builder: WENN -----

    [ObservableProperty]
    private TriggerOption? _selectedTrigger;

    [ObservableProperty]
    private double _minutesParam = 10;

    [ObservableProperty]
    private string _processParam = "";

    public bool ShowMinutes => SelectedTrigger?.ParamKind == TriggerParamKind.Minutes;

    public bool ShowProcess => SelectedTrigger?.ParamKind == TriggerParamKind.ProcessName;

    // ----- builder: NUR WENN (optional condition) -----

    [ObservableProperty]
    private bool _hasCondition;

    [ObservableProperty]
    private bool _conditionIsTime;

    [ObservableProperty]
    private EntityTileViewModel? _conditionTile;

    [ObservableProperty]
    private bool _conditionWantedOn = true;

    [ObservableProperty]
    private TimeSpan _conditionFrom = new(22, 0, 0);

    [ObservableProperty]
    private TimeSpan _conditionTo = new(6, 0, 0);

    [ObservableProperty]
    private bool _hasItems;

    public bool CanAdd => BuildRule() is { } rule && rule.IsValid();

    /// <summary>Short text on the condition node ("Licht Flur ist an" / "22:00–06:00").</summary>
    public string ConditionSummary
    {
        get
        {
            if (!HasCondition)
                return "";
            if (ConditionIsTime)
                return $"{ConditionFrom.Hours:00}:{ConditionFrom.Minutes:00}–{ConditionTo.Hours:00}:{ConditionTo.Minutes:00}";
            var name = ConditionTile?.FriendlyName ?? _loc["Au_CondEntity"];
            return $"{name} {_loc[ConditionWantedOn ? "Au_CondOn" : "Au_CondOff"]}";
        }
    }

    public AutomationsViewModel(IRulesStore store, IRulesEngine engine, IWindowsStateMonitor monitor,
        EntityCatalogViewModel catalog, LocalizationService loc, MdiIconProvider icons, IUiDispatcher ui)
    {
        _store = store;
        _engine = engine;
        _monitor = monitor;
        Catalog = catalog;
        _loc = loc;
        _icons = icons;
        _ui = ui;

        BuildTriggerGroups();
        ResetBuilder();
        Rebuild();

        _loc.LanguageChanged += (_, _) => _ui.Post(() => { BuildTriggerGroups(); Rebuild(); });
        _engine.RuleFired += (_, _) => _ui.Post(RefreshFooters);
        _monitor.TriggerFired += (_, _) => RefreshLiveDots();      // already on the UI thread
        _monitor.IdleMinutesChanged += (_, _) => RefreshLiveDots();
    }

    public IReadOnlyList<EntityTileViewModel> Search(string query) =>
        Catalog.SearchTiles(query, actionableOnly: true);

    /// <summary>Distinct names of currently running processes (suggestions for app triggers).</summary>
    public static IReadOnlyList<string> RunningProcessNames(string filter)
    {
        try
        {
            var all = System.Diagnostics.Process.GetProcesses()
                .Select(p => { var n = p.ProcessName; p.Dispose(); return n.ToLowerInvariant(); })
                .Distinct()
                .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .Take(20)
                .ToList();
            return all;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ----- builder actions -----

    /// <summary>Assign an entity to the given draft (from search or the category quick-pick).</summary>
    public void AssignEntity(ActionDraftViewModel draft, EntityTileViewModel tile)
    {
        draft.Tile = tile;
        draft.Actions.Clear();
        foreach (var action in AutomationActions.AllowedFor(tile.EntityId.Split('.')[0]))
            draft.Actions.Add(new ActionOption(action, _loc["Act_" + action]));
        draft.SelectedAction = draft.Actions.FirstOrDefault();
        NotifyBuilderChanged();
    }

    /// <summary>Quick-pick target: the first draft still missing an entity (or a fresh one).</summary>
    public void AssignEntityToNextFreeDraft(EntityTileViewModel tile)
    {
        var free = ActionDrafts.FirstOrDefault(d => d.Tile is null);
        if (free is null)
        {
            free = new ActionDraftViewModel();
            ActionDrafts.Add(free);
        }
        AssignEntity(free, tile);
    }

    [RelayCommand]
    private void AddActionDraft()
    {
        ActionDrafts.Add(new ActionDraftViewModel());
        NotifyBuilderChanged();
    }

    [RelayCommand]
    private void RemoveActionDraft(ActionDraftViewModel draft)
    {
        ActionDrafts.Remove(draft);
        if (ActionDrafts.Count == 0)
            ActionDrafts.Add(new ActionDraftViewModel());
        NotifyBuilderChanged();
    }

    [RelayCommand]
    private void RemoveCondition()
    {
        HasCondition = false;
        ConditionTile = null;
        NotifyBuilderChanged();
    }

    [RelayCommand]
    private void Add()
    {
        if (BuildRule() is not { } rule || !rule.IsValid())
            return;
        _store.Save(_store.Load().Append(rule).ToList());
        _engine.Reload();
        ResetBuilder();
        Rebuild();
    }

    [RelayCommand]
    private void Remove(AutomationItemViewModel item)
    {
        _store.Save(_store.Load().Where(r => r != item.Rule).ToList());
        _engine.Reload();
        Rebuild();
    }

    public void SetEnabled(AutomationItemViewModel item, bool enabled)
    {
        if (item.Rule.IsEnabled == enabled)
            return;
        var rules = _store.Load()
            .Select(r => r == item.Rule ? r with { IsEnabled = enabled } : r)
            .ToList();
        _store.Save(rules);
        _engine.Reload();
        Rebuild();
    }

    /// <summary>The draft rule as currently configured, or null while nothing is chosen.</summary>
    private AutomationRule? BuildRule()
    {
        if (SelectedTrigger is null)
            return null;
        var param = SelectedTrigger.ParamKind switch
        {
            TriggerParamKind.Minutes => ((int)MinutesParam).ToString(),
            TriggerParamKind.ProcessName => RuleMatcher.NormalizeProcessName(ProcessParam),
            _ => null,
        };
        var actions = ActionDrafts
            .Where(d => d.IsComplete)
            .Select(d => new RuleAction(d.Tile!.EntityId, d.SelectedAction!.Action))
            .ToList();
        RuleCondition? condition = null;
        if (HasCondition)
        {
            condition = ConditionIsTime
                ? new RuleCondition(RuleCondition.TypeTime,
                    FromTime: $"{ConditionFrom.Hours:00}:{ConditionFrom.Minutes:00}",
                    ToTime: $"{ConditionTo.Hours:00}:{ConditionTo.Minutes:00}")
                : new RuleCondition(RuleCondition.TypeEntity, ConditionTile?.EntityId,
                    ConditionWantedOn ? "on" : "off");
        }
        return new AutomationRule(SelectedTrigger.Key, param, actions, condition);
    }

    public void NotifyBuilderChanged()
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(ShowMinutes));
        OnPropertyChanged(nameof(ShowProcess));
        OnPropertyChanged(nameof(ConditionSummary));
    }

    partial void OnSelectedTriggerChanged(TriggerOption? value) => NotifyBuilderChanged();

    partial void OnMinutesParamChanged(double value) => NotifyBuilderChanged();

    partial void OnProcessParamChanged(string value) => NotifyBuilderChanged();

    partial void OnHasConditionChanged(bool value) => NotifyBuilderChanged();

    partial void OnConditionIsTimeChanged(bool value) => NotifyBuilderChanged();

    partial void OnConditionTileChanged(EntityTileViewModel? value) => NotifyBuilderChanged();

    partial void OnConditionWantedOnChanged(bool value) => NotifyBuilderChanged();

    partial void OnConditionFromChanged(TimeSpan value) => NotifyBuilderChanged();

    partial void OnConditionToChanged(TimeSpan value) => NotifyBuilderChanged();

    private void ResetBuilder()
    {
        SelectedTrigger = null;
        MinutesParam = 10;
        ProcessParam = "";
        HasCondition = false;
        ConditionIsTime = false;
        ConditionTile = null;
        ConditionWantedOn = true;
        ActionDrafts.Clear();
        ActionDrafts.Add(new ActionDraftViewModel());
        NotifyBuilderChanged();
    }

    // ----- list -----

    private void Rebuild()
    {
        Items.Clear();
        foreach (var rule in _store.Load())
        {
            var option = FindOption(rule.Trigger);
            var item = new AutomationItemViewModel
            {
                Rule = rule,
                TriggerGlyph = option?.Glyph ?? "\uE945",
                TriggerText = TriggerTextOf(rule, option),
                ConditionText = ConditionTextOf(rule.Condition),
                IsEnabled = rule.IsEnabled,
            };
            foreach (var action in rule.Actions)
            {
                var tile = Catalog.FindTile(action.EntityId);
                item.Actions.Add(new RuleActionView(
                    tile?.IconGlyph ?? _icons.DomainGlyph(action.EntityId.Split('.')[0]),
                    tile?.FriendlyName ?? action.EntityId,
                    _loc["Act_" + action.Action]));
            }
            Items.Add(item);
        }
        HasItems = Items.Count > 0;
        RefreshFooters();
        RefreshLiveDots();
    }

    private TriggerOption? FindOption(string key) =>
        TriggerGroups.SelectMany(g => g.Items).FirstOrDefault(o => o.Key == key);

    private string TriggerTextOf(AutomationRule rule, TriggerOption? option)
    {
        var label = option?.Label ?? rule.Trigger;
        return WindowsTriggers.TryParse(rule.Trigger, out var t)
            ? WindowsTriggers.ParamKind(t) switch
            {
                TriggerParamKind.Minutes => $"{label} ({rule.Param} {_loc["Au_MinutesSuffix"]})",
                TriggerParamKind.ProcessName => $"{label}: {rule.Param}",
                _ => label,
            }
            : label;
    }

    private string? ConditionTextOf(RuleCondition? condition)
    {
        if (condition is null)
            return null;
        if (condition.Type == RuleCondition.TypeTime)
            return $"{condition.FromTime}–{condition.ToTime}";
        var tile = condition.EntityId is null ? null : Catalog.FindTile(condition.EntityId);
        var name = tile?.FriendlyName ?? condition.EntityId;
        return $"{name} {_loc[condition.WantedState == "on" ? "Au_CondOn" : "Au_CondOff"]}";
    }

    private void RefreshFooters()
    {
        foreach (var item in Items)
        {
            var at = _engine.LastFiredAt(item.Rule);
            item.LastFiredText = at is null
                ? ""
                : string.Format(_loc["Au_LastFired"], at.Value.ToLocalTime().ToString("HH:mm"));
        }
    }

    private void RefreshLiveDots()
    {
        var s = _monitor.Current;
        foreach (var item in Items)
        {
            if (!WindowsTriggers.TryParse(item.Rule.Trigger, out var t) || !WindowsTriggers.IsStateLike(t))
            {
                item.IsLive = false;
                continue;
            }
            item.IsLive = t switch
            {
                WindowsTrigger.Lock => s.IsLocked,
                WindowsTrigger.Unlock => !s.IsLocked,
                WindowsTrigger.DisplayOn => s.DisplayOn,
                WindowsTrigger.DisplayOff => !s.DisplayOn,
                WindowsTrigger.IdleStart => item.Rule.IdleMinutes is { } m && s.IdleMinutes >= m,
                WindowsTrigger.IdleEnd => item.Rule.IdleMinutes is { } m2 && s.IdleMinutes < m2,
                WindowsTrigger.FullscreenStart => s.IsFullscreen,
                WindowsTrigger.FullscreenEnd => !s.IsFullscreen,
                WindowsTrigger.AppStart => s.ForegroundProcess is not null
                    && s.ForegroundProcess == RuleMatcher.NormalizeProcessName(item.Rule.Param ?? ""),
                WindowsTrigger.AppStop => s.ForegroundProcess != RuleMatcher.NormalizeProcessName(item.Rule.Param ?? ""),
                WindowsTrigger.MicOn => s.MicInUse,
                WindowsTrigger.MicOff => !s.MicInUse,
                WindowsTrigger.CamOn => s.CamInUse,
                WindowsTrigger.CamOff => !s.CamInUse,
                WindowsTrigger.AudioStart => s.AudioPlaying == true,
                WindowsTrigger.AudioStop => s.AudioPlaying == false,
                _ => false,
            };
        }
    }

    private void BuildTriggerGroups()
    {
        TriggerGroups.Clear();
        AddGroup("TrigGrp_session", new[]
        {
            WindowsTrigger.Startup, WindowsTrigger.Lock, WindowsTrigger.Unlock, WindowsTrigger.Logon,
            WindowsTrigger.Logoff, WindowsTrigger.Suspend, WindowsTrigger.Resume, WindowsTrigger.Shutdown,
        });
        AddGroup("TrigGrp_display", new[] { WindowsTrigger.DisplayOn, WindowsTrigger.DisplayOff });
        AddGroup("TrigGrp_activity", new[] { WindowsTrigger.IdleStart, WindowsTrigger.IdleEnd });
        AddGroup("TrigGrp_apps", new[]
        {
            WindowsTrigger.AppStart, WindowsTrigger.AppStop,
            WindowsTrigger.FullscreenStart, WindowsTrigger.FullscreenEnd,
        });
        AddGroup("TrigGrp_media", new[]
        {
            WindowsTrigger.MicOn, WindowsTrigger.MicOff, WindowsTrigger.CamOn,
            WindowsTrigger.CamOff, WindowsTrigger.AudioStart, WindowsTrigger.AudioStop,
        });
    }

    private void AddGroup(string titleKey, IEnumerable<WindowsTrigger> triggers)
    {
        var group = new TriggerGroupViewModel { Title = _loc[titleKey] };
        foreach (var trigger in triggers)
        {
            var key = WindowsTriggers.ToKey(trigger);
            group.Items.Add(new TriggerOption(trigger, key, _loc["Trig_" + key],
                GlyphOf(trigger), WindowsTriggers.ParamKind(trigger)));
        }
        TriggerGroups.Add(group);
    }

    private static string GlyphOf(WindowsTrigger trigger) => trigger switch
    {
        WindowsTrigger.Startup => "\uE768",          // Play
        WindowsTrigger.Lock => "\uE72E",             // Lock
        WindowsTrigger.Unlock => "\uE785",           // Unlock
        WindowsTrigger.Logon or WindowsTrigger.Logoff => "\uE748", // SwitchUser
        WindowsTrigger.Suspend => "\uE708",          // QuietHours (moon)
        WindowsTrigger.Resume => "\uE823",           // Recent (clock)
        WindowsTrigger.Shutdown => "\uE7E8",         // PowerButton
        WindowsTrigger.DisplayOn or WindowsTrigger.DisplayOff => "\uE7F4", // TVMonitor
        WindowsTrigger.IdleStart or WindowsTrigger.IdleEnd => "\uE916",    // Stopwatch
        WindowsTrigger.FullscreenStart => "\uE740",  // FullScreen
        WindowsTrigger.FullscreenEnd => "\uE73F",    // BackToWindow
        WindowsTrigger.AppStart or WindowsTrigger.AppStop => "\uE71D",     // AllApps
        WindowsTrigger.MicOn or WindowsTrigger.MicOff => "\uE720",         // Microphone
        WindowsTrigger.CamOn or WindowsTrigger.CamOff => "\uE714",         // Video
        WindowsTrigger.AudioStart => "\uE767",       // Volume
        WindowsTrigger.AudioStop => "\uE74F",        // Mute
        _ => "\uE945",
    };
}
