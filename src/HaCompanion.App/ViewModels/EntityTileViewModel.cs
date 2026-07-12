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
    }

    /// <summary>Re-derive the localized state text (called when the UI language changes).</summary>
    public void RefreshStateText() => StateText = FormatState(_state);

    [RelayCommand]
    private async Task ToggleAsync()
    {
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
