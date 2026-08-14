// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaCompanion.App.Models;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <inheritdoc cref="ISettingsStore"/>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SettingsStore> _logger;
    private readonly string _dir;
    private readonly string _file;
    private readonly object _sync = new();
    private AppSettings? _cache;

    // Set by LoadFromDisk (always under _sync). If the stored secret could not be DECRYPTED on
    // this machine (roaming profile / restored install), the plaintext loads as "" — but the
    // on-disk blob may still be valid elsewhere, so a later Save must NOT overwrite it with an
    // empty string. We remember the raw blob and whether decryption failed to preserve it.
    private bool _tokenDecryptFailed;
    private bool _webhookDecryptFailed;
    private string _diskTokenProtected = string.Empty;
    private string _diskWebhookProtected = string.Empty;
    // A transient read error (AV / backup lock) must not be mistaken for corruption; skip
    // caching so the next Load re-reads the intact file.
    private bool _diskReadFailed;
    // A deliberate secret removal has been requested but not yet written to disk.
    private bool _discardPending;

    public SettingsStore(ILogger<SettingsStore> logger)
    {
        _logger = logger;
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion");
        _file = Path.Combine(_dir, "settings.json");
    }

    public AppSettings Load()
    {
        // Load() runs on hot paths (every panel open / focus change) — serve from the
        // in-memory copy instead of re-reading + DPAPI-decrypting the file each time.
        // Callers get a private clone so mutating it without Save() can't corrupt the cache.
        // All file/DPAPI work happens under _sync so it can never interleave with a Save/Update
        // write (which also holds _sync) — the same lock now guards both read and write.
        lock (_sync)
        {
            if (_cache is not null)
                return Clone(_cache);

            var loaded = LoadFromDisk();
            if (!_diskReadFailed)
                _cache = Clone(loaded); // a transient read failure stays uncached so the next Load retries
            return loaded;
        }
    }

    /// <remarks>Caller must hold <see cref="_sync"/>.</remarks>
    private AppSettings LoadFromDisk()
    {
        _diskReadFailed = false;
        _tokenDecryptFailed = false;
        _webhookDecryptFailed = false;
        _diskTokenProtected = string.Empty;
        _diskWebhookProtected = string.Empty;

        if (!File.Exists(_file))
            return new AppSettings();

        string raw;
        try
        {
            raw = File.ReadAllText(_file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient: an AV/backup/OneDrive lock, NOT corruption. Leave the file intact and
            // run on defaults for now — moving it to .bad here would destroy a perfectly good
            // file, and a second such hit would then overwrite the preserved copy too (M7).
            _logger.LogWarning(ex, "settings.json is temporarily unreadable; using defaults for now");
            _diskReadFailed = true;
            return new AppSettings();
        }

        Persisted persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<Persisted>(raw) ?? new Persisted();
        }
        catch (Exception ex)
        {
            // Genuinely corrupt JSON: move it aside (timestamped, never clobbering an earlier
            // .bad) so the DPAPI-protected token survives and the failure is visible. If the
            // move itself fails (file locked), block writes like a transient read failure —
            // otherwise the next save would overwrite the original with defaults and the token
            // would be gone without any preserved copy.
            _logger.LogError(ex, "settings.json is corrupt; moving it aside and starting from defaults");
            if (!PreserveCorruptFile())
                _diskReadFailed = true;
            return new AppSettings();
        }

        var token = Unprotect(persisted.TokenProtected, out var tokenOk);
        var webhook = Unprotect(persisted.WebhookIdProtected, out var webhookOk);
        if (_discardPending)
        {
            // A removal is in flight and has not reached the file yet — do not re-arm the blobs
            // it is meant to delete, or the pending write would put them straight back.
            ClearPreservedSecrets();
        }
        else
        {
            _diskTokenProtected = persisted.TokenProtected;
            _diskWebhookProtected = persisted.WebhookIdProtected;
            _tokenDecryptFailed = !tokenOk;
            _webhookDecryptFailed = !webhookOk;
        }

        return new AppSettings
        {
            BaseUrl = persisted.BaseUrl,
            IgnoreCertificateErrors = persisted.IgnoreCertificateErrors,
            Hotkey = string.IsNullOrWhiteSpace(persisted.Hotkey) ? "Win+Ctrl+H" : persisted.Hotkey,
            AutoHideQuickPanel = persisted.AutoHideQuickPanel,
            QuickPanelWidth = persisted.QuickPanelWidth is >= 320 and <= 900 ? persisted.QuickPanelWidth : 400,
            Language = string.IsNullOrWhiteSpace(persisted.Language) ? "en" : persisted.Language,
            // Migrate the legacy bool: true used to mean "open on the first dashboard".
            QuickPanelStartView = !string.IsNullOrEmpty(persisted.QuickPanelStartView)
                ? persisted.QuickPanelStartView
                : persisted.QuickPanelStartOnDashboard ? "firstdash" : "last",
            QuickPanelLastView = string.IsNullOrEmpty(persisted.QuickPanelLastView) ? "favorites" : persisted.QuickPanelLastView,
            QuickPanelDragResize = persisted.QuickPanelDragResize,
            QuickPanelSortByCategory = persisted.QuickPanelSortByCategory,
            QuickPanelMonitor = string.IsNullOrWhiteSpace(persisted.QuickPanelMonitor) ? "primary" : persisted.QuickPanelMonitor,
            ShowHaNotifications = persisted.ShowHaNotifications,
            ToastAppName = persisted.ToastAppName,
            ReportSensors = persisted.ReportSensors,
            HaDeviceName = persisted.HaDeviceName,
            ReportTrackerHome = persisted.ReportTrackerHome,
            MobileAppDeviceId = persisted.MobileAppDeviceId,
            MobileAppRegisteredName = persisted.MobileAppRegisteredName,
            MobileAppWebhookId = webhook,
            IdleSensorThresholdMinutes =
                persisted.IdleSensorThresholdMinutes is >= 1 and <= 720 ? persisted.IdleSensorThresholdMinutes : 5,
            AllowCmdLock = persisted.AllowCmdLock,
            AllowCmdMonitorOff = persisted.AllowCmdMonitorOff,
            AllowCmdVolume = persisted.AllowCmdVolume,
            AllowCmdSleep = persisted.AllowCmdSleep,
            AllowCmdShutdown = persisted.AllowCmdShutdown,
            AllowCmdLaunch = persisted.AllowCmdLaunch,
            LaunchWhitelist = persisted.LaunchWhitelist ?? new List<string>(),
            Token = token,
        };
    }

    private bool PreserveCorruptFile()
    {
        try
        {
            var bad = _file + ".bad." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_file, bad, overwrite: false);
            _logger.LogWarning("Corrupt settings kept at {Path}", bad);
            return true;
        }
        catch (Exception moveEx)
        {
            _logger.LogWarning(moveEx, "Could not preserve the corrupt settings file");
            return false;
        }
    }

    public void DiscardPreservedSecrets()
    {
        // A deliberate removal must win over "keep the blob we could not decrypt": otherwise the
        // credential drop on an origin change would write an empty token and then quietly put the
        // old encrypted secret back, leaving on disk exactly what the user asked to remove.
        // The intent is STICKY until a write happens: the caller's next Update may re-read the
        // file (empty cache), which would otherwise re-arm the very blob being discarded.
        lock (_sync)
        {
            _discardPending = true;
            ClearPreservedSecrets();
        }
    }

    public void ReplaceOnDisk(Action mutateFile)
    {
        ArgumentNullException.ThrowIfNull(mutateFile);
        // Import and factory reset manipulate settings.json at the FILE level. Running them under
        // the store's lock closes the race where a background Update() lands between their write
        // (or delete) and the cache drop, persisting the pre-import/pre-reset snapshot — which
        // for the import meant re-pairing the previous host's token with the new URL.
        lock (_sync)
        {
            try
            {
                mutateFile();
            }
            finally
            {
                _cache = null;          // next Load re-reads whatever mutateFile left behind
                _discardPending = false; // ...and re-derives blob state from that file, so a
                                         // discard requested inside the callback is either done
                                         // (new file has no blob) or moot (write failed).
            }
        }
    }

    /// <remarks>Caller must hold <see cref="_sync"/>.</remarks>
    private void ClearPreservedSecrets()
    {
        _tokenDecryptFailed = false;
        _webhookDecryptFailed = false;
        _diskTokenProtected = string.Empty;
        _diskWebhookProtected = string.Empty;
    }

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        // Atomic read-modify-write: a background component that only owns one field (webhook id,
        // device id) mutates the CURRENT settings under the lock instead of writing back a whole
        // snapshot it captured earlier — so it can never clobber a concurrent change to another
        // field. The mutate callback must not call back into the store.
        lock (_sync)
        {
            AppSettings current;
            if (_cache is not null)
            {
                current = Clone(_cache);
            }
            else
            {
                current = LoadFromDisk();
                if (_diskReadFailed)
                {
                    // We are holding DEFAULTS because the file was momentarily unreadable, not
                    // because it is empty. Writing them back would replace a perfectly good
                    // settings.json — URL, token, permissions, whitelist — with nothing.
                    // The whole operation failed, so a pending secret-discard failed WITH it:
                    // drop the intent, or it would stay armed for the rest of the session and
                    // make a later unrelated write blank a blob nobody asked to remove.
                    _discardPending = false;
                    _logger.LogWarning("Skipping a settings write: settings.json could not be read just now");
                    return;
                }
                _cache = Clone(current);
            }
            mutate(current);
            // Persist FIRST, cache second: if the write throws (disk full, ACL), a cache
            // updated up front would keep handing out state the disk does not hold.
            Persist(current);
            _cache = Clone(current);
        }
    }

    /// <remarks>Caller must hold <see cref="_sync"/>.</remarks>
    private void Persist(AppSettings settings)
    {
        Directory.CreateDirectory(_dir);
        var persisted = new Persisted
        {
            BaseUrl = settings.BaseUrl,
            IgnoreCertificateErrors = settings.IgnoreCertificateErrors,
            Hotkey = settings.Hotkey,
            AutoHideQuickPanel = settings.AutoHideQuickPanel,
            QuickPanelWidth = settings.QuickPanelWidth,
            Language = settings.Language,
            QuickPanelStartView = settings.QuickPanelStartView,
            QuickPanelLastView = settings.QuickPanelLastView,
            QuickPanelDragResize = settings.QuickPanelDragResize,
            QuickPanelSortByCategory = settings.QuickPanelSortByCategory,
            QuickPanelMonitor = settings.QuickPanelMonitor,
            ShowHaNotifications = settings.ShowHaNotifications,
            ToastAppName = settings.ToastAppName,
            ReportSensors = settings.ReportSensors,
            HaDeviceName = settings.HaDeviceName,
            ReportTrackerHome = settings.ReportTrackerHome,
            MobileAppDeviceId = settings.MobileAppDeviceId,
            MobileAppRegisteredName = settings.MobileAppRegisteredName,
            WebhookIdProtected = ProtectOrKeep(settings.MobileAppWebhookId, _diskWebhookProtected, _webhookDecryptFailed),
            IdleSensorThresholdMinutes = settings.IdleSensorThresholdMinutes,
            AllowCmdLock = settings.AllowCmdLock,
            AllowCmdMonitorOff = settings.AllowCmdMonitorOff,
            AllowCmdVolume = settings.AllowCmdVolume,
            AllowCmdSleep = settings.AllowCmdSleep,
            AllowCmdShutdown = settings.AllowCmdShutdown,
            AllowCmdLaunch = settings.AllowCmdLaunch,
            LaunchWhitelist = settings.LaunchWhitelist.ToList(),
            TokenProtected = ProtectOrKeep(settings.Token, _diskTokenProtected, _tokenDecryptFailed),
        };

        // Write-to-temp + move so a crash mid-write can never leave a truncated settings.json
        // behind. Unique temp name so two savers never write the same .tmp (the move is atomic).
        // Sweep temp files a crashed previous run left behind — they contain the encrypted
        // token and would otherwise accumulate forever. (Single-instance app, and we hold the
        // store lock, so nothing else is writing one right now.)
        try
        {
            foreach (var stale in Directory.GetFiles(_dir, "settings.json.tmp.*"))
                File.Delete(stale);
            File.Delete(_file + ".tmp"); // pre-1.6.1 builds used this exact fixed name
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a locked leftover is retried on the next write.
        }
        var tmp = _file + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(persisted, JsonOptions));
            File.Move(tmp, _file, overwrite: true);

            // What is on disk is now exactly what we just wrote. Track it, or a later
            // "keep the blob we could not decrypt" would hand back a blob that no longer
            // exists — resurrecting a dead webhook id or a replaced token.
            _diskTokenProtected = persisted.TokenProtected;
            _diskWebhookProtected = persisted.WebhookIdProtected;
            if (!string.IsNullOrEmpty(settings.Token))
                _tokenDecryptFailed = false;   // we hold the plaintext; nothing to preserve
            if (!string.IsNullOrEmpty(settings.MobileAppWebhookId))
                _webhookDecryptFailed = false;
            _discardPending = false;           // the removal has reached the file
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            // The removal did NOT reach the file — but drop the intent anyway: the old file is
            // intact, so the next LoadFromDisk re-derives truthful blob state. A sticky flag
            // would instead disarm blob preservation for the rest of the session and let a
            // later unrelated write blank a blob that was never meant to be removed.
            _discardPending = false;
            throw;
        }
    }

    /// <summary>
    /// Encrypt the current plaintext — but never let a hiccup destroy a secret:
    /// <list type="bullet">
    /// <item>plaintext empty only because the on-disk blob could not be DECRYPTED here: keep the
    /// blob, it may still be valid on the machine that wrote it;</item>
    /// <item>ENCRYPTION failed (returns empty for a non-empty secret): keep the blob too, rather
    /// than replacing a working secret with nothing.</item>
    /// </list>
    /// A deliberate removal calls <see cref="DiscardPreservedSecrets"/> first, which clears the
    /// blob so neither branch can put it back.
    /// </summary>
    private string ProtectOrKeep(string plain, string diskProtected, bool decryptFailed)
    {
        if (!string.IsNullOrEmpty(plain))
        {
            var encrypted = Protect(plain);
            if (!string.IsNullOrEmpty(encrypted))
                return encrypted;
            // Encryption failed — do not blank an existing secret. Loud on purpose: on a
            // same-origin token ROTATION this keeps the previous token, so the UI would report
            // success while the next start authenticates with the old one.
            _logger.LogError("Encrypting a secret failed; keeping the previously stored value");
            return diskProtected;
        }
        if (decryptFailed && !string.IsNullOrEmpty(diskProtected))
            return diskProtected;
        return string.Empty;
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        BaseUrl = s.BaseUrl,
        Token = s.Token,
        IgnoreCertificateErrors = s.IgnoreCertificateErrors,
        Hotkey = s.Hotkey,
        AutoHideQuickPanel = s.AutoHideQuickPanel,
        QuickPanelWidth = s.QuickPanelWidth,
        Language = s.Language,
        QuickPanelStartView = s.QuickPanelStartView,
        QuickPanelLastView = s.QuickPanelLastView,
        QuickPanelDragResize = s.QuickPanelDragResize,
        QuickPanelSortByCategory = s.QuickPanelSortByCategory,
        QuickPanelMonitor = s.QuickPanelMonitor,
        ShowHaNotifications = s.ShowHaNotifications,
        ToastAppName = s.ToastAppName,
        ReportSensors = s.ReportSensors,
        HaDeviceName = s.HaDeviceName,
        ReportTrackerHome = s.ReportTrackerHome,
        MobileAppDeviceId = s.MobileAppDeviceId,
        MobileAppRegisteredName = s.MobileAppRegisteredName,
        MobileAppWebhookId = s.MobileAppWebhookId,
        IdleSensorThresholdMinutes = s.IdleSensorThresholdMinutes,
        AllowCmdLock = s.AllowCmdLock,
        AllowCmdMonitorOff = s.AllowCmdMonitorOff,
        AllowCmdVolume = s.AllowCmdVolume,
        AllowCmdSleep = s.AllowCmdSleep,
        AllowCmdShutdown = s.AllowCmdShutdown,
        AllowCmdLaunch = s.AllowCmdLaunch,
        LaunchWhitelist = s.LaunchWhitelist.ToList(),
    };

    private string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt token");
            return string.Empty;
        }
    }

    /// <param name="ok">
    /// True if the value was decrypted (or was legitimately empty). False means the blob exists
    /// but could not be decrypted on this machine — the caller must then preserve it rather than
    /// overwrite it with the empty result.
    /// </param>
    private string Unprotect(string protectedBase64, out bool ok)
    {
        ok = true;
        if (string.IsNullOrEmpty(protectedBase64))
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt a stored secret; keeping the encrypted blob so it is not lost");
            ok = false;
            return string.Empty;
        }
    }

    private sealed class Persisted
    {
        public string BaseUrl { get; set; } = string.Empty;
        public bool IgnoreCertificateErrors { get; set; }
        public string Hotkey { get; set; } = "Win+Ctrl+H";
        public bool AutoHideQuickPanel { get; set; } = true;
        public int QuickPanelWidth { get; set; } = 400;
        public string Language { get; set; } = "en";
        public bool QuickPanelStartOnDashboard { get; set; } // legacy; read for migration only
        public string QuickPanelStartView { get; set; } = string.Empty;
        public string QuickPanelLastView { get; set; } = string.Empty;
        public bool QuickPanelDragResize { get; set; } = true;
        public bool QuickPanelSortByCategory { get; set; }
        public string QuickPanelMonitor { get; set; } = "primary";
        public bool ShowHaNotifications { get; set; } = true;
        public string ToastAppName { get; set; } = string.Empty;
        public bool ReportSensors { get; set; }
        public string HaDeviceName { get; set; } = string.Empty;
        public bool ReportTrackerHome { get; set; }
        public string MobileAppRegisteredName { get; set; } = string.Empty;
        public string MobileAppDeviceId { get; set; } = string.Empty;
        public string WebhookIdProtected { get; set; } = string.Empty;
        public int IdleSensorThresholdMinutes { get; set; } = 5;
        // Off-by-default like AppSettings: Save() always writes these keys explicitly,
        // so existing settings files keep their values — only fresh installs change.
        public bool AllowCmdLock { get; set; }
        public bool AllowCmdMonitorOff { get; set; }
        public bool AllowCmdVolume { get; set; }
        public bool AllowCmdSleep { get; set; }
        public bool AllowCmdShutdown { get; set; }
        public bool AllowCmdLaunch { get; set; }
        public List<string>? LaunchWhitelist { get; set; }
        public string TokenProtected { get; set; } = string.Empty;
    }
}
