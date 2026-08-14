// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using System.Text.Json;
using HaCompanion.App.Services;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;
using HaCompanion.Core.Web;
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
    private int _initGen = -1;   // _resetGen an in-flight init belongs to (-1 = none). A plain bool
                                 // here deadlocked the post-reset init: the STALE init still held it,
                                 // so the fresh control was never initialized until a re-visit.
    private int _resetGen;       // bumped by ResetWebView; an in-flight init with a stale value abandons
    private string _baseUrl = string.Empty;

    // The page is cached (NavigationCacheMode=Required), so one instance outlives every
    // navigation. Settings needs to reach it to rebuild the WebView after a URL/token change.
    private static HaDashboardsPage? _current;

    public HaDashboardsPage()
    {
        _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
        _connection = App.Services.GetRequiredService<IHaConnection>();
        InitializeComponent();
        Loaded += OnLoaded;
        _current = this;
    }

    /// <summary>
    /// Rebuild this page's WebView from current settings (called after the HA URL, token or
    /// certificate option changed). Without it the cached page keeps the OLD origin in its
    /// navigation handlers and the OLD token in its injected script until the app restarts —
    /// and because the hardening validates against the CURRENT origin, every navigation of the
    /// stale page is cancelled, leaving a blank view with no error.
    /// </summary>
    public static void RequestReset()
    {
        var page = _current;
        // Never created yet: the first creation reads current settings anyway.
        page?.DispatcherQueue.TryEnqueue(page.ResetWebView);
    }

    private void ResetWebView()
    {
        // Revoke ANY in-flight init first: it captured the old URL/token and must not finish
        // against the fresh control (an "!_initialized" early-return alone missed exactly the
        // init that was still running and let it complete with the stale credentials).
        _resetGen++;
        _initialized = false;

        SwapWebViewControl();

        DashboardCombo.ItemsSource = null;
        // Re-initialize only while the page is actually in the visual tree — WebView2 init on an
        // unloaded (cached, currently hidden) page never completes; the next Loaded handles it.
        // (A reset that lands while an old init is pending across an unload is also fine: the
        // stale init abandons at its next generation check, and the following Loaded starts
        // fresh.)
        if (IsLoaded)
            _ = EnsureInitializedAsync();
    }

    /// <summary>
    /// Replace the WebView2 control with a virgin one. A CoreWebView2 cannot be re-pointed:
    /// its handlers and document-created scripts (which carry the token) can only be dropped
    /// by disposing the control — and a PARTIALLY initialized control cannot be initialized
    /// again either (EnsureCoreWebView2Async with a new environment throws), so every retry
    /// path needs a fresh control too.
    /// </summary>
    private void SwapWebViewControl()
    {
        var host = (Grid)Content;
        var index = host.Children.IndexOf(Web);
        var stale = Web;
        host.Children.Remove(stale);
        try { stale.Close(); }
        catch (Exception) { /* already torn down */ }

        var fresh = new Microsoft.UI.Xaml.Controls.WebView2 { FlowDirection = FlowDirection.LeftToRight };
        Grid.SetRow(fresh, Grid.GetRow(stale));
        host.Children.Insert(index < 0 ? host.Children.Count : index, fresh);
        Web = fresh;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await EnsureInitializedAsync();

    private async Task EnsureInitializedAsync()
    {
        // The page is cached (NavigationCacheMode=Required), so Loaded fires on every visit.
        // Mark it initialized only after a successful start — that way "no settings yet" or
        // a missing runtime is retried on the next visit instead of showing a stale warning
        // forever after the user has fixed the cause. The in-flight guard is a GENERATION
        // stamp, not a bool: a reset bumps _resetGen, so a stale init still in flight does
        // not block the fresh control's init — while a second Loaded for the SAME generation
        // still returns early instead of double-initializing one control.
        var gen = _resetGen;
        if (_initialized || _initGen == gen)
            return;
        _initGen = gen;
        try
        {
            await InitializeCoreAsync(gen);
        }
        catch (Exception ex)
        {
            // Callers fire-and-forget this method; anything escaping the core's own guards
            // (its prologue, in practice) must not surface as an unobserved-task crash entry.
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            if (_initGen == gen)
                _initGen = -1; // a newer generation owns the field now — leave it alone otherwise
        }
    }

    private async Task InitializeCoreAsync(int gen)
    {
        // Captured once: if a reset swaps the control while an await below is pending, this
        // (stale) init must abandon rather than initialize the fresh control with old settings.
        var web = Web;

        var loc = App.Services.GetRequiredService<LocalizationService>();
        var settings = _settingsStore.Load();
        if (!settings.HasConnection)
        {
            ShowInfo(loc["Dash_NeedSettings"], InfoBarSeverity.Warning);
            return;
        }
        var baseUrl = settings.BaseUrl.TrimEnd('/');

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
            if (gen != _resetGen)
                return; // reset happened mid-init — the fresh control gets its own init
            // InPrivate: the auth script seeds the token into localStorage on every
            // document, and a PERSISTENT profile flushes that localStorage to disk in
            // cleartext — defeating the DPAPI protection of settings.json and outliving
            // token rotation. An in-memory profile keeps auto-login working (the token is
            // re-seeded each time) without ever writing the secret to disk.
            var controllerOptions = env.CreateCoreWebView2ControllerOptions();
            controllerOptions.IsInPrivateModeEnabled = true;
            await web.EnsureCoreWebView2Async(env, controllerOptions);
            if (gen != _resetGen)
                return;

            _baseUrl = baseUrl; // publish only once this init is known to be the current one
            var baseUri = new Uri(baseUrl, UriKind.Absolute);
            // Both callbacks read live settings: turning the certificate exception back off must
            // take effect immediately, not after a restart.
            WebViewHardening.Apply(web.CoreWebView2, CurrentBaseUri,
                () => _settingsStore.Load().IgnoreCertificateErrors);

            // Pre-seed hassTokens so the HA frontend logs in without any prompt.
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                HaWebViewScripts.BuildAuthScript(baseUri, settings.Token));
            // Camera stills must never be requested with box-of-the-moment dimensions —
            // window resizing corrupts their aspect ratio otherwise (see the script's doc).
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                HaWebViewScripts.CameraStillFixScript);
            // Note: HA's own sidebar stays intact (the ☰ button must keep working);
            // switch dashboards via the native picker above or HA's sidebar.
        }
        catch (Exception ex)
        {
            if (gen != _resetGen)
                return; // the control this init was working on is gone — not an error to show
            ShowInfo(string.Format(CultureInfo.CurrentCulture, loc["Dash_EmbedFailed"], ex.Message), InfoBarSeverity.Error);
            // The control may be PARTIALLY initialized (EnsureCoreWebView2Async succeeded, a
            // later step threw) — a retry on it would always fail (a second EnsureCoreWebView2Async
            // with a fresh environment throws) or stack duplicate handlers/scripts. Hand the
            // next visit a virgin control instead; the retry itself stays user-driven (Loaded),
            // so a persistent failure cannot loop.
            _resetGen++;
            SwapWebViewControl();
            return;
        }

        if (gen != _resetGen)
            return; // reset raced the final continuation — the fresh control gets its own init
        _initialized = true;
        Info.IsOpen = false;
        await LoadDashboardListAsync();
    }

    /// <summary>The configured HA origin as of right now (never a captured snapshot) — the
    /// user can change the URL while this cached page keeps its initialized WebView.</summary>
    private Uri CurrentBaseUri()
    {
        var url = _settingsStore.Load().BaseUrl.TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : new Uri(_baseUrl, UriKind.Absolute);
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
