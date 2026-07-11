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

    public EntityTileViewModel(IHaConnection connection, MdiIconProvider icons, LocalizationService localization, HaEntityState state)
    {
        _connection = connection;
        _icons = icons;
        _localization = localization;
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
        IconGlyph = _icons.Resolve(state);
        FriendlyName = state.FriendlyName;
        StateText = FormatState(state);
        IsOn = state.IsOn;
        IsUnavailable = state.IsUnavailable;
    }

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
        return unit is null ? Capitalize(state.State) : $"{state.State} {unit}";
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
