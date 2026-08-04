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
    [NotifyPropertyChangedFor(nameof(DataLabelText), nameof(IsPercent))]
    private ActionOption? _selectedAction;

    /// <summary>Optional service-data value (brightness %, volume %, temperature, position %).</summary>
    [ObservableProperty]
    private double _dataValue = 50;

    // ----- light turn_on data modes (unchanged / brightness / colour / both / colour temp) -----

    public ObservableCollection<LightDataModeOption> LightModes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrightness), nameof(ShowColor), nameof(ShowColorTemp))]
    private LightDataModeOption? _selectedLightMode;

    /// <summary>Quick colour swatches for the colour mode (shared palette with the tiles).</summary>
    public IReadOnlyList<ColorSwatchViewModel> ColorSwatches { get; }

    private (byte R, byte G, byte B) _rgb = (0, 122, 255); // default: the palette's blue

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorTempKelvinText))]
    private double _colorTempKelvin = 3000;

    public string ColorTempKelvinText => $"{(int)ColorTempKelvin} K";

    public double MinKelvin => Tile?.HasColorTemp == true ? Tile.MinColorTempKelvin : 2000;

    public double MaxKelvin => Tile?.HasColorTemp == true ? Tile.MaxColorTempKelvin : 6500;

    /// <summary>The persisted Data of the action being edited — foreign, hand-added keys
    /// must survive an edit round-trip, so BuildData merges instead of rebuilding.</summary>
    private IReadOnlyDictionary<string, object?>? _originalData;

    private static readonly string[] ManagedDataKeys =
        ["brightness_pct", "rgb_color", "color_temp_kelvin", "volume_level", "temperature", "position", "percentage"];

    public ActionDraftViewModel()
    {
        ColorSwatches = ColorSwatchViewModel.CreatePalette(SelectSwatch);
        ColorSwatches[5].IsCurrent = true; // matches the _rgb default
    }

    private void SelectSwatch(ColorSwatchViewModel swatch)
    {
        foreach (var s in ColorSwatches)
            s.IsCurrent = ReferenceEquals(s, swatch);
        _rgb = (swatch.R, swatch.G, swatch.B);
    }

    public bool HasTile => Tile is not null;

    public bool IsComplete => Tile is not null && SelectedAction is not null;

    private string Domain => Tile?.EntityId.Split('.')[0] ?? "";

    /// <summary>light + turn_on gets the data-mode chips instead of a forced value.</summary>
    public bool IsLightTurnOn => Domain == "light" && SelectedAction?.Action == AutomationActions.TurnOn;

    /// <summary>The set-verbs that carry a single data field (light turn_on has its own UI).</summary>
    public bool ShowSimpleData => DataKind is not null;

    public bool ShowBrightness => IsLightTurnOn && SelectedLightMode?.Key is "brightness" or "brightness_color";

    public bool ShowColor => IsLightTurnOn && SelectedLightMode?.Key is "color" or "brightness_color";

    public bool ShowColorTemp => IsLightTurnOn && SelectedLightMode?.Key == "color_temp";

    /// <summary>Whether the data value is a 0–100 percentage (vs. a raw number like °C).</summary>
    public bool IsPercent => DataKind is "volume" or "position" or "percentage";

    /// <summary>Kind of data control to show for the current action (null = none).</summary>
    public string? DataKind => (Domain, SelectedAction?.Action) switch
    {
        ("media_player", AutomationActions.SetVolume) => "volume",
        ("climate", AutomationActions.SetTemperature) => "temperature",
        ("cover", AutomationActions.SetPosition) => "position",
        ("fan", AutomationActions.SetPercentage) => "percentage",
        _ => null,
    };

    private string DataLabelKey => DataKind switch
    {
        "volume" => "Au_Volume",
        "temperature" => "Au_Temperature",
        "position" => "Au_Position",
        "percentage" => "Au_Percentage",
        _ => "",
    };

    public string DataLabelText => Loc is null || DataLabelKey.Length == 0 ? "" : Loc[DataLabelKey];

    /// <summary>
    /// Build the service data for the current action. Starts from the ORIGINAL persisted
    /// data (minus the keys this UI manages) so hand-edited extras in automations.json are
    /// not silently deleted by an edit round-trip.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? BuildData()
    {
        var data = _originalData is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(_originalData);
        foreach (var key in ManagedDataKeys)
            data.Remove(key);

        if (IsLightTurnOn)
        {
            switch (SelectedLightMode?.Key)
            {
                case "brightness":
                    data["brightness_pct"] = (int)DataValue;
                    break;
                case "color":
                    data["rgb_color"] = new int[] { _rgb.R, _rgb.G, _rgb.B };
                    break;
                case "brightness_color":
                    data["brightness_pct"] = (int)DataValue;
                    data["rgb_color"] = new int[] { _rgb.R, _rgb.G, _rgb.B };
                    break;
                case "color_temp":
                    data["color_temp_kelvin"] = (int)ColorTempKelvin;
                    break;
                // "none" (unchanged): send no light data — HA restores its last state
            }
        }
        else
        {
            switch (DataKind)
            {
                case "volume":
                    data["volume_level"] = Math.Round(DataValue / 100.0, 2);
                    break;
                case "temperature":
                    data["temperature"] = DataValue;
                    break;
                case "position":
                    data["position"] = (int)DataValue;
                    break;
                case "percentage":
                    data["percentage"] = (int)DataValue;
                    break;
            }
        }
        return data.Count == 0 ? null : data;
    }

    /// <summary>Seed the data UI from a persisted action (edit flow).</summary>
    public void SeedData(RuleAction action)
    {
        _originalData = action.Data;
        if (action.Data is null)
            return;

        var hasBrightness = TryGetDouble(action.Data, "brightness_pct", out var brightness);
        if (hasBrightness)
            DataValue = brightness;
        var hasRgb = TryGetRgb(action.Data, out var rgb);
        if (hasRgb)
        {
            _rgb = rgb;
            foreach (var s in ColorSwatches)
                s.IsCurrent = Math.Abs(s.R - rgb.R) + Math.Abs(s.G - rgb.G) + Math.Abs(s.B - rgb.B) <= 60;
        }
        var hasKelvin = TryGetDouble(action.Data, "color_temp_kelvin", out var kelvin);
        if (hasKelvin)
            ColorTempKelvin = kelvin;

        SelectedLightMode = LightModes.FirstOrDefault(m => m.Key == (hasBrightness, hasRgb, hasKelvin) switch
        {
            (true, true, _) => "brightness_color",
            (false, true, _) => "color",
            (_, _, true) => "color_temp",
            (true, false, false) => "brightness",
            _ => "none",
        }) ?? LightModes.FirstOrDefault();

        if (TryGetDouble(action.Data, "volume_level", out var volume))
            DataValue = Math.Round(volume * 100.0);
        else if (TryGetDouble(action.Data, "temperature", out var temperature))
            DataValue = temperature;
        else if (TryGetDouble(action.Data, "position", out var position))
            DataValue = position;
        else if (TryGetDouble(action.Data, "percentage", out var percentage))
            DataValue = percentage;
    }

    internal static bool TryGetDouble(IReadOnlyDictionary<string, object?> data, string key, out double value)
    {
        value = 0;
        if (!data.TryGetValue(key, out var raw) || raw is null)
            return false;
        switch (raw)
        {
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number:
                value = je.GetDouble();
                return true;
            case IConvertible c:
                value = c.ToDouble(CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryGetRgb(IReadOnlyDictionary<string, object?> data, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (!data.TryGetValue("rgb_color", out var raw) || raw is null)
            return false;
        var parts = new List<byte>(3);
        if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                    parts.Add((byte)Math.Clamp(item.GetDouble(), 0, 255));
            }
        }
        else if (raw is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                if (item is IConvertible c)
                    parts.Add((byte)Math.Clamp(c.ToDouble(CultureInfo.InvariantCulture), 0, 255));
            }
        }
        if (parts.Count != 3)
            return false;
        rgb = (parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Offer only the modes the target light actually supports.</summary>
    private void RebuildLightModes()
    {
        var previous = SelectedLightMode?.Key;
        LightModes.Clear();
        if (!IsLightTurnOn || Loc is null)
            return;
        LightModes.Add(new LightDataModeOption("none", Loc["Au_DataUnchanged"]));
        if (Tile?.HasBrightness == true)
            LightModes.Add(new LightDataModeOption("brightness", Loc["Au_Brightness"]));
        if (Tile?.HasColor == true)
        {
            LightModes.Add(new LightDataModeOption("color", Loc["Au_Color"]));
            if (Tile?.HasBrightness == true)
                LightModes.Add(new LightDataModeOption("brightness_color", Loc["Au_BrightnessColor"]));
        }
        if (Tile?.HasColorTemp == true)
            LightModes.Add(new LightDataModeOption("color_temp", Loc["Au_ColorTemp"]));
        SelectedLightMode = LightModes.FirstOrDefault(m => m.Key == previous) ?? LightModes[0];
    }

    partial void OnSelectedActionChanged(ActionOption? value)
    {
        RebuildLightModes();
        OnPropertyChanged(nameof(IsLightTurnOn));
        OnPropertyChanged(nameof(ShowSimpleData));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowColorTemp));
    }

    partial void OnTileChanged(EntityTileViewModel? value)
    {
        RebuildLightModes();
        OnPropertyChanged(nameof(HasTile));
        OnPropertyChanged(nameof(IsLightTurnOn));
        OnPropertyChanged(nameof(ShowSimpleData));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowColorTemp));
        OnPropertyChanged(nameof(DataLabelText));
        OnPropertyChanged(nameof(IsPercent));
        OnPropertyChanged(nameof(MinKelvin));
        OnPropertyChanged(nameof(MaxKelvin));
    }
}

