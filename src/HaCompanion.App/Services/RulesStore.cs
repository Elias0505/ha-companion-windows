// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using HaCompanion.Core.Automations;

namespace HaCompanion.App.Services;

/// <summary>Persistent store for the Windows automation rules (automations.json).</summary>
public interface IRulesStore
{
    IReadOnlyList<AutomationRule> Load();

    void Save(IReadOnlyList<AutomationRule> rules);

    /// <summary>Drop the cache so the next <see cref="Load"/> re-reads the file (config import).</summary>
    void Invalidate();
}

/// <inheritdoc cref="IRulesStore"/>
/// <remarks>Same shape as ShortcutStore: cached list, Persisted wrapper object (forward
/// compatible), atomic tmp+move write. Rules that fail validation are dropped on load —
/// a broken entry must not wedge the whole list.</remarks>
public sealed class RulesStore : IRulesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaCompanion", "automations.json");

    private List<AutomationRule>? _cache;

    public IReadOnlyList<AutomationRule> Load()
    {
        lock (_gate)
        {
            if (_cache is not null)
                return _cache;
            var migrated = false;
            try
            {
                var raw = File.Exists(_file)
                    ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file))?.Rules
                        ?.Where(r => r is not null && r.IsValid()).ToList() ?? new List<AutomationRule>()
                    : new List<AutomationRule>();
                _cache = raw.Select(r =>
                {
                    // Migrate old files: give every rule a stable id and fold the legacy single
                    // Condition into the canonical Conditions list.
                    if (r.Id is not null && r.Condition is null)
                        return r;
                    migrated = true;
                    var conditions = r.EffectiveConditions;
                    return r with
                    {
                        Id = r.Id ?? Guid.NewGuid().ToString("N"),
                        Conditions = conditions.Count > 0 ? conditions : null,
                        Condition = null,
                    };
                }).ToList();
            }
            catch
            {
                _cache = new List<AutomationRule>(); // unreadable file — start empty, don't crash
            }
            if (migrated && _cache.Count > 0)
                WriteToDisk(_cache); // upgrade the file once, in the new shape
            return _cache;
        }
    }

    public void Save(IReadOnlyList<AutomationRule> rules)
    {
        lock (_gate)
        {
            _cache = rules.ToList();
            WriteToDisk(_cache);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
            _cache = null;
    }

    private void WriteToDisk(List<AutomationRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Persisted { Rules = rules }, JsonOptions));
        File.Move(tmp, _file, overwrite: true);
    }

    private sealed class Persisted
    {
        public List<AutomationRule> Rules { get; set; } = new();
    }
}
