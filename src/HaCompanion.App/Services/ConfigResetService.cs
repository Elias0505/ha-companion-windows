// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// "Reset to factory settings": wipes every trace of the user's configuration from
/// %LOCALAPPDATA%\HaCompanion plus the autostart entry, so the next start behaves like a
/// first start. The counterpart to <see cref="IConfigBackupService"/> — export a backup
/// first if anything is worth keeping.
/// </summary>
public interface IConfigResetService
{
    /// <summary>
    /// Delete all user data and switch autostart off. The WebView2 profiles are left to the
    /// next start (see <see cref="CompletePending"/>); the return value says whether
    /// everything else could be removed.
    /// </summary>
    bool Reset();

    /// <summary>Finish the parts of a reset only a fresh process can do. Call once at startup.</summary>
    void CompletePending();

    /// <summary>Remove WebView storage left behind by versions with persistent profiles —
    /// it holds the HA token in cleartext. Call once at startup, before any WebView.</summary>
    void PurgeLegacyWebViewProfiles();
}

/// <inheritdoc cref="IConfigResetService"/>
public sealed class ConfigResetService : IConfigResetService
{
    // WebView2 keeps its profile open for as long as the hosting process (and its browser
    // children) live, so a reset triggered from the running app must not touch these at all.
    // This marker hands them to the next start, which deletes them before anything opens them.
    private const string PendingMarker = "reset.pending";

    private static readonly string[] Folders = { "WebView2", "WebView2Panel" };

    private readonly ISettingsStore _settings;
    private readonly IStartupService _startup;
    private readonly ILogger<ConfigResetService> _logger;
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaCompanion");

    public ConfigResetService(ISettingsStore settings, IStartupService startup,
                              ILogger<ConfigResetService> logger)
    {
        _settings = settings;
        _startup = startup;
        _logger = logger;
    }

    public bool Reset()
    {
        var complete = true;

        // 1. Autostart lives in the registry, never locked — and "off" is the factory default.
        try
        {
            _startup.SetEnabled(false);
        }
        catch (Exception ex)
        {
            complete = false;
            _logger.LogWarning(ex, "Reset: could not clear the autostart entry");
        }

        // 2. Every file we own: settings (token!), layout, shortcuts, automations, stats,
        //    notification rules and the logs. Anything the user dropped in stays. The sweep runs
        //    under the store's lock (ReplaceOnDisk): a background Update() landing between the
        //    delete and the cache drop would otherwise re-create settings.json with the token
        //    the reset just removed.
        _settings.ReplaceOnDisk(() =>
        {
            foreach (var file in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>())
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    _logger.LogWarning(ex, "Reset: {File} is in use", Path.GetFileName(file));
                }
            }
            // Give up any preserved (undecryptable) secret blob too — a factory reset must not
            // leave one behind for a later save to resurrect.
            _settings.DiscardPreservedSecrets();
        });

        // 3. The WebView2 profiles hold the Home Assistant session cookies, so a reset has to
        //    take them too - but never from inside the running app: the browser processes keep
        //    them open, and deleting a profile out from under a live WebView2 crashed the
        //    relaunched instance during testing. Hand the job to the next start instead, where
        //    CompletePending() runs before anything opens a WebView.
        if (Folders.Any(f => Directory.Exists(Path.Combine(_dir, f))))
            MarkPending();

        return complete;
    }

    /// <summary>
    /// One-time cleanup for installations upgraded from a version whose WebView2 profiles
    /// were PERSISTENT: those wrote the seeded hassTokens localStorage entry to disk in
    /// cleartext. The profiles are in-private now, so nothing of value lives there — but the
    /// old files (and the old token inside them) would linger forever. Called at startup,
    /// before any WebView exists, so nothing holds the folders open.
    /// </summary>
    // Written once the legacy (persistent, token-bearing) profiles have been swept. Without it
    // the sweep ran on EVERY start: in-private WebView2 still materializes an EBWebView folder,
    // so each launch deleted the freshly built code cache and paid for a cold first load.
    // Versioned: a future build that must re-sweep (e.g. after a downgrade re-created a
    // persistent profile) bumps the suffix instead of inventing a second mechanism.
    private const string PurgedMarker = "webview-profiles.purged.v1";

    public void PurgeLegacyWebViewProfiles()
    {
        var marker = Path.Combine(_dir, PurgedMarker);
        if (File.Exists(marker))
            return;

        foreach (var folder in Folders)
        {
            // The WHOLE profile, not just localStorage: the HTTP cache holds responses and
            // signed URLs that carry the token too. In-private profiles keep nothing worth
            // preserving, so this is a clean sweep — and after the first run there is
            // almost nothing left to delete.
            var profile = Path.Combine(_dir, folder, "EBWebView");
            if (!Directory.Exists(profile))
                continue;
            try
            {
                Directory.Delete(profile, recursive: true);
                _logger.LogInformation("Removed legacy WebView profile in {Folder} (held the token in cleartext)", folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a shutting-down browser process — retried on the next start
                // (the marker below is only written once every folder is gone).
                _logger.LogWarning(ex, "Could not remove legacy WebView profile in {Folder}", folder);
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(marker, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: without the marker the (now cheap) sweep just runs again next start.
            _logger.LogDebug(ex, "Could not write the WebView purge marker");
        }
    }

    public void CompletePending()
    {
        var marker = Path.Combine(_dir, PendingMarker);
        if (!File.Exists(marker))
            return;
        _logger.LogInformation("Finishing the pending factory reset");
        foreach (var folder in Folders)
        {
            var path = Path.Combine(_dir, folder);
            // After a reset-relaunch the previous instance's browser processes may still be
            // shutting down and holding the profile. A few short retries cover that; anything
            // longer would stall the start, and the marker survives for the next one anyway.
            for (var attempt = 0; Directory.Exists(path); attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 9)
                    {
                        _logger.LogWarning(ex, "Reset: {Folder} is still locked", folder);
                        return; // keep the marker, try again next time
                    }
                    Thread.Sleep(250);
                }
            }
        }
        try
        {
            File.Delete(marker);
        }
        catch (IOException)
        {
            // harmless: the next start tries again
        }
    }

    private void MarkPending()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, PendingMarker), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Reset: could not write the pending marker");
        }
    }
}
