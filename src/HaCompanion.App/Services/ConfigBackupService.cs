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

    // Settings keys carried in an exported bundle. Cosmetic/behavioural only.
    //
    // Deliberately EXCLUDED, and never re-added: the token/webhook/device id (secrets),
    // and every SECURITY DECISION — IgnoreCertificateErrors, all AllowCmd* toggles and
    // LaunchWhitelist. A shared config file must not be able to disable TLS validation,
    // enable HA→PC commands, or seed a launch whitelist behind the user's back; those are
    // choices the user makes locally, not something an import turns on. BaseUrl is kept
    // (convenient on a new PC) but triggers a credential reset on import when it changes,
    // so it can never redirect the stored token to a foreign host. QuickPanelMonitor is
    // device-specific and intentionally not portable.
    private static readonly string[] PortableSettingKeys =
    {
        "BaseUrl", "Hotkey", "AutoHideQuickPanel", "QuickPanelWidth",
        "Language", "QuickPanelStartView", "QuickPanelLastView", "QuickPanelDragResize",
        "QuickPanelSortByCategory", "ShowHaNotifications", "IdleSensorThresholdMinutes",
    };

    /// <summary>Expected JSON kind per portable key — an import that disagrees is rejected
    /// whole, so a type-confused bundle can't corrupt settings.json (and, via the load-time
    /// fallback, destroy the stored token).</summary>
    private static bool PortableTypesValid(JsonObject imported)
    {
        foreach (var key in PortableSettingKeys)
        {
            if (!imported.TryGetPropertyValue(key, out var node) || node is null)
                continue;
            var kind = node.GetValueKind();
            var ok = key switch
            {
                "BaseUrl" or "Hotkey" or "Language" or "QuickPanelStartView" or "QuickPanelLastView"
                    => kind == JsonValueKind.String,
                "AutoHideQuickPanel" or "QuickPanelDragResize" or "QuickPanelSortByCategory"
                    or "ShowHaNotifications"
                    => kind is JsonValueKind.True or JsonValueKind.False,
                "QuickPanelWidth" or "IdleSensorThresholdMinutes"
                    => kind == JsonValueKind.Number,
                _ => true,
            };
            if (!ok)
                return false;
        }
        return true;
    }

    private readonly IShortcutStore _shortcuts;
    private readonly IRulesStore _rules;
    private readonly INotifyRulesStore _notifyRules;
    private readonly ITileLayoutStore _layout;

    public ConfigBackupService(ISettingsStore settings, IShortcutStore shortcuts, IRulesStore rules,
        INotifyRulesStore notifyRules, ITileLayoutStore layout, ILogger<ConfigBackupService> logger)
    {
        _settings = settings;
        _shortcuts = shortcuts;
        _rules = rules;
        _notifyRules = notifyRules;
        _layout = layout;
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

            // Validate EVERYTHING before writing anything — a half-applied bundle (files in,
            // settings rejected) could leave the config inconsistent, and a type-confused
            // settings value would make settings.json unparseable, which the load-time
            // fallback then overwrites with defaults (destroying the stored token).
            var imported = bundle["settings"]?.AsObject();
            if (imported is not null && !PortableTypesValid(imported))
            {
                _logger.LogWarning("Import rejected: a setting has the wrong type");
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

                // Every store caches its file in memory AND writes from that cache — without
                // dropping the caches the import only shows after a restart, and worse, the
                // next Save() would overwrite the imported file with pre-import data.
                _shortcuts.Invalidate();
                _rules.Invalidate();
                _notifyRules.Invalidate();
                _layout.Invalidate();
            }

            // Merge portable settings into the existing settings.json (keeps token/webhook).
            if (imported is not null)
            {
                var settingsPath = Path.Combine(_dir, "settings.json");
                var current = File.Exists(settingsPath)
                    ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? new JsonObject()
                    : new JsonObject();

                var oldBaseUrl = current["BaseUrl"]?.GetValue<string>() ?? string.Empty;
                foreach (var key in PortableSettingKeys)
                    if (imported.TryGetPropertyValue(key, out var val))
                        current[key] = val?.DeepClone();

                // A changed BaseUrl must never reunite the stored token with a different host:
                // drop the credentials so the user re-authenticates against the imported URL
                // instead of silently leaking the token to it on the next connect.
                var newBaseUrl = current["BaseUrl"]?.GetValue<string>() ?? string.Empty;
                if (!string.Equals(oldBaseUrl.TrimEnd('/'), newBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    current.Remove("TokenProtected");
                    current.Remove("WebhookIdProtected");
                    current.Remove("MobileAppDeviceId");
                }

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
