// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.IO;
using System.Text.Json;
using HaCompanion.Core.Models;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Resolves a Home Assistant entity to its Material Design Icons glyph — using the
/// entity's own <c>icon</c> attribute (<c>mdi:*</c>) when present, otherwise a sensible
/// per-domain default. The name→codepoint table and font are bundled in Assets/Mdi.
/// </summary>
public sealed class MdiIconProvider
{
    private readonly Dictionary<string, string> _glyphByName;

    public MdiIconProvider(ILogger<MdiIconProvider> logger)
    {
        _glyphByName = Load(logger);
    }

    /// <summary>Glyph string (may be a surrogate pair) for an entity's icon.</summary>
    public string Resolve(HaEntityState state) => GlyphForName(IconName(state));

    /// <summary>Glyph for a domain's default icon (used for group headers).</summary>
    public string DomainGlyph(string domain) => GlyphForName(DefaultName(domain));

    private string GlyphForName(string name)
    {
        if (_glyphByName.TryGetValue(name, out var glyph))
            return glyph;
        return _glyphByName.TryGetValue("shape", out var fallback) ? fallback : string.Empty;
    }

    private static string IconName(HaEntityState state)
    {
        var icon = state.GetAttributeString("icon");
        if (!string.IsNullOrEmpty(icon) && icon.StartsWith("mdi:", StringComparison.OrdinalIgnoreCase))
            return icon[4..];
        return DefaultName(state.Domain);
    }

    private static string DefaultName(string domain) => domain switch
    {
        "light" => "lightbulb",
        "switch" => "toggle-switch-variant",
        "fan" => "fan",
        "cover" => "window-shutter",
        "scene" => "palette",
        "script" => "script-text",
        "automation" => "robot",
        "media_player" => "cast",
        "climate" => "thermostat",
        "lock" => "lock",
        "button" => "gesture-tap-button",
        "input_boolean" => "toggle-switch-outline",
        "sensor" => "gauge",
        "binary_sensor" => "checkbox-marked-circle",
        "person" => "account",
        "device_tracker" => "map-marker",
        "vacuum" => "robot-vacuum",
        "camera" => "cctv",
        _ => "shape",
    };

    private static Dictionary<string, string> Load(ILogger logger)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Mdi", "mdi-map.json");
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (raw is null)
                return result;
            foreach (var (name, hex) in raw)
            {
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                    result[name] = char.ConvertFromUtf32(codePoint);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load MDI icon map; icons will be blank");
        }
        return result;
    }
}
