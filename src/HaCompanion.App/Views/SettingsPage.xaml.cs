// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using HaCompanion.App.Controls;
using HaCompanion.App.Services;
using HaCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.Storage;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

namespace HaCompanion.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _recordingHotkey;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        TokenBox.Password = ViewModel.Token; // one-time init; updates flow via PasswordChanged
        // The page is cached: re-sync the default-view picker on every visit — the quick
        // panel's pin button changes the stored value behind this page's back — and
        // re-enumerate displays (monitors may have been (un)plugged meanwhile).
        Loaded += (_, _) =>
        {
            ViewModel.RefreshStartViewSelection();
            ViewModel.RefreshMonitorOptions();
        };
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Token = TokenBox.Password;

#pragma warning disable CA1822 // x:Bind function — the generated binding code calls it on the instance
    public bool IsNotBusy(bool isBusy) => !isBusy;
#pragma warning restore CA1822

    private bool _twoColumn = true;

    // Reflow the settings cards: two columns side by side when there's room (fullscreen /
    // wide window), a single stacked column otherwise. Driven by the real content width
    // because the XAML VisualStateManager's attached-Grid setters proved unreliable here.
    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 1080;
        if (wide == _twoColumn)
            return;
        _twoColumn = wide;
        if (wide)
        {
            // side by side: each column spans one grid column
            Grid.SetColumnSpan(LeftCol, 1);
            Grid.SetRow(RightCol, 1);
            Grid.SetColumn(RightCol, 1);
            Grid.SetColumnSpan(RightCol, 1);
            Root.MaxWidth = 1040;
            Root.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            // stacked: both columns span the full width (the grid keeps two columns,
            // so without the span each card would sit at half width)
            Grid.SetColumnSpan(LeftCol, 2);
            Grid.SetRow(RightCol, 2);
            Grid.SetColumn(RightCol, 0);
            Grid.SetColumnSpan(RightCol, 2);
            Root.MaxWidth = 640;
            Root.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    // --- Config backup: export/import the whole config as one portable JSON ---

    private static LocalizationService Loc => App.Services.GetRequiredService<LocalizationService>();

    private static IntPtr WindowHandle => WindowNative.GetWindowHandle(App.MainWindow);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "ha-companion-config" };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            InitializeWithWindow.Initialize(picker, WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;
            var json = App.Services.GetRequiredService<IConfigBackupService>().Export();
            await FileIO.WriteTextAsync(file, json);
            BackupStatus.Text = string.Format(CultureInfo.CurrentCulture, Loc["Backup_Exported"], file.Name);
        }
        catch (Exception ex)
        {
            BackupStatus.Text = Loc["Backup_Failed"];
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;
            var json = await FileIO.ReadTextAsync(file);
            var ok = App.Services.GetRequiredService<IConfigBackupService>().Import(json);
            // Re-apply everything that reads from the stores at runtime.
            if (ok)
            {
                App.Services.GetRequiredService<IShortcutManager>().Reload();
                App.Services.GetRequiredService<IRulesEngine>().Reload();
                App.Services.GetRequiredService<INotifyRulesEngine>().Reload();
                // The tile catalog keeps pins/spans in memory — rebuild it from the
                // imported layout.json (we are on the UI thread here).
                App.Services.GetRequiredService<EntityCatalogViewModel>().ReloadLayout();
            }
            BackupStatus.Text = Loc[ok ? "Backup_Imported" : "Backup_Invalid"];
        }
        catch (Exception ex)
        {
            BackupStatus.Text = Loc["Backup_Failed"];
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // --- Diagnostics: redacted report export + open-log-folder ---

    private async void DiagExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = "ha-companion-diagnostics-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
            InitializeWithWindow.Initialize(picker, WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;
            var report = App.Services.GetRequiredService<IDiagnosticsService>().BuildReport();
            await FileIO.WriteTextAsync(file, report);
            DiagStatus.Text = string.Format(CultureInfo.CurrentCulture, Loc["Diag_Saved"], file.Name);
        }
        catch (Exception ex)
        {
            DiagStatus.Text = Loc["Diag_Failed"];
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void DiagOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = App.Services.GetRequiredService<IDiagnosticsService>().LogFolderPath;
            System.IO.Directory.CreateDirectory(folder); // exists before we try to open it
            // Shell-execute the folder itself: robust when the path contains spaces
            // (e.g. C:\Users\First Last\...), unlike passing it as an explorer.exe argument.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DiagStatus.Text = Loc["Diag_Failed"];
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // --- Factory reset: wipe the configuration and start over ---

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            // dialogs open in the popup layer and do not inherit the window's direction
            FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Title = Loc["Reset_ConfirmTitle"],
            Content = Loc["Reset_ConfirmBody"],
            PrimaryButtonText = Loc["Reset_Confirm"],
            CloseButtonText = Loc["Au_Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            App.Services.GetRequiredService<IConfigResetService>().Reset();
            ResetStatus.Text = Loc["Reset_Restarting"];
            // Everything still in memory (view models, connection, hotkeys) belongs to the
            // configuration we just deleted — a fresh process is the honest way back.
            App.Relaunch();
        }
        catch (Exception ex)
        {
            ResetStatus.Text = Loc["Reset_Failed"];
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // --- Custom hotkey capture: let the user press any Ctrl/Alt/Shift(+Win)+key combo ---

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        ViewModel.HotkeyStatus = ViewModel.RecordPrompt;
        RecordHotkeyButton.Focus(FocusState.Programmatic);
    }

    private void RecordHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recordingHotkey)
            return;
        _recordingHotkey = false; // clicked away without pressing a key — cancel
        ViewModel.RefreshHotkeyStatusPublic();
    }

    private void RecordHotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingHotkey)
            return;

        switch (HotkeyCapture.Handle(e, out var combo))
        {
            case HotkeyCapture.Result.Captured:
                _recordingHotkey = false;
                ViewModel.Hotkey = combo;
                break;
            case HotkeyCapture.Result.Cancelled:
                _recordingHotkey = false;
                ViewModel.RefreshHotkeyStatusPublic();
                break;
        }
    }
}
