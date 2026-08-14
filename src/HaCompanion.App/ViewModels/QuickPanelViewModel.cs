// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>
/// View model for the slide-in quick panel (Win+Ctrl+H): a dashboard picker over
/// "Favourites" (the editable pinned tiles) plus the user's real HA dashboards,
/// which are shown 1:1 in an embedded chrome-less WebView.
/// </summary>
public sealed partial class QuickPanelViewModel : ObservableObject
{
    private readonly IHaConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore _settingsStore;
    private readonly LocalizationService _localization;
    private readonly MdiIconProvider _icons;

    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Dropdown entries: Favourites + the user's HA dashboards.</summary>
    public ObservableCollection<QuickDashboard> Dashboards { get; } = new() { QuickDashboard.Favorites };

    /// <summary>
    /// Category mode: the pinned tiles grouped into named sections (same order and localized
    /// titles as the start page). Only populated while <see cref="SortByCategory"/> is on;
    /// manual mode binds the grid straight to <see cref="EntityCatalogViewModel.Pinned"/>.
    /// </summary>
    public ObservableCollection<EntityGroupViewModel> PinnedGroups { get; } = new();

    /// <summary>Sort favourites into named category sections instead of the manual order.</summary>
    [ObservableProperty]
    private bool _sortByCategory;

    /// <summary>
    /// Header pin: true while the CURRENT view is the configured default. Toggling on pins
    /// the current view as the start default; toggling off returns to "remember last view" —
    /// no trip to Settings needed.
    /// </summary>
    [ObservableProperty]
    private bool _isDefaultView;

    private bool _syncingDefaultFlag;
    private bool _rebuildingDashboards;

    [ObservableProperty]
    private QuickDashboard _selectedDashboard = QuickDashboard.Favorites;

    [ObservableProperty]
    private bool _showFavorites = true;

    public bool ShowDashboard => !ShowFavorites;

    /// <summary>Raised when a real HA dashboard is chosen; the window navigates the WebView.</summary>
    public event EventHandler<QuickDashboard>? DashboardRequested;

    public QuickPanelViewModel(EntityCatalogViewModel catalog, ShellViewModel shell, IHaConnection connection, IUiDispatcher ui, ISettingsStore settingsStore, LocalizationService localization, MdiIconProvider icons)
    {
        Catalog = catalog;
        Shell = shell;
        _connection = connection;
        _ui = ui;
        _settingsStore = settingsStore;
        _localization = localization;
        _icons = icons;
        _sortByCategory = settingsStore.Load().QuickPanelSortByCategory;
        Catalog.Pinned.CollectionChanged += (_, _) => RebuildGroups();
        _localization.LanguageChanged += (_, _) => _ui.Post(RebuildGroups); // section titles are localized
        RebuildGroups();
        SyncDefaultFlag();
    }

    private static string EncodeView(QuickDashboard? view) =>
        view is null or { IsFavorites: true } ? "favorites" : $"dash:{view.UrlPath ?? ""}";

    private void SyncDefaultFlag()
    {
        _syncingDefaultFlag = true;
        IsDefaultView = _settingsStore.Load().QuickPanelStartView == EncodeView(SelectedDashboard);
        _syncingDefaultFlag = false;
    }

    partial void OnIsDefaultViewChanged(bool value)
    {
        if (_syncingDefaultFlag)
            return;
        _settingsStore.Update(s => s.QuickPanelStartView = value ? EncodeView(SelectedDashboard) : "last");
    }

    partial void OnSortByCategoryChanged(bool value)
    {
        _settingsStore.Update(s => s.QuickPanelSortByCategory = value);
        RebuildGroups();
    }

