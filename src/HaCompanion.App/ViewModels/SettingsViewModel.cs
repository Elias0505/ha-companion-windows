// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Models;
using HaCompanion.App.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>Backing view model for the settings page: connection, hotkey and panel behaviour.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ShellViewModel _shell;
    private readonly IHotkeyService _hotkeys;
    private bool _loading;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _ignoreCertificateErrors;

    [ObservableProperty]
    private string _hotkey = "Win+Ctrl+H";

    [ObservableProperty]
    private bool _autoHideQuickPanel = true;

    [ObservableProperty]
    private string _hotkeyStatus = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> HotkeyPresets { get; } = new()
    {
        "Win+Ctrl+H", "Ctrl+Alt+H", "Ctrl+Shift+H", "Ctrl+Alt+Space", "Win+Ctrl+Space", "Ctrl+Alt+A",
    };

    public SettingsViewModel(ISettingsStore settingsStore, ShellViewModel shell, IHotkeyService hotkeys)
    {
        _settingsStore = settingsStore;
        _shell = shell;
        _hotkeys = hotkeys;
        Load();
    }

    private void Load()
    {
        _loading = true;
        var settings = _settingsStore.Load();
        BaseUrl = settings.BaseUrl;
        Token = settings.Token;
        IgnoreCertificateErrors = settings.IgnoreCertificateErrors;
        Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "Win+Ctrl+H" : settings.Hotkey;
        AutoHideQuickPanel = settings.AutoHideQuickPanel;
        if (!HotkeyPresets.Contains(Hotkey))
            HotkeyPresets.Insert(0, Hotkey);
        _loading = false;
        RefreshHotkeyStatus();
    }

    private AppSettings BuildSettings() => new()
    {
        BaseUrl = BaseUrl.Trim(),
        Token = Token.Trim(),
        IgnoreCertificateErrors = IgnoreCertificateErrors,
        Hotkey = Hotkey,
        AutoHideQuickPanel = AutoHideQuickPanel,
    };

    partial void OnAutoHideQuickPanelChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_loading || string.IsNullOrWhiteSpace(value))
            return;
        _settingsStore.Save(BuildSettings());
        _hotkeys.Register(value);
        RefreshHotkeyStatus();
    }

    private void RefreshHotkeyStatus() =>
        HotkeyStatus = _hotkeys.IsRegistered
            ? $"Active — press {Hotkey} anywhere to open the quick panel."
            : $"'{Hotkey}' could not be registered (reserved or already in use). Pick another combo above, or open the panel from the tray icon.";

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        var settings = BuildSettings();
        if (!settings.HasConnection)
        {
            StatusMessage = "Please enter both a base URL and a long-lived access token.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Connecting…";
        _settingsStore.Save(settings);

        var ok = await _shell.ConnectAsync(settings);
        StatusMessage = ok
            ? "Connected. Your tiles will appear on the dashboard and in the quick panel."
            : "Could not connect — check the URL, token and the certificate option.";
        IsBusy = false;
    }
}
