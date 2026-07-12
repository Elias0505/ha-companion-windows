// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>Persists which entities are pinned as tiles, their order and per-tile sizes.</summary>
public interface ITileLayoutStore
{
    IReadOnlyList<string> LoadPinned();

    /// <summary>Per-entity tile spans in grid cells (cols, rows); entities not listed are 1×1.</summary>
    IReadOnlyDictionary<string, (int Cols, int Rows)> LoadSpans();

    void SavePinned(IReadOnlyList<string> entityIds);

    void SaveSpans(IReadOnlyDictionary<string, (int Cols, int Rows)> spans);
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

    public IReadOnlyDictionary<string, (int Cols, int Rows)> LoadSpans()
    {
        var layout = GetLayout();
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        if (layout.Spans is not null)
        {
            foreach (var (id, text) in layout.Spans)
            {
                var parts = text.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var r))
                    result[id] = (c, r);
            }
        }
        else if (layout.Sizes is not null)
        {
            // Migrate the short-lived preset format (1 = wide, 2 = large) to free spans.
            foreach (var (id, mode) in layout.Sizes)
                if (mode is 1 or 2)
                    result[id] = (2, mode == 2 ? 2 : 1);
        }

        return result;
    }

    public void SavePinned(IReadOnlyList<string> entityIds)
    {
        GetLayout().Pinned = [.. entityIds];
        Write();
    }

    public void SaveSpans(IReadOnlyDictionary<string, (int Cols, int Rows)> spans)
    {
        var layout = GetLayout();
        // Only persist non-default sizes ("CxR"); missing = 1×1. The legacy Sizes key is dropped.
        layout.Spans = spans
            .Where(kv => kv.Value.Cols > 1 || kv.Value.Rows > 1)
            .ToDictionary(kv => kv.Key, kv => $"{kv.Value.Cols}x{kv.Value.Rows}");
        layout.Sizes = null;
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

        /// <summary>Legacy preset sizes (read for migration only; never written back).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, int>? Sizes { get; set; }

        /// <summary>Per-entity spans as "ColsxRows" (e.g. "2x1"); missing = 1×1.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Spans { get; set; }
    }
}
