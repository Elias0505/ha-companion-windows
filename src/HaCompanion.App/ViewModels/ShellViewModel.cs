// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>App-wide connection state + orchestration (auto-connect, status text).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IHaConnection _connection;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _ui;

    [ObservableProperty]
    private HaConnectionStatus _status = HaConnectionStatus.Disconnected;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private bool _isConnected;

    public ShellViewModel(IHaConnection connection, ISettingsStore settingsStore, IUiDispatcher ui)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _ui = ui;
        _connection.StatusChanged += (_, status) => _ui.Post(() => ApplyStatus(status));
    }

    /// <summary>Auto-connect at startup if we already have stored settings.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settingsStore.Load();
        if (settings.HasConnection)
            await ConnectAsync(settings);
    }

    public async Task<bool> ConnectAsync(AppSettings settings)
    {
        try
        {
            return await _connection.ConnectAsync(settings.ToConnectionSettings());
        }
        catch
        {
            return false;
        }
    }

    private void ApplyStatus(HaConnectionStatus status)
    {
        Status = status;
        IsConnected = status == HaConnectionStatus.Connected;
        StatusText = status switch
        {
            HaConnectionStatus.Disconnected => "Disconnected",
            HaConnectionStatus.Connecting => "Connecting…",
            HaConnectionStatus.Authenticating => "Authenticating…",
            HaConnectionStatus.AuthFailed => "Authentication failed",
            HaConnectionStatus.Connected => "Connected",
            HaConnectionStatus.Reconnecting => "Reconnecting…",
            _ => status.ToString(),
        };
    }
}
