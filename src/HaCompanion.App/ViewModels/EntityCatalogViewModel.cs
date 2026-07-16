// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
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
        ("sensor", "Sensors"),
        ("binary_sensor", "Sensors"),
    };

    private readonly IHaConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly ITileLayoutStore _layoutStore;
    private readonly MdiIconProvider _icons;
    private readonly LocalizationService _localization;
    private readonly Dictionary<string, EntityTileViewModel> _tilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityGroupViewModel> _groupsByDomain = new(StringComparer.Ordinal);
    private List<string> _pinnedIds;
    private readonly Dictionary<string, (int Cols, int Rows)> _spansById;
    private bool _suppressLayoutSave;

    /// <summary>Raised after a tile's size mode changed (views refresh the container spans).</summary>
    public event EventHandler<EntityTileViewModel>? TileSizeChanged;

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

    /// <summary>Quick panel: show the "no favourites yet" hint whenever nothing is pinned.</summary>
    public bool ShowNoFavHint => !HasPinned;

    public EntityCatalogViewModel(IHaConnection connection, IUiDispatcher ui, ITileLayoutStore layoutStore, MdiIconProvider icons, LocalizationService localization)
    {
        _connection = connection;
        _ui = ui;
        _layoutStore = layoutStore;
        _icons = icons;
        _localization = localization;
        _pinnedIds = [.. _layoutStore.LoadPinned()];
        _spansById = new Dictionary<string, (int, int)>(_layoutStore.LoadSpans(), StringComparer.Ordinal);

        foreach (var state in _connection.Entities.Values)
            Apply(state);
        UpdateDerived();

        Pinned.CollectionChanged += OnPinnedCollectionChanged;
        _connection.EntityUpdated += OnEntityUpdated;
        _connection.StatusChanged += OnConnectionStatusChanged;
        _localization.LanguageChanged += (_, _) => _ui.Post(RefreshGroupHeaders);
    }

    private bool _connectionLost;

    // This catalog owns every tile instance shown anywhere (dashboard, quick panel,
    // pickers) — one hook here greys ALL tiles out when the connection itself is gone,
    // instead of leaving frozen last-known values on screen.
    private void OnConnectionStatusChanged(object? sender, HaConnectionStatus status)
    {
        var lost = status != HaConnectionStatus.Connected;
        if (lost == _connectionLost)
            return;
        _connectionLost = lost;
        _ui.Post(() =>
        {
            foreach (var tile in _tilesById.Values)
                tile.SetConnectionLost(lost);
        });
    }

    private void RefreshGroupHeaders()
    {
        foreach (var group in _groupsByDomain.Values)
            group.Header = _localization.Group(group.Domain);
        foreach (var tile in _tilesById.Values)
            tile.RefreshStateText(); // "On"/"Off" are localized too
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

    /// <summary>Set a tile's size in grid cells, persist it and notify the views.</summary>
    public void SetTileSpans(EntityTileViewModel tile, int cols, int rows)
    {
        tile.SetSpans(cols, rows);
        if (tile is { ColSpan: 1, RowSpan: 1 })
            _spansById.Remove(tile.EntityId);
        else
            _spansById[tile.EntityId] = (tile.ColSpan, tile.RowSpan);
        _layoutStore.SaveSpans(_spansById);
        TileSizeChanged?.Invoke(this, tile);
    }

    /// <summary>Advance a tile through the size presets (1×1 → 2×1 → 2×2 → 1×1).</summary>
    public void CycleTileSize(EntityTileViewModel tile)
    {
        var (cols, rows) = (tile.ColSpan, tile.RowSpan) switch
        {
            (1, 1) => (2, 1),
            (2, 1) => (2, 2),
            _ => (1, 1),
        };
        SetTileSpans(tile, cols, rows);
    }

    /// <summary>The tile for an entity id, or null when unknown (e.g. entity removed in HA).</summary>
    public EntityTileViewModel? FindTile(string entityId) =>
        _tilesById.GetValueOrDefault(entityId);

    /// <summary>
    /// Search all tiles (pinned or not). With <paramref name="actionableOnly"/> the filter runs
    /// BEFORE the take — filtering afterwards could return nothing even though actionable
    /// matches exist, whenever a query mostly hits (read-only) sensors.
    /// </summary>
    public IReadOnlyList<EntityTileViewModel> SearchTiles(string query, int take = 20, bool actionableOnly = false) =>
        _tilesById.Values
            .Where(t => !actionableOnly || DomainCatalog.HasAction(t.Domain))
            .Where(t => string.IsNullOrWhiteSpace(query)
                        || t.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.EntityId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => DomainRank(t.Domain))
            .ThenBy(t => t.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();

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
        if (!DomainCatalog.IsDisplayable(state.Domain))
            return;

        if (_tilesById.TryGetValue(state.EntityId, out var existing))
        {
            existing.Update(state);
            return;
        }

        var tile = new EntityTileViewModel(_connection, _icons, _localization, state);
        var (cols, rows) = _spansById.GetValueOrDefault(state.EntityId, (1, 1));
        tile.SetSpans(cols, rows);
        _tilesById[state.EntityId] = tile;

        // Read-only domains (sensors) stay OUT of the browse groups — a PV setup easily has
        // hundreds of them and they would drown the start page and the quick-pick. They are
        // still searchable (add flyout) and pinnable like any other tile.
        if (!DomainCatalog.ReadOnly.Contains(state.Domain))
        {
            var group = GetOrCreateGroup(state.Domain);
            InsertTileSorted(group, tile);
            group.Count = group.Tiles.Count;
        }

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
        OnPropertyChanged(nameof(ShowNoFavHint));
    }

    private EntityGroupViewModel GetOrCreateGroup(string domain)
    {
        if (_groupsByDomain.TryGetValue(domain, out var group))
            return group;

        group = new EntityGroupViewModel(domain, _localization.Group(domain), _icons.DomainGlyph(domain));
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

    /// <summary>Category order used for grouping/sorting (matches the start page's sections).</summary>
    public static int DomainRank(string domain)
    {
        for (var i = 0; i < DomainOrder.Length; i++)
            if (DomainOrder[i].Domain == domain)
                return i;
        return DomainOrder.Length;
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
