// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Models;

/// <summary>
/// Knows which Home Assistant domains are worth surfacing as quick-action tiles,
/// which icon to show for them, and which service call toggles/activates them.
/// </summary>
public static class DomainCatalog
{
    /// <summary>Domains that make sense as one-tap quick actions.</summary>
    public static readonly IReadOnlySet<string> Actionable = new HashSet<string>(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "input_boolean", "cover", "scene",
        "script", "automation", "media_player", "climate", "lock", "button",
    };

    /// <summary>Read-only domains: pinnable value tiles (state + unit), but no tap action.</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "sensor", "binary_sensor",
    };

    public static bool IsActionable(string domain) => Actionable.Contains(domain);

    /// <summary>Domains that become tiles at all (actionable + read-only sensors).</summary>
    public static bool IsDisplayable(string domain) => Actionable.Contains(domain) || ReadOnly.Contains(domain);

    /// <summary>True when tapping a tile of this domain performs a service call.</summary>
    public static bool HasAction(string domain) => Actionable.Contains(domain);

    /// <summary>A Segoe Fluent Icons glyph for the given domain (as a 1-char string).</summary>
    public static string Glyph(string domain) => char.ConvertFromUtf32(GlyphCode(domain));

    // Segoe Fluent Icons / MDL2 private-use code points. Kept as plain hex (ASCII source only).
    private static int GlyphCode(string domain) => domain switch
    {
        "light" => 0xE706,         // Brightness
        "switch" => 0xE7E8,        // PowerButton
        "fan" => 0xE72C,           // Refresh (fan-ish)
        "input_boolean" => 0xE73E, // CheckboxComposite
        "cover" => 0xE70E,         // ChevronUp (blinds-ish)
        "scene" => 0xE7C1,         // Flag
        "script" => 0xE943,        // Code
        "automation" => 0xE945,    // LightningBolt
        "media_player" => 0xE768,  // Play
        "climate" => 0xE9CA,       // Temperature-ish
        "lock" => 0xE72E,          // Lock
        "button" => 0xE7C9,        // Touch pointer
        _ => 0xE71D,               // AllApps (generic)
    };

    /// <summary>
    /// Resolve the (domain, service) to call to toggle/activate an entity.
    /// <paramref name="isOn"/> is used for state-dependent domains (locks).
    /// </summary>
    public static (string Domain, string Service) ResolveAction(string domain, bool isOn) => domain switch
    {
        "scene" => ("scene", "turn_on"),
        "script" => ("script", "toggle"),
        "button" => ("button", "press"),
        "automation" => ("automation", "toggle"),
        "lock" => ("lock", isOn ? "lock" : "unlock"),
        _ => ("homeassistant", "toggle"),
    };
}
