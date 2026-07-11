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
                QuickPanelStartOnDashboard = persisted.QuickPanelStartOnDashboard,
                QuickPanelDragResize = persisted.QuickPanelDragResize,
                Token = Unprotect(persisted.TokenProtected),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings; using defaults");
            return new AppSettings();
        }
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
            QuickPanelStartOnDashboard = settings.QuickPanelStartOnDashboard,
            QuickPanelDragResize = settings.QuickPanelDragResize,
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
        QuickPanelStartOnDashboard = s.QuickPanelStartOnDashboard,
        QuickPanelDragResize = s.QuickPanelDragResize,
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
        public bool QuickPanelStartOnDashboard { get; set; }
        public bool QuickPanelDragResize { get; set; } = true;
        public string TokenProtected { get; set; } = string.Empty;
    }
}
