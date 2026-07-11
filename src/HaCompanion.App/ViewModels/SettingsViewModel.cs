// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCompanion.App.Models;
using HaCompanion.App.Services;

namespace HaCompanion.App.ViewModels;

/// <summary>Backing view model for the settings page: connection details + connect action.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _ignoreCertificateErrors;

    [ObservableProperty]
    private string _hotkey = "Win+Ctrl+H";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public SettingsViewModel(ISettingsStore settingsStore, ShellViewModel shell)
    {
        _settingsStore = settingsStore;
        _shell = shell;
        Load();
    }

    private void Load()
    {
        var settings = _settingsStore.Load();
        BaseUrl = settings.BaseUrl;
        Token = settings.Token;
        IgnoreCertificateErrors = settings.IgnoreCertificateErrors;
        Hotkey = settings.Hotkey;
    }

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        var settings = new AppSettings
        {
            BaseUrl = BaseUrl.Trim(),
            Token = Token.Trim(),
            IgnoreCertificateErrors = IgnoreCertificateErrors,
            Hotkey = Hotkey,
        };

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
