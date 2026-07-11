// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
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

    public QuickPanelViewModel(EntityCatalogViewModel catalog, ShellViewModel shell, IHaConnection connection, IUiDispatcher ui)
    {
        Catalog = catalog;
        Shell = shell;
        _connection = connection;
        _ui = ui;
    }

    /// <summary>Load the HA dashboards into the picker (once connected). Safe to call repeatedly.</summary>
    public async Task EnsureDashboardsAsync()
    {
        if (Dashboards.Count > 1 || Shell.Status != HaConnectionStatus.Connected)
            return;
        try
        {
            var list = await _connection.ListDashboardsAsync();
            _ui.Post(() =>
            {
                foreach (var d in list)
                    Dashboards.Add(new QuickDashboard(d.Title, d.UrlPath, false));
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
