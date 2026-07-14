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
            try
            {
                _cache = File.Exists(_file)
                    ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file))?.Rules
                        ?.Where(r => r is not null && r.IsValid()).ToList() ?? new List<AutomationRule>()
                    : new List<AutomationRule>();
            }
            catch
            {
                _cache = new List<AutomationRule>(); // unreadable file — start empty, don't crash
            }
            return _cache;
        }
    }

    public void Save(IReadOnlyList<AutomationRule> rules)
    {
        lock (_gate)
        {
            _cache = rules.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Persisted { Rules = _cache }, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    private sealed class Persisted
    {
        public List<AutomationRule> Rules { get; set; } = new();
    }
}
