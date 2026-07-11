// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>
/// Auto-detected catalog of actionable Home Assistant entities, automatically
/// grouped by domain (in a sensible order) and sorted by name within each group.
/// Kept live via <see cref="IHaConnection.EntityUpdated"/>. Shared by the dashboard
/// and the quick panel.
/// </summary>
public sealed partial class EntityCatalogViewModel : ObservableObject
{
    // Display order + friendly section titles. Domains not listed sort to the end.
    private static readonly (string Domain, string Header)[] DomainOrder =
    {
        ("light", "Lights"),
        ("switch", "Switches"),
        ("cover", "Covers"),
        ("climate", "Climate"),
        ("fan", "Fans"),
        ("media_player", "Media"),
        ("lock", "Locks"),
        ("scene", "Scenes"),
        ("script", "Scripts"),
        ("automation", "Automations"),
        ("input_boolean", "Helpers"),
        ("button", "Buttons"),
    };

    private readonly IHaConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, EntityTileViewModel> _tilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGroupViewModel> _groupsByDomain = new(StringComparer.Ordinal);

    public ObservableCollection<EntityGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    public EntityCatalogViewModel(IHaConnection connection, IUiDispatcher ui)
    {
        _connection = connection;
        _ui = ui;

        foreach (var state in _connection.Entities.Values)
            Apply(state);
        UpdateEmpty();

        _connection.EntityUpdated += OnEntityUpdated;
    }

    private void OnEntityUpdated(object? sender, HaEntityState state) =>
        _ui.Post(() =>
        {
            Apply(state);
            UpdateEmpty();
        });

    private void Apply(HaEntityState state)
    {
        if (!DomainCatalog.IsActionable(state.Domain))
            return;

        if (_tilesById.TryGetValue(state.EntityId, out var existing))
        {
            existing.Update(state);
            return;
        }

        var tile = new EntityTileViewModel(_connection, state);
        _tilesById[state.EntityId] = tile;

        var group = GetOrCreateGroup(state.Domain);
        InsertTileSorted(group, tile);
        group.Count = group.Tiles.Count;
    }

    private EntityGroupViewModel GetOrCreateGroup(string domain)
    {
        if (_groupsByDomain.TryGetValue(domain, out var group))
            return group;

        group = new EntityGroupViewModel(domain, HeaderFor(domain), DomainCatalog.Glyph(domain));
        _groupsByDomain[domain] = group;
        InsertGroupSorted(group);
        return group;
    }

    private void InsertGroupSorted(EntityGroupViewModel group)
    {
        var rank = DomainRank(group.Domain);
        var index = 0;
        while (index < Groups.Count && DomainRank(Groups[index].Domain) < rank)
            index++;
        Groups.Insert(index, group);
    }

    private static void InsertTileSorted(EntityGroupViewModel group, EntityTileViewModel tile)
    {
        var index = 0;
        while (index < group.Tiles.Count &&
               string.Compare(group.Tiles[index].FriendlyName, tile.FriendlyName, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        group.Tiles.Insert(index, tile);
    }

    private static int DomainRank(string domain)
    {
        for (var i = 0; i < DomainOrder.Length; i++)
            if (DomainOrder[i].Domain == domain)
                return i;
        return DomainOrder.Length;
    }

    private static string HeaderFor(string domain)
    {
        foreach (var (d, header) in DomainOrder)
            if (d == domain)
                return header;
        return char.ToUpperInvariant(domain[0]) + domain[1..].Replace('_', ' ');
    }

    private void UpdateEmpty() => IsEmpty = _tilesById.Count == 0;
}
