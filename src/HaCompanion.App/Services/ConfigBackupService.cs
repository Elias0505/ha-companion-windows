// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaCompanion.Core.Configuration;
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

    // Which settings may travel in a bundle — and their expected types — live in Core
    // (PortableSettings) so the rules are unit-tested. An import writes straight into
    // settings.json, so a mistake in that list is a security bug.
    private static IReadOnlyList<string> PortableSettingKeys => PortableSettings.Keys;

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

            // Only understand bundles up to our own format version. A newer bundle may carry
            // keys/semantics this build can't validate, so reject rather than half-apply it.
            var version = bundle["_version"]?.GetValue<int>() ?? 0;
            if (version <= 0 || version > Version)
            {
                _logger.LogWarning("Import rejected: unsupported bundle version {Version}", version);
                return false;
            }

            // Validate EVERYTHING that can be validated before writing anything — a half-applied
            // bundle (files in, settings rejected) leaves the config inconsistent, and a
            // type-confused settings value would make settings.json unparseable, which the
            // load-time fallback then overwrites with defaults (destroying the stored token).
            // (Environmental IO failures during the writes themselves remain the one residual
            // way to end up half-applied.)
            var imported = bundle["settings"]?.AsObject();
            if (imported is not null && !PortableSettings.TypesValid(imported))
            {
                _logger.LogWarning("Import rejected: a setting has the wrong type");
                return false;
            }
            if (imported is not null)
            {
                // Dry-run the part of the settings merge that can REJECT: an unparseable
                // existing settings.json must fail the import here, before the payload files
                // are replaced — not halfway through.
                var settingsPath = Path.Combine(_dir, "settings.json");
                if (File.Exists(settingsPath))
                    _ = JsonNode.Parse(File.ReadAllText(settingsPath)); // throws into the outer catch
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
            // The whole read-merge-write runs under the store's own lock (ReplaceOnDisk): a
            // background Update() landing in between would otherwise persist the PRE-import
            // snapshot — re-pairing the previous host's token with the imported URL.
            if (imported is not null)
            {
                _settings.ReplaceOnDisk(() =>
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
                        // Also give up a blob the store kept in memory because it could not be
                        // decrypted here — otherwise the next save would put it back.
                        _settings.DiscardPreservedSecrets();
                    }

                    WriteAtomic(settingsPath, current.ToJsonString(Indented));
                });
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
