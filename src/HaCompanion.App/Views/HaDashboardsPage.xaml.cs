// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using System.Text.Json;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace HaCompanion.App.Views;

/// <summary>
/// Shows the user's real Home Assistant dashboards 1:1 by embedding the HA web
/// frontend (WebView2), auto-authenticated with the stored long-lived token via
/// a pre-seeded <c>hassTokens</c> localStorage entry. The HA sidebar is hidden
/// best-effort so the app's own navigation stays the primary chrome.
/// </summary>
public sealed partial class HaDashboardsPage : Page
{
    private readonly ISettingsStore _settingsStore;
    private readonly IHaConnection _connection;
    private bool _initialized;
    private string _baseUrl = string.Empty;

    public HaDashboardsPage()
    {
        _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
        _connection = App.Services.GetRequiredService<IHaConnection>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The page is cached (NavigationCacheMode=Required), so Loaded fires on every visit.
        // Mark it initialized only after a successful start — that way "no settings yet" or
        // a missing runtime is retried on the next visit instead of showing a stale warning
        // forever after the user has fixed the cause.
        if (_initialized)
            return;

        var loc = App.Services.GetRequiredService<LocalizationService>();
        var settings = _settingsStore.Load();
        if (!settings.HasConnection)
        {
            ShowInfo(loc["Dash_NeedSettings"], InfoBarSeverity.Warning);
            return;
        }
        _baseUrl = settings.BaseUrl.TrimEnd('/');

        // WebView2 runtime present? (Preinstalled on Win 11, but never guaranteed.)
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            ShowInfo(loc["Dash_NoWebView"], InfoBarSeverity.Error);
            return;
        }

        try
        {
            // Unpackaged apps must use a writable user-data folder.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HaCompanion", "WebView2");
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());
            await Web.EnsureCoreWebView2Async(env);

            if (settings.IgnoreCertificateErrors)
            {
                Web.CoreWebView2.ServerCertificateErrorDetected += (_, args) =>
                    args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            }

            // Pre-seed hassTokens so the HA frontend logs in without any prompt.
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                HaWebViewHelper.BuildAuthScript(_baseUrl, settings.Token));
            // Note: HA's own sidebar stays intact (the ☰ button must keep working);
            // switch dashboards via the native picker above or HA's sidebar.
        }
        catch (Exception ex)
        {
            ShowInfo(string.Format(CultureInfo.CurrentCulture, loc["Dash_EmbedFailed"], ex.Message), InfoBarSeverity.Error);
            return;
        }

        _initialized = true;
        Info.IsOpen = false;
        await LoadDashboardListAsync();
    }

    private async Task LoadDashboardListAsync()
    {
        IReadOnlyList<HaDashboardInfo> dashboards;
        try
        {
            dashboards = await _connection.ListDashboardsAsync();
        }
        catch (Exception)
        {
            dashboards = [new HaDashboardInfo(null, "Overview", null)];
            ShowInfo(App.Services.GetRequiredService<LocalizationService>()["Dash_ListFailed"], InfoBarSeverity.Informational);
        }

        DashboardCombo.ItemsSource = dashboards;
        DashboardCombo.SelectedIndex = 0; // triggers navigation via SelectionChanged
    }

    private void DashboardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DashboardCombo.SelectedItem is HaDashboardInfo dashboard && Web.CoreWebView2 is not null)
        {
            var url = string.IsNullOrEmpty(dashboard.UrlPath) ? _baseUrl : $"{_baseUrl}/{dashboard.UrlPath}";
            Web.CoreWebView2.Navigate(url);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        Info.Message = message;
        Info.Severity = severity;
        Info.IsOpen = true;
    }

}
