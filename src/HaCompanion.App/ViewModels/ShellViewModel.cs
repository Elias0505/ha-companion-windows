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
    private readonly INotificationService _notifications;

    [ObservableProperty]
    private HaConnectionStatus _status = HaConnectionStatus.Disconnected;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isConnected;

    // Repair banner (analog to HA's repair issues): shown only for terminal, user-fixable
    // failures — a revoked token or a TLS certificate problem. Connection drops that heal
    // themselves (reconnect loop) never raise it.
    [ObservableProperty]
    private bool _isRepairVisible;

    [ObservableProperty]
    private string _repairTitle = string.Empty;

    [ObservableProperty]
    private string _repairMessage = string.Empty;

    private bool _repairToastShown;

    public ShellViewModel(IHaConnection connection, ISettingsStore settingsStore, IUiDispatcher ui,
        LocalizationService localization, INotificationService notifications)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _ui = ui;
        _localization = localization;
        _notifications = notifications;
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
            var result = await _connection.ConnectAsync(settings.ToConnectionSettings());
            // Auth/TLS failures at CONNECT time never start the WebSocket, so no status
            // event fires — surface the repair state from the check result instead.
            if (result.Status is ConnectionCheckStatus.AuthFailed or ConnectionCheckStatus.TlsError)
                _ui.Post(() => ApplyStatus(result.Status == ConnectionCheckStatus.AuthFailed
                    ? HaConnectionStatus.AuthFailed
                    : HaConnectionStatus.TlsError));
            return result;
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
        UpdateRepair(status);
    }

    private void UpdateRepair(HaConnectionStatus status)
    {
        var authFailed = status == HaConnectionStatus.AuthFailed;
        if (authFailed || status == HaConnectionStatus.TlsError)
        {
            RepairTitle = _localization[authFailed ? "Repair_AuthTitle" : "Repair_TlsTitle"];
            RepairMessage = _localization[authFailed ? "Repair_AuthText" : "Repair_TlsText"];
            IsRepairVisible = true;
            // One toast per incident — a tray-first app must not fail silently while hidden.
            if (!_repairToastShown)
            {
                _repairToastShown = true;
                _notifications.Show(RepairTitle, RepairMessage);
            }
        }
        else
        {
            IsRepairVisible = false;
            if (status == HaConnectionStatus.Connected)
                _repairToastShown = false; // re-arm for the next incident
        }
    }
}
