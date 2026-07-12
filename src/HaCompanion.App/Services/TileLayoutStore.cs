// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>Persists which entities are pinned as tiles, their order and per-tile sizes.</summary>
public interface ITileLayoutStore
{
    IReadOnlyList<string> LoadPinned();

    /// <summary>Per-entity tile size mode (0 small / 1 wide / 2 large); entities not listed are small.</summary>
    IReadOnlyDictionary<string, int> LoadSizes();

    void SavePinned(IReadOnlyList<string> entityIds);

    void SaveSizes(IReadOnlyDictionary<string, int> sizes);
}

/// <inheritdoc cref="ITileLayoutStore"/>
public sealed class TileLayoutStore : ITileLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<TileLayoutStore> _logger;
    private readonly string _file;
    private Layout? _layout; // in-memory copy so partial saves (order vs sizes) merge correctly

    public TileLayoutStore(ILogger<TileLayoutStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaCompanion");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "layout.json");
    }

    public IReadOnlyList<string> LoadPinned() => GetLayout().Pinned;

    public IReadOnlyDictionary<string, int> LoadSizes() => GetLayout().Sizes ?? new Dictionary<string, int>();

    public void SavePinned(IReadOnlyList<string> entityIds)
    {
        GetLayout().Pinned = [.. entityIds];
        Write();
    }

    public void SaveSizes(IReadOnlyDictionary<string, int> sizes)
    {
        // Only persist non-default sizes; missing = small.
        GetLayout().Sizes = sizes.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        Write();
    }

    private Layout GetLayout()
    {
        if (_layout is not null)
            return _layout;
        try
        {
            _layout = File.Exists(_file)
                ? JsonSerializer.Deserialize<Layout>(File.ReadAllText(_file)) ?? new Layout()
                : new Layout();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tile layout; starting empty");
            _layout = new Layout();
        }
        return _layout;
    }

    private void Write()
    {
        try
        {
            // Temp + move keeps the layout file intact if the app dies mid-write.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_layout, JsonOptions));
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

        public Dictionary<string, int>? Sizes { get; set; }
    }
}
