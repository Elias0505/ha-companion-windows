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
        //    notification rules and the logs. Anything the user dropped in stays.
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

        // 3. The WebView2 profiles hold the Home Assistant session cookies, so a reset has to
        //    take them too - but never from inside the running app: the browser processes keep
        //    them open, and deleting a profile out from under a live WebView2 crashed the
        //    relaunched instance during testing. Hand the job to the next start instead, where
        //    CompletePending() runs before anything opens a WebView.
        if (Folders.Any(f => Directory.Exists(Path.Combine(_dir, f))))
            MarkPending();

        // 4. Drop the cached settings object. Without this a later Save() would write the
        //    stale cache straight back over the file we just deleted.
        _settings.Invalidate();

        return complete;
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
