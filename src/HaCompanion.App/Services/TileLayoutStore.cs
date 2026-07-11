// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>Persists which entities are pinned as tiles and their order.</summary>
public interface ITileLayoutStore
{
    IReadOnlyList<string> LoadPinned();

    void SavePinned(IReadOnlyList<string> entityIds);
}

/// <inheritdoc cref="ITileLayoutStore"/>
public sealed class TileLayoutStore : ITileLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<TileLayoutStore> _logger;
    private readonly string _file;

    public TileLayoutStore(ILogger<TileLayoutStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "layout.json");
    }

    public IReadOnlyList<string> LoadPinned()
    {
        try
        {
            if (!File.Exists(_file))
                return [];
            var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(_file));
            return layout?.Pinned ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tile layout; starting empty");
            return [];
        }
    }

    public void SavePinned(IReadOnlyList<string> entityIds)
    {
        try
        {
            // Temp + move keeps the layout file intact if the app dies mid-write.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Layout { Pinned = [.. entityIds] }, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save tile layout");
        }
    }

    private sealed class Layout
    {
        public List<string> Pinned { get; set; } = [];
    }
}
