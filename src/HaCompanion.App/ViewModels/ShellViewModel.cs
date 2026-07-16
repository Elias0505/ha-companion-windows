// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Rest;
using HaCompanion.Core.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>App-wide connection state + orchestration (auto-connect, status text).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IHaConnection _connection;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDispatcher _ui;
    private readonly LocalizationService _localization;

    [ObservableProperty]
    private HaConnectionStatus _status = HaConnectionStatus.Disconnected;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isConnected;

    public ShellViewModel(IHaConnection connection, ISettingsStore settingsStore, IUiDispatcher ui, LocalizationService localization)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _ui = ui;
        _localization = localization;
        _statusText = localization["St_Disconnected"];
        _connection.StatusChanged += (_, status) => _ui.Post(() => ApplyStatus(status));
        // Re-derive the status text when the UI language changes.
        _localization.LanguageChanged += (_, _) => _ui.Post(() => ApplyStatus(Status));
    }

    /// <summary>Auto-connect at startup if we already have stored settings.</summary>
    public async Task InitializeAsync()
    {
        var settings = _settingsStore.Load();
        if (settings.HasConnection)
            await ConnectAsync(settings);
    }

    public async Task<ConnectionCheckResult> ConnectAsync(AppSettings settings)
    {
        try
        {
            return await _connection.ConnectAsync(settings.ToConnectionSettings());
        }
        catch
        {
            return new ConnectionCheckResult(ConnectionCheckStatus.NetworkError);
        }
    }

    private void ApplyStatus(HaConnectionStatus status)
    {
        Status = status;
        IsConnected = status == HaConnectionStatus.Connected;
        StatusText = status switch
        {
            HaConnectionStatus.Disconnected => _localization["St_Disconnected"],
            HaConnectionStatus.Connecting => _localization["St_Connecting"],
            HaConnectionStatus.Authenticating => _localization["St_Authenticating"],
            HaConnectionStatus.AuthFailed => _localization["St_AuthFailed"],
            HaConnectionStatus.Connected => _localization["St_Connected"],
            HaConnectionStatus.Reconnecting => _localization["St_Reconnecting"],
            HaConnectionStatus.TlsError => _localization["St_TlsError"],
            _ => status.ToString(),
        };
    }
}
