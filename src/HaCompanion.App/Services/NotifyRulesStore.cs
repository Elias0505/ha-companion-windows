// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using HaCompanion.Core.Notifications;

namespace HaCompanion.App.Services;

/// <summary>Persistent store for the local notification rules (notify_rules.json).</summary>
public interface INotifyRulesStore
{
    IReadOnlyList<NotificationRule> Load();

    void Save(IReadOnlyList<NotificationRule> rules);

    /// <summary>Drop the cache so the next <see cref="Load"/> re-reads the file (config import).</summary>
    void Invalidate();
}

/// <inheritdoc cref="INotifyRulesStore"/>
/// <remarks>Same shape as ShortcutStore/RulesStore: cache, Persisted wrapper, tmp+move.</remarks>
public sealed class NotifyRulesStore : INotifyRulesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaCompanion", "notify_rules.json");

    private List<NotificationRule>? _cache;

    public IReadOnlyList<NotificationRule> Load()
    {
        lock (_gate)
        {
            if (_cache is not null)
                return _cache;
            try
            {
                _cache = File.Exists(_file)
                    ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file))?.Rules
                        ?.Where(r => r is not null && r.IsValid()).ToList() ?? new List<NotificationRule>()
                    : new List<NotificationRule>();
            }
            catch
            {
                _cache = new List<NotificationRule>();
            }
            return _cache;
        }
    }

    public void Save(IReadOnlyList<NotificationRule> rules)
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

    public void Invalidate()
    {
        lock (_gate)
            _cache = null;
    }

    private sealed class Persisted
    {
        public List<NotificationRule> Rules { get; set; } = new();
    }
}
