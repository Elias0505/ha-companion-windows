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

    /// <summary>Playing media counts as active too — <see cref="HaEntityState.IsOn"/> only knows
    /// on/open/home/unlocked, so a playing Sonos would render with a grey accent circle.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>media_player state is "playing" (drives the play/pause glyph).</summary>
    [ObservableProperty]
    private bool _isPlaying;

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
        _isPlaying = IsPlayingState(state);
        _isActive = state.IsOn || _isPlaying;
        _isUnavailable = state.IsUnavailable;
        if (Domain == "light")
            _colorSwatches = ColorSwatchViewModel.CreatePalette(s => SetColor(s.R, s.G, s.B));
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
        IsPlaying = IsPlayingState(state);
        IsActive = state.IsOn || IsPlaying;
        IsUnavailable = _connectionLost || state.IsUnavailable;
        UpdateControlValues(state);
    }

    private static bool IsPlayingState(HaEntityState state) =>
        state.State.Equals("playing", StringComparison.OrdinalIgnoreCase);

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

    // Home Assistant MediaPlayerEntityFeature bits (the ones this UI cares about).
    private const long MediaFeaturePause = 1;
    private const long MediaFeatureVolumeSet = 4;
    private const long MediaFeaturePrevious = 16;
    private const long MediaFeatureNext = 32;
    private const long MediaFeaturePlay = 16384;

    private static readonly string[] ColorCapableModes = ["hs", "rgb", "rgbw", "rgbww", "xy"];
    private static readonly string[] BrightnessCapableModes = ["brightness", "color_temp", "hs", "rgb", "rgbw", "rgbww", "xy", "white"];

    /// <summary>Lights that can actually dim (from supported_color_modes; permissive when
    /// the attribute is absent so exotic integrations keep their slider).</summary>
    [ObservableProperty]
    private bool _hasBrightness;

    [ObservableProperty]
    private double _brightnessPct;

    /// <summary>Lights that support real colour (hs/rgb/rgbw/rgbww/xy modes).</summary>
    [ObservableProperty]
    private bool _hasColor;

    /// <summary>Lights with a tunable white channel (color_temp mode).</summary>
    [ObservableProperty]
    private bool _hasColorTemp;

    [ObservableProperty]
    private double _colorTempKelvin = 3000;

    [ObservableProperty]
    private double _minColorTempKelvin = 2000;

    [ObservableProperty]
    private double _maxColorTempKelvin = 6500;

    /// <summary>Quick colour swatches for the context flyout (lights only, else empty).</summary>
    public IReadOnlyList<ColorSwatchViewModel> ColorSwatches => _colorSwatches;
    private IReadOnlyList<ColorSwatchViewModel> _colorSwatches = Array.Empty<ColorSwatchViewModel>();

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

    /// <summary>supported_features says play and/or pause work (permissive when absent).</summary>
    [ObservableProperty]
    private bool _canPlayPause;

    /// <summary>supported_features says next AND previous track work.</summary>
    [ObservableProperty]
    private bool _canSkip;

    /// <summary>supported_features says the volume can be set.</summary>
    [ObservableProperty]
    private bool _canSetVolume;

    /// <summary>Segoe glyph for the tile's inline play/pause button (flips with the state).</summary>
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseGlyph));

    /// <summary>The inline play/pause overlay on the tile itself (media players that can).</summary>
    public bool ShowInlinePlayPause => HasMedia && CanPlayPause;

    partial void OnCanPlayPauseChanged(bool value) => OnPropertyChanged(nameof(ShowInlinePlayPause));

    private void UpdateControlValues(HaEntityState state)
    {
        if (Domain == "light")
        {
            var modes = state.GetAttributeStringList("supported_color_modes");
            HasBrightness = modes.Count == 0 || modes.Any(m => BrightnessCapableModes.Contains(m));
            HasColor = modes.Any(m => ColorCapableModes.Contains(m));
            HasColorTemp = modes.Contains("color_temp");

            if (state.Attributes.TryGetValue("brightness", out var b)
                && b.ValueKind == System.Text.Json.JsonValueKind.Number)
                BrightnessPct = Math.Round(b.GetDouble() / 255.0 * 100.0);
            else if (!state.IsOn)
                BrightnessPct = 0;

            if (HasColorTemp)
            {
                MinColorTempKelvin = state.GetAttributeDouble("min_color_temp_kelvin") ?? 2000;
                MaxColorTempKelvin = state.GetAttributeDouble("max_color_temp_kelvin") ?? 6500;
                if (state.GetAttributeDouble("color_temp_kelvin") is { } kelvin)
                    ColorTempKelvin = Math.Clamp(kelvin, MinColorTempKelvin, MaxColorTempKelvin);
            }

            if (HasColor)
                MarkCurrentSwatch(state);
        }

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

        if (HasMedia)
        {
            if (state.Attributes.TryGetValue("volume_level", out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                VolumePct = Math.Round(v.GetDouble() * 100.0);

            // No supported_features: stay permissive rather than hiding working controls.
            var features = (long)(state.GetAttributeDouble("supported_features") ?? -1);
            CanPlayPause = features < 0 || (features & (MediaFeaturePlay | MediaFeaturePause)) != 0;
            CanSkip = features < 0 || (features & MediaFeatureNext) != 0 && (features & MediaFeaturePrevious) != 0;
            CanSetVolume = features < 0 || (features & MediaFeatureVolumeSet) != 0;
        }
    }

    /// <summary>Highlight the swatch matching the light's current rgb_color (tolerance-based).</summary>
    private void MarkCurrentSwatch(HaEntityState state)
    {
        byte r = 0, g = 0, b = 0;
        var haveColor = false;
        if (state.Attributes.TryGetValue("rgb_color", out var rgb)
            && rgb.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var parts = new List<byte>(3);
            foreach (var item in rgb.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                    parts.Add((byte)Math.Clamp(item.GetDouble(), 0, 255));
            }
            if (parts.Count == 3)
            {
                (r, g, b) = (parts[0], parts[1], parts[2]);
                haveColor = state.IsOn;
            }
        }

        foreach (var swatch in _colorSwatches)
        {
            swatch.IsCurrent = haveColor
                && Math.Abs(swatch.R - r) + Math.Abs(swatch.G - g) + Math.Abs(swatch.B - b) <= 60;
        }
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

    public void NextTrack() =>
        _ = CallAsync("media_player", "media_next_track", null);

    public void PreviousTrack() =>
        _ = CallAsync("media_player", "media_previous_track", null);

    /// <summary>Swatch click: one-shot, no debounce needed.</summary>
    public void SetColor(byte r, byte g, byte b) =>
        _ = CallAsync("light", "turn_on", new Dictionary<string, object?> { ["rgb_color"] = new int[] { r, g, b } });

    public void SetColorTemp(double kelvin)
    {
        ColorTempKelvin = kelvin;
        DebouncedCall("light", "turn_on", new Dictionary<string, object?> { ["color_temp_kelvin"] = (int)kelvin });
    }

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
        // A playing/paused media player names its track — far more useful than the bare state.
        if (HasMedia
            && (state.State.Equals("playing", StringComparison.OrdinalIgnoreCase)
                || state.State.Equals("paused", StringComparison.OrdinalIgnoreCase))
            && state.GetAttributeString("media_title") is { Length: > 0 } title)
            return $"{Capitalize(state.State)} · {title}";
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

/// <summary>
/// One quick-colour swatch (tile flyout AND automation builder — same palette, different
/// apply callbacks). <see cref="IsCurrent"/> marks the swatch matching the current colour.
/// </summary>
public sealed partial class ColorSwatchViewModel : ObservableObject
{
    private readonly Action<ColorSwatchViewModel> _apply;

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    /// <summary>Fill brush for the swatch button (created on the UI thread with its owner).</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush { get; }

    /// <summary>Check-mark colour with contrast against THIS swatch (an accent ring would
    /// vanish on the blue swatch, a white one on white — luminance decides instead).</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush CheckBrush { get; }

    [ObservableProperty]
    private bool _isCurrent;

    private ColorSwatchViewModel(Action<ColorSwatchViewModel> apply, byte r, byte g, byte b)
    {
        _apply = apply;
        (R, G, B) = (r, g, b);
        Brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
        var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
        CheckBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            luminance > 150 ? global::Windows.UI.Color.FromArgb(255, 20, 20, 20) : global::Windows.UI.Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>Invoke the owner's apply action with this colour.</summary>
    public void Apply() => _apply(this);

    public static IReadOnlyList<ColorSwatchViewModel> CreatePalette(Action<ColorSwatchViewModel> apply) =>
    [
        new(apply, 255, 59, 48),   // red
        new(apply, 255, 149, 0),   // orange
        new(apply, 255, 204, 0),   // yellow
        new(apply, 52, 199, 89),   // green
        new(apply, 50, 173, 230),  // cyan
        new(apply, 0, 122, 255),   // blue
        new(apply, 175, 82, 222),  // purple
        new(apply, 255, 255, 255), // white
    ];
}
