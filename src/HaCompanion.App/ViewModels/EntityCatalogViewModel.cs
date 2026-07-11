// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>
/// Auto-detected catalog of actionable Home Assistant entities: grouped by domain
/// (browse view) plus a user-curated, ordered "pinned" list (quick panel / favourites)
/// with a shared edit mode. Kept live via <see cref="IHaConnection.EntityUpdated"/>.
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
    private readonly ITileLayoutStore _layoutStore;
    private readonly MdiIconProvider _icons;
    private readonly Dictionary<string, EntityTileViewModel> _tilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGroupViewModel> _groupsByDomain = new(StringComparer.Ordinal);
    private List<string> _pinnedIds;
    private bool _suppressLayoutSave;

    public ObservableCollection<EntityGroupViewModel> Groups { get; } = new();

    /// <summary>User-curated, ordered tiles (quick panel + favourites). Reorderable via drag.</summary>
    public ObservableCollection<EntityTileViewModel> Pinned { get; } = new();

    /// <summary>Live search results for the "add tile" flyout.</summary>
    public ObservableCollection<EntityTileViewModel> AddCandidates { get; } = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _hasPinned;

    [ObservableProperty]
    private bool _isEditing;

    public bool ShowBrowse => !HasPinned && !IsEditing;

    public bool ShowPinnedArea => HasPinned || IsEditing;

    public bool ShowEditHint => IsEditing && !HasPinned;

    public EntityCatalogViewModel(IHaConnection connection, IUiDispatcher ui, ITileLayoutStore layoutStore, MdiIconProvider icons)
    {
        _connection = connection;
        _ui = ui;
        _layoutStore = layoutStore;
        _icons = icons;
        _pinnedIds = [.. _layoutStore.LoadPinned()];

        foreach (var state in _connection.Entities.Values)
            Apply(state);
        UpdateDerived();

        Pinned.CollectionChanged += OnPinnedCollectionChanged;
        _connection.EntityUpdated += OnEntityUpdated;
    }

    /// <summary>Pin or unpin a tile (pin appends at the end).</summary>
    public void TogglePin(EntityTileViewModel tile)
    {
        if (tile.IsPinned)
        {
            tile.IsPinned = false;
            Pinned.Remove(tile);
        }
        else
        {
            tile.IsPinned = true;
            Pinned.Add(tile);
        }
    }

    /// <summary>Refresh <see cref="AddCandidates"/> for the add-tile flyout.</summary>
    public void FilterCandidates(string query)
    {
        AddCandidates.Clear();
        var matches = _tilesById.Values
            .Where(t => !t.IsPinned)
            .Where(t => string.IsNullOrWhiteSpace(query)
                        || t.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.EntityId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => DomainRank(t.Domain))
            .ThenBy(t => t.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Take(60);
        foreach (var tile in matches)
            AddCandidates.Add(tile);
    }

    private void OnEntityUpdated(object? sender, HaEntityState state) =>
        _ui.Post(() =>
        {
            Apply(state);
            UpdateDerived();
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

        var tile = new EntityTileViewModel(_connection, _icons, state);
        _tilesById[state.EntityId] = tile;

        var group = GetOrCreateGroup(state.Domain);
        InsertTileSorted(group, tile);
        group.Count = group.Tiles.Count;

        var pinnedRank = _pinnedIds.IndexOf(tile.EntityId);
        if (pinnedRank >= 0)
        {
            tile.IsPinned = true;
            InsertPinnedInStoredOrder(tile, pinnedRank);
        }
    }

    private void InsertPinnedInStoredOrder(EntityTileViewModel tile, int rank)
    {
        _suppressLayoutSave = true;
        try
        {
            var index = 0;
            while (index < Pinned.Count && _pinnedIds.IndexOf(Pinned[index].EntityId) < rank)
                index++;
            Pinned.Insert(index, tile);
        }
        finally
        {
            _suppressLayoutSave = false;
        }
    }

    private void OnPinnedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasPinned = Pinned.Count > 0;
        if (_suppressLayoutSave)
            return;

        // Visible order first, then keep ids of pinned entities that haven't shown up yet
        // (e.g. temporarily unavailable) so they aren't silently dropped.
        var visible = Pinned.Select(t => t.EntityId).ToList();
        foreach (var id in _pinnedIds)
            if (!visible.Contains(id) && !_tilesById.ContainsKey(id))
                visible.Add(id);

        _pinnedIds = visible;
        _layoutStore.SavePinned(_pinnedIds);
    }

    partial void OnHasPinnedChanged(bool value) => NotifyLayoutFlags();

    partial void OnIsEditingChanged(bool value) => NotifyLayoutFlags();

    private void NotifyLayoutFlags()
    {
        OnPropertyChanged(nameof(ShowBrowse));
        OnPropertyChanged(nameof(ShowPinnedArea));
        OnPropertyChanged(nameof(ShowEditHint));
    }

    private EntityGroupViewModel GetOrCreateGroup(string domain)
    {
        if (_groupsByDomain.TryGetValue(domain, out var group))
            return group;

        group = new EntityGroupViewModel(domain, HeaderFor(domain), _icons.DomainGlyph(domain));
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

    /// <summary>Map a list of entity ids to the known actionable tiles (in the given order).</summary>
    public IEnumerable<EntityTileViewModel> ResolveTiles(IEnumerable<string> entityIds)
    {
        foreach (var id in entityIds)
            if (_tilesById.TryGetValue(id, out var tile))
                yield return tile;
    }

    private void UpdateDerived() => IsEmpty = _tilesById.Count == 0;
}
