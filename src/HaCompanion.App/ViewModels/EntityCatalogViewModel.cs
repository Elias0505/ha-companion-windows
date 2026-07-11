// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>
/// Auto-detected catalog of actionable Home Assistant entities, kept live via the
/// connection's <see cref="IHaConnection.EntityUpdated"/> events. Shared by the
/// dashboard and the quick panel.
/// </summary>
public sealed partial class EntityCatalogViewModel : ObservableObject
{
    private readonly IHaConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, EntityTileViewModel> _byId = new(StringComparer.Ordinal);

    public ObservableCollection<EntityTileViewModel> Tiles { get; } = new();

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

        if (_byId.TryGetValue(state.EntityId, out var tile))
        {
            tile.Update(state);
        }
        else
        {
            tile = new EntityTileViewModel(_connection, state);
            _byId[state.EntityId] = tile;
            InsertSorted(tile);
        }
    }

    private void InsertSorted(EntityTileViewModel tile)
    {
        var index = 0;
        while (index < Tiles.Count && Compare(Tiles[index], tile) < 0)
            index++;
        Tiles.Insert(index, tile);
    }

    private static int Compare(EntityTileViewModel a, EntityTileViewModel b)
    {
        var byDomain = string.CompareOrdinal(a.Domain, b.Domain);
        return byDomain != 0
            ? byDomain
            : string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateEmpty() => IsEmpty = Tiles.Count == 0;
}
