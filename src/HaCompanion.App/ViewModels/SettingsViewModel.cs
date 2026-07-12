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
    private readonly LocalizationService _localization;
    private readonly IQuickPanelController _quickPanel;
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
    private double _quickPanelWidth = 400;

    [ObservableProperty]
    private bool _quickPanelStartOnDashboard;

    [ObservableProperty]
    private bool _quickPanelDragResize = true;

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private string _hotkeyStatus = string.Empty;

    public IReadOnlyList<LanguageOption> Languages => _localization.Languages;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> HotkeyPresets { get; } = new()
    {
        "Win+Ctrl+H", "Ctrl+Alt+H", "Ctrl+Shift+H", "Ctrl+Alt+Space", "Win+Ctrl+Space", "Ctrl+Alt+A",
    };

    public SettingsViewModel(ISettingsStore settingsStore, ShellViewModel shell, IHotkeyService hotkeys, LocalizationService localization, IQuickPanelController quickPanel)
    {
        _settingsStore = settingsStore;
        _shell = shell;
        _hotkeys = hotkeys;
        _localization = localization;
        _quickPanel = quickPanel;
        Load();
        // Keep the (localized) hotkey status in sync when the UI language changes.
        _localization.LanguageChanged += (_, _) => RefreshHotkeyStatus();
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
        QuickPanelWidth = settings.QuickPanelWidth;
        QuickPanelStartOnDashboard = settings.QuickPanelStartOnDashboard;
        QuickPanelDragResize = settings.QuickPanelDragResize;
        SelectedLanguage = _localization.Languages.FirstOrDefault(l => l.Code == settings.Language)
                           ?? _localization.Languages[0];
        if (!HotkeyPresets.Contains(Hotkey))
            HotkeyPresets.Insert(0, Hotkey);
        _loading = false;
        RefreshHotkeyStatus();
    }

    private AppSettings BuildSettings()
    {
        // Start from the stored settings and overwrite only what this page owns — fields
        // managed elsewhere (e.g. the panel's sort toggle) must survive a settings save.
        var settings = _settingsStore.Load();
        settings.BaseUrl = BaseUrl.Trim();
        settings.Token = Token.Trim();
        settings.IgnoreCertificateErrors = IgnoreCertificateErrors;
        settings.Hotkey = Hotkey;
        settings.AutoHideQuickPanel = AutoHideQuickPanel;
        settings.QuickPanelWidth = (int)QuickPanelWidth;
        settings.Language = SelectedLanguage?.Code ?? "en";
        settings.QuickPanelStartOnDashboard = QuickPanelStartOnDashboard;
        settings.QuickPanelDragResize = QuickPanelDragResize;
        return settings;
    }

    partial void OnAutoHideQuickPanelChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnQuickPanelWidthChanged(double value)
    {
        if (_loading)
            return;
        _settingsStore.Save(BuildSettings());
        _quickPanel.PreviewWidth(); // show the panel live so the user sees the size
    }

    partial void OnQuickPanelStartOnDashboardChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnQuickPanelDragResizeChanged(bool value)
    {
        if (!_loading)
            _settingsStore.Save(BuildSettings());
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null)
            return;
        _localization.SetLanguage(value.Code);
        _settingsStore.Save(BuildSettings());
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_loading || string.IsNullOrWhiteSpace(value))
            return;
        if (!HotkeyPresets.Contains(value))
            HotkeyPresets.Insert(0, value); // keep a captured custom combo visible in the dropdown
        _settingsStore.Save(BuildSettings());
        _hotkeys.Register(value);
        RefreshHotkeyStatus();
    }

    /// <summary>Localized prompt shown while the user is recording a custom hotkey.</summary>
    public string RecordPrompt => _localization["Set_RecordPrompt"];

    /// <summary>Re-derives the hotkey status text (used to cancel an in-progress capture).</summary>
    public void RefreshHotkeyStatusPublic() => RefreshHotkeyStatus();

    private void RefreshHotkeyStatus() =>
        HotkeyStatus = string.Format(
            _localization[_hotkeys.IsRegistered ? "Set_HotkeyActive" : "Set_HotkeyFailed"], Hotkey);

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        var settings = BuildSettings();
        if (!settings.HasConnection)
        {
            StatusMessage = _localization["Set_MsgNeedBoth"];
            return;
        }

        IsBusy = true;
        StatusMessage = _localization["St_Connecting"];
        _settingsStore.Save(settings);

        var ok = await _shell.ConnectAsync(settings);
        StatusMessage = _localization[ok ? "Set_MsgConnected" : "Set_MsgFailed"];
        IsBusy = false;
    }
}
