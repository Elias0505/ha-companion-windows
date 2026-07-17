// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>One DANN card in the builder: entity plus the chosen action + optional data.</summary>
public sealed partial class ActionDraftViewModel : ObservableObject
{
    /// <summary>Localization service (set by the builder) — for the data-field label.</summary>
    public LocalizationService? Loc { get; init; }

    [ObservableProperty]
    private EntityTileViewModel? _tile;

    public ObservableCollection<ActionOption> Actions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsData), nameof(DataLabelText), nameof(IsPercent))]
    private ActionOption? _selectedAction;

    /// <summary>Optional service-data value (brightness %, volume %, temperature, position %).</summary>
    [ObservableProperty]
    private double _dataValue = 50;

    public bool HasTile => Tile is not null;

    public bool IsComplete => Tile is not null && SelectedAction is not null;

    private string Domain => Tile?.EntityId.Split('.')[0] ?? "";

    /// <summary>The set-verb / light turn_on that carries a data field, else null.</summary>
    public bool SupportsData => DataKind is not null;

    /// <summary>Whether the data value is a 0–100 percentage (vs. a raw number like °C).</summary>
    public bool IsPercent => DataKind is "brightness" or "volume" or "position" or "percentage";

    /// <summary>Kind of data control to show for the current action (null = none).</summary>
    public string? DataKind => (Domain, SelectedAction?.Action) switch
    {
        ("light", AutomationActions.TurnOn) => "brightness",
        ("media_player", AutomationActions.SetVolume) => "volume",
        ("climate", AutomationActions.SetTemperature) => "temperature",
        ("cover", AutomationActions.SetPosition) => "position",
        ("fan", AutomationActions.SetPercentage) => "percentage",
        _ => null,
    };

    private string DataLabelKey => DataKind switch
    {
        "brightness" => "Au_Brightness",
        "volume" => "Au_Volume",
        "temperature" => "Au_Temperature",
        "position" => "Au_Position",
        "percentage" => "Au_Percentage",
        _ => "",
    };

    public string DataLabelText => Loc is null || DataLabelKey.Length == 0 ? "" : Loc[DataLabelKey];

    /// <summary>Build the service-data dictionary HA expects for the current action, or null.</summary>
    public IReadOnlyDictionary<string, object?>? BuildData() => DataKind switch
    {
        "brightness" => new Dictionary<string, object?> { ["brightness_pct"] = (int)DataValue },
        "volume" => new Dictionary<string, object?> { ["volume_level"] = Math.Round(DataValue / 100.0, 2) },
        "temperature" => new Dictionary<string, object?> { ["temperature"] = DataValue },
        "position" => new Dictionary<string, object?> { ["position"] = (int)DataValue },
        "percentage" => new Dictionary<string, object?> { ["percentage"] = (int)DataValue },
        _ => null,
    };

    partial void OnTileChanged(EntityTileViewModel? value)
    {
        OnPropertyChanged(nameof(HasTile));
        OnPropertyChanged(nameof(SupportsData));
        OnPropertyChanged(nameof(DataLabelText));
        OnPropertyChanged(nameof(IsPercent));
    }
}

/// <summary>A pc-state field with its localized label (for the pc-condition picker).</summary>
public sealed record PcFieldOption(string Value, string Label);

/// <summary>One NUR-WENN condition row in the builder (entity / time / numeric / pc).</summary>
public sealed partial class ConditionRowViewModel : ObservableObject
{
    public required string Type { get; init; } // entity | time | numeric | pc

    public bool IsEntity => Type == RuleCondition.TypeEntity;
    public bool IsTime => Type == RuleCondition.TypeTime;
    public bool IsNumeric => Type == RuleCondition.TypeNumeric;
    public bool IsPc => Type == RuleCondition.TypePc;
    public bool NeedsEntity => IsEntity || IsNumeric;

    [ObservableProperty]
    private EntityTileViewModel? _entityTile;

    [ObservableProperty]
    private bool _wantedOn = true;      // entity + pc

    [ObservableProperty]
    private TimeSpan _from = new(18, 0, 0);

    [ObservableProperty]
    private TimeSpan _to = new(23, 0, 0);

    [ObservableProperty]
    private string _operator = "<";     // numeric

    [ObservableProperty]
    private double _number;             // numeric

    [ObservableProperty]
    private string _pcField = "locked"; // pc

#pragma warning disable CA1822 // x:Bind target — must be an instance member for the binding
    public IReadOnlyList<string> Operators => RuleCondition.Operators;
#pragma warning restore CA1822

