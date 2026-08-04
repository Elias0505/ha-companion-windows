// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>View model for the full dashboard page (auto-detected tiles + status).</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private bool _isUnconfigured;

    public EntityCatalogViewModel Catalog { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Category filter for the "all devices" list (chips + visible groups).</summary>
    public DeviceBrowserViewModel Browser { get; }

    /// <summary>
    /// True until a Home Assistant connection has been configured — drives the first-run
    /// welcome card. Re-checked when the connection state changes and on every page load
    /// (the page is navigation-cached, so the constructor alone would go stale).
    /// </summary>
    public bool IsUnconfigured
    {
        get => _isUnconfigured;
        private set
        {
            if (SetProperty(ref _isUnconfigured, value))
            {
                OnPropertyChanged(nameof(IsConfigured));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsUnconfigured"/> for bindings that hide the normal
    /// page chrome (edit button, category bar) behind the welcome card.</summary>
    public bool IsConfigured => !IsUnconfigured;

    /// <summary>The "no entities yet" hint — only meaningful once a connection IS configured.</summary>
    public bool ShowEmptyHint => !IsUnconfigured && Catalog.IsEmpty;

    public DashboardViewModel(EntityCatalogViewModel catalog, ShellViewModel shell,
                              DeviceBrowserViewModel browser, ISettingsStore settingsStore)
    {
        Catalog = catalog;
        Shell = shell;
        Browser = browser;
        _settingsStore = settingsStore;
        _isUnconfigured = !settingsStore.Load().HasConnection;

        shell.PropertyChanged += OnShellPropertyChanged;
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EntityCatalogViewModel.IsEmpty))
                OnPropertyChanged(nameof(ShowEmptyHint));
        };
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsConnected))
            RefreshConfigurationState();
    }

    /// <summary>Re-read the stored settings (also called from the page's Loaded handler).</summary>
    public void RefreshConfigurationState() => IsUnconfigured = !_settingsStore.Load().HasConnection;
}
