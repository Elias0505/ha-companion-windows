// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>A single quick-action tile bound to one Home Assistant entity.</summary>
public partial class EntityTileViewModel : ObservableObject
{
    private readonly IHaConnection _connection;
    private readonly MdiIconProvider _icons;
    private readonly LocalizationService _localization;
    private HaEntityState _state;

    public string EntityId { get; }

    public string Domain { get; }

    /// <summary>Material Design Icons glyph (rendered with the bundled MDI font).</summary>
    [ObservableProperty]
    private string _iconGlyph;

    [ObservableProperty]
    private string _friendlyName;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isUnavailable;

    /// <summary>Whether this tile is pinned to the quick panel / favourites (managed by the catalog).</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Grid cells this tile spans horizontally (1–4; freely set by dragging the corner grip).</summary>
    [ObservableProperty]
    private int _colSpan = 1;

    /// <summary>Grid cells this tile spans vertically (1–3; freely set by dragging the corner grip).</summary>
    [ObservableProperty]
    private int _rowSpan = 1;

    /// <summary>Apply a tile size in grid cells (clamped to the supported range).</summary>
    public void SetSpans(int colSpan, int rowSpan)
    {
        ColSpan = Math.Clamp(colSpan, 1, 4);
        RowSpan = Math.Clamp(rowSpan, 1, 3);
    }

    public EntityTileViewModel(IHaConnection connection, MdiIconProvider icons, LocalizationService localization, HaEntityState state)
    {
        _connection = connection;
        _icons = icons;
        _localization = localization;
        _state = state;
        EntityId = state.EntityId;
        Domain = state.Domain;
        _iconGlyph = icons.Resolve(state);
        _friendlyName = state.FriendlyName;
        _stateText = FormatState(state);
        _isOn = state.IsOn;
        _isUnavailable = state.IsUnavailable;
        UpdateControlValues(state);
    }

    /// <summary>Apply a new state snapshot from Home Assistant.</summary>
    public void Update(HaEntityState state)
    {
        _state = state;
        IconGlyph = _icons.Resolve(state);
        FriendlyName = state.FriendlyName;
        StateText = FormatState(state);
        IsOn = state.IsOn;
        IsUnavailable = state.IsUnavailable;
        UpdateControlValues(state);
    }

    // ----- stage-2 controls (context flyout: brightness / target temperature / media) -----

    private const int ServiceThrottleMs = 300;
    private long _lastServiceMs;

    /// <summary>Lights: brightness slider in the tile's context flyout.</summary>
    public bool HasBrightness => Domain == "light";

    [ObservableProperty]
    private double _brightnessPct;

    /// <summary>Climate: target temperature +/- in the context flyout.</summary>
    public bool HasClimate => Domain == "climate";

    [ObservableProperty]
    private double _targetTemp;

    [ObservableProperty]
    private string _currentTempText = string.Empty;

    /// <summary>Media players: play/pause + volume in the context flyout.</summary>
    public bool HasMedia => Domain == "media_player";

    [ObservableProperty]
    private double _volumePct;

    private void UpdateControlValues(HaEntityState state)
    {
        if (HasBrightness && state.Attributes.TryGetValue("brightness", out var b)
            && b.ValueKind == System.Text.Json.JsonValueKind.Number)
            BrightnessPct = Math.Round(b.GetDouble() / 255.0 * 100.0);
        else if (HasBrightness && !state.IsOn)
            BrightnessPct = 0;

        if (HasClimate)
        {
            if (state.Attributes.TryGetValue("temperature", out var t)
                && t.ValueKind == System.Text.Json.JsonValueKind.Number)
                TargetTemp = t.GetDouble();
            CurrentTempText = state.Attributes.TryGetValue("current_temperature", out var c)
                              && c.ValueKind == System.Text.Json.JsonValueKind.Number
                ? $"{c.GetDouble():0.#}°"
                : string.Empty;
        }

        if (HasMedia && state.Attributes.TryGetValue("volume_level", out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.Number)
            VolumePct = Math.Round(v.GetDouble() * 100.0);
    }

    /// <summary>Slider handler; throttled — sliders fire continuously while dragging.</summary>
    public void SetBrightness(double pct)
    {
        if (!Throttle())
            return;
        _ = CallAsync("light", "turn_on", new Dictionary<string, object?> { ["brightness_pct"] = (int)pct });
    }

    public void NudgeTemperature(double delta)
    {
        TargetTemp = Math.Round((TargetTemp + delta) * 2) / 2; // 0.5° steps
        _ = CallAsync("climate", "set_temperature", new Dictionary<string, object?> { ["temperature"] = TargetTemp });
    }

    public void PlayPause() =>
        _ = CallAsync("media_player", "media_play_pause", null);

    public void SetVolume(double pct)
    {
        if (!Throttle())
            return;
        _ = CallAsync("media_player", "volume_set", new Dictionary<string, object?> { ["volume_level"] = Math.Round(pct / 100.0, 2) });
    }

    private bool Throttle()
    {
        var now = Environment.TickCount64;
        if (now - _lastServiceMs < ServiceThrottleMs)
            return false;
        _lastServiceMs = now;
        return true;
    }

    private async Task CallAsync(string domain, string service, Dictionary<string, object?>? data)
    {
        try
        {
            if (data is null)
                await _connection.CallServiceAsync(domain, service, EntityId);
            else
                await _connection.CallServiceAsync(domain, service, EntityId, data);
        }
        catch
        {
            // Best-effort: the real state arrives via the WebSocket feed.
        }
    }

    /// <summary>Localized category (domain group) name, e.g. "Lights" — shown in the add-tile search.</summary>
    public string CategoryName => _localization.Group(Domain);

    /// <summary>Re-derive the localized texts (called when the UI language changes).</summary>
    public void RefreshStateText()
    {
        StateText = FormatState(_state);
        OnPropertyChanged(nameof(CategoryName));
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!DomainCatalog.HasAction(Domain))
            return; // read-only tile (sensor): tapping shows, never calls a service

        var (domain, service) = DomainCatalog.ResolveAction(Domain, IsOn);
        try
        {
            await _connection.CallServiceAsync(domain, service, EntityId);
        }
        catch
        {
            // Best-effort: the real state will arrive via the WebSocket feed.
        }
    }

    private string FormatState(HaEntityState state)
    {
        if (state.IsUnavailable)
            return _localization["State_Unavailable"];
        var unit = state.GetAttributeString("unit_of_measurement");
        if (unit is not null)
            return $"{state.State} {unit}";
        // Localize the two ubiquitous states; anything else shows HA's raw value.
        return state.State.ToLowerInvariant() switch
        {
            "on" => _localization["State_On"],
            "off" => _localization["State_Off"],
            _ => Capitalize(state.State),
        };
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
