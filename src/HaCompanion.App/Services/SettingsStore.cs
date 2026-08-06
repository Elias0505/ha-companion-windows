// SPDX-License-Identifier: AGPL-3.0-only
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
        lock (_sync)
        {
            if (_cache is not null)
                return Clone(_cache);
        }

        var loaded = LoadFromDisk();
        lock (_sync)
            _cache ??= Clone(loaded);
        return loaded;
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_file))
                return new AppSettings();

            var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file)) ?? new Persisted();
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
                ShowHaNotifications = persisted.ShowHaNotifications,
                ReportSensors = persisted.ReportSensors,
                MobileAppDeviceId = persisted.MobileAppDeviceId,
                MobileAppWebhookId = Unprotect(persisted.WebhookIdProtected),
                IdleSensorThresholdMinutes =
                    persisted.IdleSensorThresholdMinutes is >= 1 and <= 720 ? persisted.IdleSensorThresholdMinutes : 5,
                AllowCmdLock = persisted.AllowCmdLock,
                AllowCmdMonitorOff = persisted.AllowCmdMonitorOff,
                AllowCmdVolume = persisted.AllowCmdVolume,
                AllowCmdSleep = persisted.AllowCmdSleep,
                AllowCmdShutdown = persisted.AllowCmdShutdown,
                AllowCmdLaunch = persisted.AllowCmdLaunch,
                LaunchWhitelist = persisted.LaunchWhitelist ?? new List<string>(),
                Token = Unprotect(persisted.TokenProtected),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings; using defaults");
            return new AppSettings();
        }
    }

    public void Invalidate()
    {
        lock (_sync)
            _cache = null;
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
            _cache = Clone(settings);

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
            ShowHaNotifications = settings.ShowHaNotifications,
            ReportSensors = settings.ReportSensors,
            MobileAppDeviceId = settings.MobileAppDeviceId,
            WebhookIdProtected = Protect(settings.MobileAppWebhookId),
            IdleSensorThresholdMinutes = settings.IdleSensorThresholdMinutes,
            AllowCmdLock = settings.AllowCmdLock,
            AllowCmdMonitorOff = settings.AllowCmdMonitorOff,
            AllowCmdVolume = settings.AllowCmdVolume,
            AllowCmdSleep = settings.AllowCmdSleep,
            AllowCmdShutdown = settings.AllowCmdShutdown,
            AllowCmdLaunch = settings.AllowCmdLaunch,
            LaunchWhitelist = settings.LaunchWhitelist.ToList(),
            TokenProtected = Protect(settings.Token),
        };

        // Write-to-temp + move so a crash mid-write can never leave a truncated
        // settings.json behind (which would silently drop the stored URL + token).
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(persisted, JsonOptions));
        File.Move(tmp, _file, overwrite: true);
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
        ShowHaNotifications = s.ShowHaNotifications,
        ReportSensors = s.ReportSensors,
        MobileAppDeviceId = s.MobileAppDeviceId,
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

    private string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt stored token; clearing it");
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
        public bool ShowHaNotifications { get; set; } = true;
        public bool ReportSensors { get; set; }
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
