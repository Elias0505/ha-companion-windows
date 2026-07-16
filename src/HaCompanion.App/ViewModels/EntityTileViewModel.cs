// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Infrastructure.IUiDispatcher _ui;
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

    public EntityTileViewModel(IHaConnection connection, MdiIconProvider icons, LocalizationService localization,
        Infrastructure.IUiDispatcher ui, HaEntityState state)
    {
        _connection = connection;
        _icons = icons;
        _localization = localization;
        _ui = ui;
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
        IsUnavailable = _connectionLost || state.IsUnavailable;
        UpdateControlValues(state);
    }

    private bool _connectionLost;

    /// <summary>
    /// The connection to Home Assistant itself dropped (or came back): frozen last-known
    /// values would be a lie, so the tile greys out like an unavailable entity. On restore
    /// everything re-derives from the (freshly reloaded) state.
    /// </summary>
    public void SetConnectionLost(bool lost)
    {
        if (_connectionLost == lost)
            return;
        _connectionLost = lost;
        IsUnavailable = lost || _state.IsUnavailable;
        StateText = FormatState(_state);
    }

    // ----- stage-2 controls (context flyout: brightness / target temperature / media) -----

    private const int SliderDebounceMs = 250;
    private int _sliderVersion;

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

    /// <summary>
    /// Slider handler; debounced TRAILING — sliders fire continuously while dragging, and a
    /// leading throttle would drop the FINAL value, leaving the light at wherever the last
    /// non-throttled tick happened instead of where the user released the thumb.
    /// </summary>
    public void SetBrightness(double pct)
    {
        BrightnessPct = pct;
        DebouncedCall("light", "turn_on", new Dictionary<string, object?> { ["brightness_pct"] = (int)pct });
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
        VolumePct = pct;
        DebouncedCall("media_player", "volume_set", new Dictionary<string, object?> { ["volume_level"] = Math.Round(pct / 100.0, 2) });
    }

    /// <summary>Send only the LAST value once the slider has been still for the debounce window.</summary>
    private void DebouncedCall(string domain, string service, Dictionary<string, object?> data)
    {
        var version = Interlocked.Increment(ref _sliderVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(SliderDebounceMs).ConfigureAwait(false);
            if (version == _sliderVersion)
                await CallAsync(domain, service, data).ConfigureAwait(false);
        });
    }

    private async Task CallAsync(string domain, string service, Dictionary<string, object?>? data)
    {
        if (IsUnavailable)
            return; // no point firing a doomed service call
        try
        {
            if (data is null)
                await _connection.CallServiceAsync(domain, service, EntityId);
            else
                await _connection.CallServiceAsync(domain, service, EntityId, data);
        }
        catch
        {
            FlashActionFailed();
        }
    }

    private int _failFlashVersion;

    /// <summary>
    /// A rejected service call must not fail silently: show "action failed" on the tile
    /// briefly, then re-derive from the real state (same trailing-version pattern as the
    /// slider debounce — a newer flash or state update wins).
    /// </summary>
    private void FlashActionFailed()
    {
        var version = Interlocked.Increment(ref _failFlashVersion);
        // Callers may be off the UI thread (slider debounce runs on the pool) — bound
        // properties must only change on the UI thread.
        _ui.Post(() => StateText = _localization["Tile_ActionFailed"]);
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            if (version == _failFlashVersion)
                _ui.Post(() => StateText = FormatState(_state));
        });
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
        if (IsUnavailable)
            return; // entity gone or connection lost — the call could not succeed

        var (domain, service) = DomainCatalog.ResolveAction(Domain, IsOn);
        try
        {
            await _connection.CallServiceAsync(domain, service, EntityId);
        }
        catch
        {
            FlashActionFailed();
        }
    }

    private string FormatState(HaEntityState state)
    {
        if (_connectionLost || state.IsUnavailable)
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
