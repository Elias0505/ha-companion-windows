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
        Directory.CreateDirectory(_dir);
        var persisted = new Persisted
        {
            BaseUrl = settings.BaseUrl,
            IgnoreCertificateErrors = settings.IgnoreCertificateErrors,
            Hotkey = settings.Hotkey,
            AutoHideQuickPanel = settings.AutoHideQuickPanel,
            TokenProtected = Protect(settings.Token),
        };
        File.WriteAllText(_file, JsonSerializer.Serialize(persisted, JsonOptions));
    }

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
        public string TokenProtected { get; set; } = string.Empty;
    }
}
