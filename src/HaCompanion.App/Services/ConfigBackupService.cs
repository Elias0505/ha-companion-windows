// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Exports/imports the user's configuration (tile layout, shortcuts, automations,
/// notification rules and non-secret settings) as one portable JSON file — so a
/// reinstall or a new PC doesn't start from scratch. Secrets (the HA token and the
/// mobile_app webhook id) are deliberately excluded.
/// </summary>
public interface IConfigBackupService
{
    /// <summary>Serialize the whole config bundle to a JSON string.</summary>
    string Export();

    /// <summary>Apply a previously exported bundle; returns false on malformed input.</summary>
    bool Import(string json);
}

/// <inheritdoc cref="IConfigBackupService"/>
public sealed class ConfigBackupService : IConfigBackupService
{
    private const int Version = 1;

    private readonly ISettingsStore _settings;
    private readonly ILogger<ConfigBackupService> _logger;
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaCompanion");

    // The plain JSON files that make up the config (all live next to settings.json).
    private static readonly string[] Files =
    {
        "layout.json", "shortcuts.json", "automations.json", "notify_rules.json",
    };

    // Settings keys that are safe to carry across machines (NOT the token/webhook/device id).
    private static readonly string[] PortableSettingKeys =
    {
        "BaseUrl", "IgnoreCertificateErrors", "Hotkey", "AutoHideQuickPanel", "QuickPanelWidth",
        "Language", "QuickPanelStartView", "QuickPanelLastView", "QuickPanelDragResize",
        "QuickPanelSortByCategory", "ShowHaNotifications", "IdleSensorThresholdMinutes",
        "AllowCmdLock", "AllowCmdMonitorOff", "AllowCmdVolume", "AllowCmdSleep",
        "AllowCmdShutdown", "AllowCmdLaunch", "LaunchWhitelist",
    };

    public ConfigBackupService(ISettingsStore settings, ILogger<ConfigBackupService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Export()
    {
        var bundle = new JsonObject
        {
            ["_format"] = "ha-companion-config",
            ["_version"] = Version,
        };

        var files = new JsonObject();
        foreach (var name in Files)
        {
            var path = Path.Combine(_dir, name);
            if (File.Exists(path))
            {
                try { files[name] = JsonNode.Parse(File.ReadAllText(path)); }
                catch (Exception ex) { _logger.LogWarning(ex, "Skipping unreadable {File} on export", name); }
            }
        }
        bundle["files"] = files;

        // settings.json holds the encrypted token — copy only the portable, non-secret keys
        var settingsPath = Path.Combine(_dir, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var full = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
                var portable = new JsonObject();
                if (full is not null)
                    foreach (var key in PortableSettingKeys)
                        if (full.TryGetPropertyValue(key, out var val))
                            portable[key] = val?.DeepClone();
                bundle["settings"] = portable;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read settings for export");
            }
        }

        return bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public bool Import(string json)
    {
        try
        {
            var bundle = JsonNode.Parse(json)?.AsObject();
            if (bundle is null || bundle["_format"]?.GetValue<string>() != "ha-companion-config")
            {
                _logger.LogWarning("Import rejected: not a HA Companion config file");
                return false;
            }

            Directory.CreateDirectory(_dir);

            if (bundle["files"]?.AsObject() is { } files)
            {
                foreach (var (name, node) in files)
                {
                    if (!Files.Contains(name) || node is null)
                        continue; // ignore unknown/foreign file names
                    WriteAtomic(Path.Combine(_dir, name), node.ToJsonString(Indented));
                }
            }

            // Merge portable settings into the existing settings.json (keeps token/webhook).
            if (bundle["settings"]?.AsObject() is { } imported)
            {
                var settingsPath = Path.Combine(_dir, "settings.json");
                var current = File.Exists(settingsPath)
                    ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? new JsonObject()
                    : new JsonObject();
                foreach (var key in PortableSettingKeys)
                    if (imported.TryGetPropertyValue(key, out var val))
                        current[key] = val?.DeepClone();
                WriteAtomic(settingsPath, current.ToJsonString(Indented));
                // Drop the store's cache so the next Load() re-reads the just-written file.
                // (Save(Load()) would re-serialize the STALE cache over the imported values.)
                _settings.Invalidate();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config import failed");
            return false;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