    /// <summary>Localized (value, label) pairs for the pc-field picker (set by the parent VM).</summary>
    public IReadOnlyList<PcFieldOption> PcFieldOptions { get; set; } = Array.Empty<PcFieldOption>();

    partial void OnEntityTileChanged(EntityTileViewModel? value)
    {
        OnPropertyChanged(nameof(EntityName));
        OnPropertyChanged(nameof(HasEntitySelected));
    }

    public string EntityName => EntityTile?.FriendlyName ?? "";

    public bool HasEntitySelected => EntityTile is not null;

    public RuleCondition? Build() => Type switch
    {
        RuleCondition.TypeEntity when EntityTile is not null =>
            new RuleCondition(Type, EntityTile.EntityId, WantedOn ? "on" : "off"),
        RuleCondition.TypeTime =>
            new RuleCondition(Type, FromTime: $"{From.Hours:00}:{From.Minutes:00}", ToTime: $"{To.Hours:00}:{To.Minutes:00}"),
        RuleCondition.TypeNumeric when EntityTile is not null =>
            new RuleCondition(Type, EntityTile.EntityId, Operator: Operator, Number: Number),
        RuleCondition.TypePc =>
            new RuleCondition(Type, PcField: PcField, WantedState: WantedOn ? "on" : "off"),
        _ => null,
    };
}

/// <summary>One executed action inside a rule card ("💡 Name · Aus").</summary>
public sealed record RuleActionView(string Glyph, string Name, string ActionLabel);

/// <summary>One rule card in the list.</summary>
public sealed partial class AutomationItemViewModel : ObservableObject
{
    public required AutomationRule Rule { get; init; }

    /// <summary>Card headline — the user's name, or an auto-generated "trigger → action".</summary>
    public string Title { get; init; } = "";

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

    // ----- builder: NUR WENN (zero or more AND conditions) -----

    public ObservableCollection<ConditionRowViewModel> Conditions { get; } = new();

    [ObservableProperty]
    private bool _hasItems;