/// <summary>One selectable data mode for a light turn_on action.</summary>
public sealed record LightDataModeOption(string Key, string Label);

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
public sealed record RuleActionView(string Glyph, string Name, string ActionLabel,
    string DataText = "", Microsoft.UI.Xaml.Media.SolidColorBrush? DataBrush = null)
{
    public bool HasDataBrush => DataBrush is not null;
}

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
    /// <summary>Builder-level error line (e.g. refusing a save that would drop actions).</summary>
    [ObservableProperty]
    private string _builderError = "";

    /// <summary>Compact, human-readable summary of an action's data for the rule list
    /// ("· 60 % · 2700 K" plus a colour dot) — a wrong value must be visible BEFORE it fires.</summary>
    private static (string Text, Microsoft.UI.Xaml.Media.SolidColorBrush? Brush) FormatActionData(
        IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null)
            return ("", null);
        var parts = new List<string>();
        if (ActionDraftViewModel.TryGetDouble(data, "brightness_pct", out var brightness))
            parts.Add($"{(int)brightness} %");
        if (ActionDraftViewModel.TryGetDouble(data, "volume_level", out var volume))
            parts.Add($"{(int)Math.Round(volume * 100.0)} %");
        if (ActionDraftViewModel.TryGetDouble(data, "temperature", out var temperature))
            parts.Add($"{temperature:0.#}\u00b0");
        if (ActionDraftViewModel.TryGetDouble(data, "position", out var position))
            parts.Add($"{(int)position} %");
        if (ActionDraftViewModel.TryGetDouble(data, "percentage", out var percentage))
            parts.Add($"{(int)percentage} %");
        if (ActionDraftViewModel.TryGetDouble(data, "color_temp_kelvin", out var kelvin))
            parts.Add($"{(int)kelvin} K");
        Microsoft.UI.Xaml.Media.SolidColorBrush? brush = null;
        if (ActionDraftViewModel.TryGetRgb(data, out var rgb))
            brush = new(global::Windows.UI.Color.FromArgb(255, rgb.R, rgb.G, rgb.B));
        return (parts.Count > 0 ? " \u00b7 " + string.Join(" \u00b7 ", parts) : "", brush);
    }

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

    public bool ShowSchedule => SelectedTrigger?.ParamKind == TriggerParamKind.Schedule;

    // ----- schedule trigger param (time + weekdays) -----

    [ObservableProperty]
    private TimeSpan _scheduleTime = new(7, 0, 0);

    [ObservableProperty] private bool _dayMon = true;
    [ObservableProperty] private bool _dayTue = true;
    [ObservableProperty] private bool _dayWed = true;
    [ObservableProperty] private bool _dayThu = true;
    [ObservableProperty] private bool _dayFri = true;
    [ObservableProperty] private bool _daySat;
    [ObservableProperty] private bool _daySun;

    private string BuildScheduleParam()
    {
        var days = new List<int>();
        if (DayMon) days.Add(1);
        if (DayTue) days.Add(2);
        if (DayWed) days.Add(3);
        if (DayThu) days.Add(4);
        if (DayFri) days.Add(5);
        if (DaySat) days.Add(6);
        if (DaySun) days.Add(7);
        // an empty set (no day ticked) means "every day" — never a rule that can't fire
        return new ScheduleSpec(TimeOnly.FromTimeSpan(ScheduleTime), days).ToParam();
    }

    private void SeedSchedule(string? param)
    {
        if (!ScheduleSpec.TryParse(param, out var spec))
            return;
        ScheduleTime = spec.Time.ToTimeSpan();
        var set = spec.Days.Count == 0 ? new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 } : new HashSet<int>(spec.Days);
        DayMon = set.Contains(1); DayTue = set.Contains(2); DayWed = set.Contains(3);
        DayThu = set.Contains(4); DayFri = set.Contains(5); DaySat = set.Contains(6); DaySun = set.Contains(7);
    }

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

    /// <summary>Header of the builder form ("New automation" / "Edit automation").</summary>
    public string EditTitle => _loc[IsEditingExisting ? "Au_EditTitle" : "Au_NewTitle"];

    /// <summary>Raised after the UI language changed and this view model has refreshed.
    /// The page uses it to re-apply the strings it sets imperatively.</summary>
    public event EventHandler? LanguageChanged;

    public bool CanAdd => BuildRule() is { } rule && rule.IsValid();

    /// <summary>Category filter for the in-editor device browse (tap = add as action).</summary>
    public DeviceBrowserViewModel Browser { get; }

    public AutomationsViewModel(IRulesStore store, IRulesEngine engine, IWindowsStateMonitor monitor,
        EntityCatalogViewModel catalog, LocalizationService loc, MdiIconProvider icons, IUiDispatcher ui,
        DeviceBrowserViewModel browser)
    {
        _store = store;
        _engine = engine;
        _monitor = monitor;
        Catalog = catalog;
        _loc = loc;
        _icons = icons;
        _ui = ui;
        Browser = browser;

        BuildTriggerGroups();
        ResetBuilder();
        Rebuild();

        // A live language switch must repaint every localized string, including the ones
        // that are plain computed properties (they have no setter to raise the change).
        _loc.LanguageChanged += (_, _) => _ui.Post(() =>
        {
            BuildTriggerGroups();
            Rebuild();
            OnPropertyChanged(nameof(EditTitle));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        });
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

    /// <summary>Open the New editor pre-seeded from a quick-start template (trigger only).</summary>
    [RelayCommand]
    private void BeginTemplate(string triggerKey)
    {
        BeginNew();
        SelectedTrigger = FindOption(triggerKey);
        if (triggerKey == WindowsTriggers.ToKey(WindowsTrigger.IdleStart))
            MinutesParam = 10;
    }

    /// <summary>Save the builder as a new rule or replace the one being edited (by id).</summary>
    [RelayCommand]
    private void Save()
    {
        BuilderError = "";
        if (BuildRule() is not { } rule || !rule.IsValid())
            return;
        var rules = _store.Load().ToList();
        var idx = _editingId is null ? -1 : rules.FindIndex(r => r.Id == _editingId);
        // Editing while entities are unresolved (e.g. disconnected, renamed) must not
        // silently drop actions the stored rule still has.
        if (idx >= 0 && rule.Actions.Count < rules[idx].Actions.Count)
        {
            BuilderError = _loc["Au_SaveIncomplete"];
            return;
        }
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
            else if (WindowsTriggers.ParamKind(t) == TriggerParamKind.Schedule)
                SeedSchedule(rule.Param);
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
                draft.SeedData(action);
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
            TriggerParamKind.Schedule => BuildScheduleParam(),
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
        OnPropertyChanged(nameof(ShowSchedule));
    }

    partial void OnSelectedTriggerChanged(TriggerOption? value) => NotifyBuilderChanged();

    partial void OnMinutesParamChanged(double value) => NotifyBuilderChanged();

    partial void OnProcessParamChanged(string value) => NotifyBuilderChanged();

    private void ResetBuilder()
    {
        BuilderError = "";
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
                var (dataText, dataBrush) = FormatActionData(action.Data);
                item.Actions.Add(new RuleActionView(
                    tile?.IconGlyph ?? _icons.DomainGlyph(action.EntityId.Split('.')[0]),
                    tile?.FriendlyName ?? action.EntityId,
                    _loc["Act_" + action.Action],
                    dataText, dataBrush));
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
                TriggerParamKind.Schedule => ScheduleText(rule.Param),
                _ => label,
            }
            : label;
    }

    private string ScheduleText(string? param)
    {
        if (!ScheduleSpec.TryParse(param, out var spec))
            return _loc["Trig_schedule"];
        var time = spec.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (spec.Days.Count is 0 or 7)
            return $"{time} · {_loc["Au_EveryDay"]}";
        var abbr = new[] { "", "Day_Mo", "Day_Tu", "Day_We", "Day_Th", "Day_Fr", "Day_Sa", "Day_Su" };
        return $"{time} · {string.Join(" ", spec.Days.Select(d => _loc[abbr[d]]))}";
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
        AddGroup("TrigGrp_schedule", new[] { WindowsTrigger.Schedule });
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
        WindowsTrigger.Schedule => "\uE787",         // Calendar
        WindowsTrigger.AppStart or WindowsTrigger.AppStop => "\uE71D",     // AllApps
        WindowsTrigger.MicOn or WindowsTrigger.MicOff => "\uE720",         // Microphone
        WindowsTrigger.CamOn or WindowsTrigger.CamOff => "\uE714",         // Video
        WindowsTrigger.AudioStart => "\uE767",       // Volume
        WindowsTrigger.AudioStop => "\uE74F",        // Mute
        _ => "\uE945",
    };
}