    /// <summary>Re-derive the named category sections (no-op while in manual order).</summary>
    public void RebuildGroups()
    {
        PinnedGroups.Clear();
        if (!SortByCategory)
            return;

        foreach (var group in Catalog.Pinned
                     .GroupBy(t => t.Domain)
                     .OrderBy(g => EntityCatalogViewModel.DomainRank(g.Key)))
        {
            var section = new EntityGroupViewModel(group.Key, _localization.Group(group.Key), _icons.DomainGlyph(group.Key));
            foreach (var tile in group.OrderBy(t => t.FriendlyName, StringComparer.OrdinalIgnoreCase))
                section.Tiles.Add(tile);
            section.Count = section.Tiles.Count;
            PinnedGroups.Add(section);
        }
    }

    /// <summary>
    /// Select the configured default view (Settings → "Default view when the panel opens").
    /// Called on every panel open; "last" keeps whatever was selected before.
    /// </summary>
    public void ApplyStartView()
    {
        var settings = _settingsStore.Load();
        var view = settings.QuickPanelStartView;
        // "last" restores the persisted selection — it must survive app restarts, not just
        // panel closes, so the target comes from settings rather than in-memory state.
        if (view == "last")
            view = settings.QuickPanelLastView;

        switch (view)
        {
            case "favorites":
                SelectedDashboard = QuickDashboard.Favorites;
                break;
            case "firstdash" when Dashboards.Count > 1: // migrated legacy "open on dashboard"
                SelectedDashboard = Dashboards[1];
                break;
            case not null when view.StartsWith("dash:", StringComparison.Ordinal):
                var path = view["dash:".Length..];
                if (Dashboards.FirstOrDefault(d => !d.IsFavorites && (d.UrlPath ?? "") == path) is { } match)
                    SelectedDashboard = match;
                break;
        }
    }

    /// <summary>
    /// Sync the picker with HA's dashboard list (called on every panel open). The list is only
    /// rebuilt when it actually changed in HA, so the current selection normally survives; if
    /// the selected dashboard was removed, the picker falls back to Favourites.
    /// </summary>
    public async Task EnsureDashboardsAsync()
    {
        if (Shell.Status != HaConnectionStatus.Connected)
            return;
        try
        {
            var list = await _connection.ListDashboardsAsync();
            _ui.Post(() =>
            {
                var fresh = list.Select(d => new QuickDashboard(d.Title, d.UrlPath, false)).ToList();
                var changed = Dashboards.Count != fresh.Count + 1
                              || !Dashboards.Skip(1).SequenceEqual(fresh); // records: value equality
                if (changed)
                {
                    // Clearing the list momentarily nulls the ComboBox selection — that
                    // transient state must not be persisted as the "last view".
                    _rebuildingDashboards = true;
                    try
                    {
                        var previous = SelectedDashboard;
                        while (Dashboards.Count > 1)
                            Dashboards.RemoveAt(Dashboards.Count - 1);
                        foreach (var d in fresh)
                            Dashboards.Add(d);
                        if (previous is { IsFavorites: false })
                            SelectedDashboard = fresh.FirstOrDefault(d => d.UrlPath == previous.UrlPath)
                                                ?? QuickDashboard.Favorites;
                    }
                    finally
                    {
                        _rebuildingDashboards = false;
                    }
                }

                // Re-apply the default view — it may point at a dashboard that only just
                // became known (first open after launch).
                ApplyStartView();
            });
        }
        catch
        {
            // Not connected yet / older HA — Favourites still works; retry on next open.
        }
    }

    partial void OnShowFavoritesChanged(bool value) => OnPropertyChanged(nameof(ShowDashboard));

    partial void OnSelectedDashboardChanged(QuickDashboard value)
    {
        ShowFavorites = value is null || value.IsFavorites;
        OnPropertyChanged(nameof(ShowDashboard));
        if (value is { IsFavorites: false })
            DashboardRequested?.Invoke(this, value);

        // Remember the shown view across app restarts (used by the "last view" start mode).
        if (!_rebuildingDashboards)
        {
            var encoded = EncodeView(value);
            if (_settingsStore.Load().QuickPanelLastView != encoded)
                _settingsStore.Update(s => s.QuickPanelLastView = encoded);
        }
        SyncDefaultFlag(); // the pin reflects whether THIS view is the configured default
    }
}