    // ----- editor mode (manager list vs. the full-width builder form) -----

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditTitle))]
    private bool _isEditingExisting;

    [ObservableProperty]
    private string _ruleName = "";

    private string? _editingId;

    /// <summary>Header of the builder form ("Neue Automation" / "Automation bearbeiten").</summary>
    public string EditTitle => _loc[IsEditingExisting ? "Au_EditTitle" : "Au_NewTitle"];

    public bool CanAdd => BuildRule() is { } rule && rule.IsValid();

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
            free = NewDraft();
            ActionDrafts.Add(free);
        }
        AssignEntity(free, tile);
    }

    private ActionDraftViewModel NewDraft() => new() { Loc = _loc };

    [RelayCommand]
    private void AddActionDraft()
    {
        ActionDrafts.Add(NewDraft());
        NotifyBuilderChanged();
    }

    [RelayCommand]
    private void RemoveActionDraft(ActionDraftViewModel draft)
    {
        ActionDrafts.Remove(draft);
        if (ActionDrafts.Count == 0)
            ActionDrafts.Add(NewDraft());
        NotifyBuilderChanged();
    }

    /// <summary>Add a condition row of the given type (entity/time/numeric/pc).</summary>
    public void AddCondition(string type)
    {
        Conditions.Add(new ConditionRowViewModel { Type = type, PcFieldOptions = BuildPcOptions() });
        NotifyBuilderChanged();
    }

    private List<PcFieldOption> BuildPcOptions() =>
        RuleCondition.PcFields.Select(f => new PcFieldOption(f, _loc["Pc_" + f])).ToList();

    [RelayCommand]
    private void RemoveConditionRow(ConditionRowViewModel row)
    {
        Conditions.Remove(row);
        NotifyBuilderChanged();
    }

    /// <summary>Open the builder for a brand-new rule.</summary>
    [RelayCommand]
    private void BeginNew()
    {
        ResetBuilder();
        _editingId = null;
        RuleName = "";
        IsEditingExisting = false;
        IsEditing = true;
    }

    /// <summary>Open the builder pre-filled with an existing rule.</summary>
    [RelayCommand]
    private void BeginEdit(AutomationItemViewModel item)
    {
        SeedFrom(item.Rule);
        _editingId = item.Rule.Id;
        RuleName = item.Rule.Name ?? "";
        IsEditingExisting = true;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>Save the builder as a new rule or replace the one being edited (by id).</summary>
    [RelayCommand]
    private void Save()
    {
        if (BuildRule() is not { } rule || !rule.IsValid())
            return;
        var rules = _store.Load().ToList();
        var idx = _editingId is null ? -1 : rules.FindIndex(r => r.Id == _editingId);
        if (idx >= 0)
            rules[idx] = rule;
        else
            rules.Add(rule);
        _store.Save(rules);
        _engine.Reload();
        IsEditing = false;
        Rebuild();
    }

    [RelayCommand]
    private void Duplicate(AutomationItemViewModel item)
    {
        var copy = item.Rule with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = (item.Rule.Name is { Length: > 0 } n ? n : DefaultName(item.Rule)) + " " + _loc["Au_CopySuffix"],
        };
        _store.Save(_store.Load().Append(copy).ToList());
        _engine.Reload();
        Rebuild();
    }

    /// <summary>"Jetzt testen": run the rule's actions immediately to verify the effect.</summary>
    [RelayCommand]
    private void RunTest(AutomationItemViewModel item) => _engine.RunActionsNow(item.Rule);

    [RelayCommand]
    private void Remove(AutomationItemViewModel item)
    {
        _store.Save(_store.Load().Where(r => r.Id != item.Rule.Id).ToList());
        _engine.Reload();
        Rebuild();
    }

    public void SetEnabled(AutomationItemViewModel item, bool enabled)
    {
        if (item.Rule.IsEnabled == enabled)
            return;
        var rules = _store.Load()
            .Select(r => r.Id == item.Rule.Id ? r with { IsEnabled = enabled } : r)
            .ToList();
        _store.Save(rules);
        _engine.Reload();
        Rebuild();
    }

    /// <summary>Populate the builder controls from an existing rule (for editing).</summary>
    private void SeedFrom(AutomationRule rule)
    {
        ResetBuilder();
        SelectedTrigger = FindOption(rule.Trigger);
        if (WindowsTriggers.TryParse(rule.Trigger, out var t))
        {
            if (WindowsTriggers.ParamKind(t) == TriggerParamKind.Minutes && rule.IdleMinutes is { } m)
                MinutesParam = m;
            else if (WindowsTriggers.ParamKind(t) == TriggerParamKind.ProcessName)
                ProcessParam = rule.Param ?? "";
        }

        Conditions.Clear();
        foreach (var cond in rule.EffectiveConditions)
            Conditions.Add(RowFrom(cond));

        ActionDrafts.Clear();
        foreach (var action in rule.Actions)
        {
            var draft = NewDraft();
            var tile = Catalog.FindTile(action.EntityId);
            if (tile is not null)
            {
                AssignEntity(draft, tile);
                draft.SelectedAction = draft.Actions.FirstOrDefault(a => a.Action == action.Action) ?? draft.SelectedAction;
                SeedActionData(draft, action);
            }
            ActionDrafts.Add(draft);
        }
        if (ActionDrafts.Count == 0)
            ActionDrafts.Add(NewDraft());
        NotifyBuilderChanged();
    }

    private ConditionRowViewModel RowFrom(RuleCondition c)
    {
        var row = new ConditionRowViewModel { Type = c.Type, PcFieldOptions = BuildPcOptions() };
        switch (c.Type)
        {
            case RuleCondition.TypeEntity:
                row.EntityTile = c.EntityId is null ? null : Catalog.FindTile(c.EntityId);
                row.WantedOn = c.WantedState == "on";
                break;
            case RuleCondition.TypeNumeric:
                row.EntityTile = c.EntityId is null ? null : Catalog.FindTile(c.EntityId);
                row.Operator = c.Operator ?? "<";
                row.Number = c.Number ?? 0;
                break;
            case RuleCondition.TypeTime:
                if (RuleCondition.TryParseTime(c.FromTime, out var from)) row.From = from.ToTimeSpan();
                if (RuleCondition.TryParseTime(c.ToTime, out var to)) row.To = to.ToTimeSpan();
                break;
            case RuleCondition.TypePc:
                row.PcField = c.PcField ?? "locked";
                row.WantedOn = c.WantedState == "on";
                break;
        }
        return row;
    }

    private static void SeedActionData(ActionDraftViewModel draft, RuleAction action)
    {
        if (action.Data is null)
            return;
        if (action.Data.TryGetValue("brightness_pct", out var b) && b is not null)
            draft.DataValue = ToDouble(b);
        else if (action.Data.TryGetValue("volume_level", out var v) && v is not null)
            draft.DataValue = Math.Round(ToDouble(v) * 100.0);
        else if (action.Data.TryGetValue("temperature", out var tp) && tp is not null)
            draft.DataValue = ToDouble(tp);
        else if (action.Data.TryGetValue("position", out var p) && p is not null)
            draft.DataValue = ToDouble(p);
        else if (action.Data.TryGetValue("percentage", out var pc) && pc is not null)
            draft.DataValue = ToDouble(pc);
    }

    private static double ToDouble(object value) => value switch
    {
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => 0,
    };

    /// <summary>Auto-generated readable name ("PC gesperrt → Wohnzimmer") when the user left it blank.</summary>
    private string DefaultName(AutomationRule rule)
    {
        var trig = TriggerTextOf(rule, FindOption(rule.Trigger));
        var first = rule.Actions.Count > 0 ? Catalog.FindTile(rule.Actions[0].EntityId)?.FriendlyName ?? rule.Actions[0].EntityId : "";
        return rule.Actions.Count > 0 ? $"{trig} → {first}" : trig;
    }

    /// <summary>The draft rule as currently configured, or null while nothing is chosen.</summary>
    private AutomationRule? BuildRule()
    {
        if (SelectedTrigger is null)
            return null;
        var param = SelectedTrigger.ParamKind switch
        {
            TriggerParamKind.Minutes => ((int)MinutesParam).ToString(CultureInfo.InvariantCulture),
            TriggerParamKind.ProcessName => RuleMatcher.NormalizeProcessName(ProcessParam),
            _ => null,
        };
        var actions = ActionDrafts
            .Where(d => d.IsComplete)
            .Select(d => new RuleAction(d.Tile!.EntityId, d.SelectedAction!.Action, d.BuildData()))
            .ToList();
        // Every condition row must build to a valid condition, else the rule is incomplete.
        var built = Conditions.Select(r => r.Build()).ToList();
        if (built.Any(c => c is null || !c.IsValid()))
            return null;
        var conditions = built.Count > 0 ? built.Select(c => c!).ToList() : null;
        return new AutomationRule(SelectedTrigger.Key, param, actions,
            Conditions: conditions,
            Id: _editingId ?? Guid.NewGuid().ToString("N"),
            Name: string.IsNullOrWhiteSpace(RuleName) ? null : RuleName.Trim());
    }

    public void NotifyBuilderChanged()
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(ShowMinutes));
        OnPropertyChanged(nameof(ShowProcess));
    }

    partial void OnSelectedTriggerChanged(TriggerOption? value) => NotifyBuilderChanged();

    partial void OnMinutesParamChanged(double value) => NotifyBuilderChanged();

    partial void OnProcessParamChanged(string value) => NotifyBuilderChanged();

    private void ResetBuilder()
    {
        SelectedTrigger = null;
        MinutesParam = 10;
        ProcessParam = "";
        Conditions.Clear();
        ActionDrafts.Clear();
        ActionDrafts.Add(NewDraft());
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
                Title = rule.Name is { Length: > 0 } n ? n : DefaultName(rule),
                TriggerGlyph = option?.Glyph ?? "\uE945",
                TriggerText = TriggerTextOf(rule, option),
                ConditionText = ConditionChipTextOf(rule.EffectiveConditions),
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

    /// <summary>Compact summary of a rule's conditions for the card chip (null when none).</summary>
    private string? ConditionChipTextOf(IReadOnlyList<RuleCondition> conditions)
    {
        if (conditions.Count == 0)
            return null;
        return string.Join(" · ", conditions.Select(ConditionOne));
    }

    private string ConditionOne(RuleCondition c) => c.Type switch
    {
        RuleCondition.TypeTime => $"{c.FromTime}–{c.ToTime}",
        RuleCondition.TypeNumeric => $"{ShortEntity(c.EntityId)} {c.Operator} {c.Number?.ToString(CultureInfo.CurrentCulture)}",
        RuleCondition.TypePc => $"{_loc["Pc_" + c.PcField]} {_loc[c.WantedState == "on" ? "Au_CondOn" : "Au_CondOff"]}",
        _ => $"{ShortEntity(c.EntityId)} {_loc[c.WantedState == "on" ? "Au_CondOn" : "Au_CondOff"]}",
    };

    private string ShortEntity(string? entityId) =>
        (entityId is null ? null : Catalog.FindTile(entityId)?.FriendlyName) ?? entityId ?? "";

    private void RefreshFooters()
    {
        foreach (var item in Items)
        {
            var at = _engine.LastFiredAt(item.Rule);
            if (at is null)
            {
                item.LastFiredText = _loc["Au_NeverFired"];
                continue;
            }
            var when = string.Format(CultureInfo.CurrentCulture, _loc["Au_LastFired"],
                at.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
            var count = _engine.RunCount(item.Rule);
            item.LastFiredText = count > 1
                ? when + " · " + string.Format(CultureInfo.CurrentCulture, _loc["Au_RunCount"], count)
                : when;
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
