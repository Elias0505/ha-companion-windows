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

    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Dropdown entries: Favourites + the user's HA dashboards.</summary>
    public ObservableCollection<QuickDashboard> Dashboards { get; } = new() { QuickDashboard.Favorites };

    [ObservableProperty]
    private QuickDashboard _selectedDashboard = QuickDashboard.Favorites;

    [ObservableProperty]
    private bool _showFavorites = true;

    public bool ShowDashboard => !ShowFavorites;

    /// <summary>Raised when a real HA dashboard is chosen; the window navigates the WebView.</summary>
    public event EventHandler<QuickDashboard>? DashboardRequested;

    public QuickPanelViewModel(EntityCatalogViewModel catalog, ShellViewModel shell, IHaConnection connection, IUiDispatcher ui, ISettingsStore settingsStore)
    {
        Catalog = catalog;
        Shell = shell;
        _connection = connection;
        _ui = ui;
        _settingsStore = settingsStore;
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
                var firstLoad = Dashboards.Count <= 1;
                var fresh = list.Select(d => new QuickDashboard(d.Title, d.UrlPath, false)).ToList();
                var changed = Dashboards.Count != fresh.Count + 1
                              || !Dashboards.Skip(1).SequenceEqual(fresh); // records: value equality
                if (changed)
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

                // If configured, open the panel on the first HA dashboard instead of Favourites.
                if (firstLoad && _settingsStore.Load().QuickPanelStartOnDashboard && Dashboards.Count > 1)
                    SelectedDashboard = Dashboards[1];
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
    }
}
