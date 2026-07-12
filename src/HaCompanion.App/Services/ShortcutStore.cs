// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>A global hotkey bound to one Home Assistant entity (toggled/run on press).</summary>
public sealed record ShortcutBinding(string Hotkey, string EntityId);

/// <summary>Persists the user's entity shortcuts (hotkey → entity).</summary>
public interface IShortcutStore
{
    IReadOnlyList<ShortcutBinding> Load();

    void Save(IReadOnlyList<ShortcutBinding> shortcuts);
}

/// <inheritdoc cref="IShortcutStore"/>
public sealed class ShortcutStore : IShortcutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<ShortcutStore> _logger;
    private readonly string _file;
    private List<ShortcutBinding>? _cache;

    public ShortcutStore(ILogger<ShortcutStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "shortcuts.json");
    }

    public IReadOnlyList<ShortcutBinding> Load()
    {
        if (_cache is not null)
            return _cache;
        try
        {
            _cache = File.Exists(_file)
                ? JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file))?.Shortcuts
                      .Where(s => !string.IsNullOrWhiteSpace(s.Hotkey) && !string.IsNullOrWhiteSpace(s.EntityId))
                      .ToList() ?? []
                : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load shortcuts; starting empty");
            _cache = [];
        }
        return _cache;
    }

    public void Save(IReadOnlyList<ShortcutBinding> shortcuts)
    {
        _cache = [.. shortcuts];
        try
        {
            // Temp + move keeps the file intact if the app dies mid-write.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Persisted { Shortcuts = _cache }, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save shortcuts");
        }
    }

    private sealed class Persisted
    {
        public List<ShortcutBinding> Shortcuts { get; set; } = [];
    }
}
