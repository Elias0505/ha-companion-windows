// SPDX-License-Identifier: AGPL-3.0-only
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
        if (_initialized)
            return;
        _initialized = true;

        var settings = _settingsStore.Load();
        if (!settings.HasConnection)
        {
            ShowInfo("Configure your Home Assistant URL and token in Settings first.", InfoBarSeverity.Warning);
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
            ShowInfo("The WebView2 runtime is missing. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen this page.",
                InfoBarSeverity.Error);
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
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildAuthScript(_baseUrl, settings.Token));
            // Note: HA's own sidebar stays intact (the ☰ button must keep working);
            // switch dashboards via the native picker above or HA's sidebar.
        }
        catch (Exception ex)
        {
            ShowInfo($"Could not start the embedded view: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

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
            ShowInfo("Could not list dashboards (not connected?) — showing the default dashboard.", InfoBarSeverity.Informational);
        }

        DashboardCombo.ItemsSource = dashboards;
        DashboardCombo.SelectedIndex = 0; // triggers navigation via SelectionChanged
    }

    private void DashboardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DashboardCombo.SelectedItem is HaDashboardInfo dashboard && Web.CoreWebView2 is not null)
            Web.CoreWebView2.Navigate($"{_baseUrl}/{dashboard.NavigationPath}");
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        Info.Message = message;
        Info.Severity = severity;
        Info.IsOpen = true;
    }

    private static string BuildAuthScript(string baseUrl, string token)
    {
        // Serialize twice: once to build the AuthData JSON, once to turn it into a safe JS string literal.
        var authJson = JsonSerializer.Serialize(new
        {
            hassUrl = baseUrl,
            clientId = (string?)null,
            expires = 9999999999999,
            refresh_token = "",
            access_token = token,
            expires_in = 315360000,
        });
        return $"window.localStorage.setItem('hassTokens', {JsonSerializer.Serialize(authJson)});";
    }
}
