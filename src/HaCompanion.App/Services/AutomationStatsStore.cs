// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;

namespace HaCompanion.App.Services;

/// <summary>Persisted run statistics for one automation rule.</summary>
public sealed record AutomationStat(DateTimeOffset LastFired, int RunCount);

/// <summary>
/// Persists per-rule "last fired" time and run count (automation_stats.json), keyed by the
/// rule's stable id — so the manager's "zuletzt: … · N×" survives an app restart. Separate
/// from automations.json so runtime stats never touch the rule-config contract.
/// </summary>
public interface IAutomationStatsStore
{
    AutomationStat? GetStat(string ruleId);

    /// <summary>Record one execution (now, count+1) for the rule and persist.</summary>
    void Record(string ruleId);

    /// <summary>Drop stats for ids no longer present (called after rules change).</summary>
    void Prune(IEnumerable<string> liveIds);
}

/// <inheritdoc cref="IAutomationStatsStore"/>
public sealed class AutomationStatsStore : IAutomationStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaCompanion", "automation_stats.json");

    private Dictionary<string, AutomationStat>? _cache;

    public AutomationStat? GetStat(string ruleId)
    {
        lock (_gate)
            return Cache().GetValueOrDefault(ruleId);
    }

    public void Record(string ruleId)
    {
        lock (_gate)
        {
            var cache = Cache();
            var prev = cache.GetValueOrDefault(ruleId);
            cache[ruleId] = new AutomationStat(DateTimeOffset.Now, (prev?.RunCount ?? 0) + 1);
            WriteToDisk(cache);
        }
    }

    public void Prune(IEnumerable<string> liveIds)
    {
        lock (_gate)
        {
            var cache = Cache();
            var live = new HashSet<string>(liveIds, StringComparer.Ordinal);
            var stale = cache.Keys.Where(k => !live.Contains(k)).ToList();
            if (stale.Count == 0)
                return;
            foreach (var key in stale)
                cache.Remove(key);
            WriteToDisk(cache);
        }
    }

    private Dictionary<string, AutomationStat> Cache()
    {
        if (_cache is not null)
            return _cache;
        try
        {
            _cache = File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, AutomationStat>>(File.ReadAllText(_file))
                  ?? new()
                : new();
        }
        catch
        {
            _cache = new();
        }
        return _cache;
    }

    private void WriteToDisk(Dictionary<string, AutomationStat> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // stats are best-effort; never let a write failure disturb rule execution
        }
    }
}
