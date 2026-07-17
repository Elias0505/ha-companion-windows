// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Models;

namespace HaCompanion.Core.Automations;

/// <summary>
/// Explicit rule actions (unlike the tiles' state-dependent default toggle):
/// which actions each domain supports and which HA service implements them.
/// </summary>
public static class AutomationActions
{
    public const string TurnOn = "turn_on";
    public const string TurnOff = "turn_off";
    public const string Toggle = "toggle";
    public const string Run = "run";

    // Value-setting verbs. Light brightness/colour ride the normal turn_on (homeassistant.turn_on
    // forwards them); these need their own services and each carries exactly one numeric field.
    public const string SetVolume = "set_volume";           // media_player.volume_set  → volume_level
    public const string SetTemperature = "set_temperature"; // climate.set_temperature  → temperature
    public const string SetPosition = "set_position";       // cover.set_cover_position → position
    public const string SetPercentage = "set_percentage";   // fan.set_percentage       → percentage

    private static readonly string[] RunOnly = { Run };
    private static readonly string[] OnOff = { TurnOn, TurnOff };
    private static readonly string[] None = Array.Empty<string>();

    /// <summary>Actions a rule may use for an entity of the given domain (UI chip order).</summary>
    public static IReadOnlyList<string> AllowedFor(string domain) => domain switch
    {
        "script" or "scene" or "button" => RunOnly,
        "lock" => OnOff, // "toggle a lock" is a footgun — force an explicit direction
        "media_player" => new[] { TurnOn, TurnOff, Toggle, SetVolume },
        "climate" => new[] { TurnOn, TurnOff, Toggle, SetTemperature },
        "cover" => new[] { TurnOn, TurnOff, Toggle, SetPosition },
        "fan" => new[] { TurnOn, TurnOff, Toggle, SetPercentage },
        _ when DomainCatalog.Actionable.Contains(domain) => new[] { TurnOn, TurnOff, Toggle },
        _ => None,
    };

    public static bool IsAllowed(string domain, string action) =>
        AllowedFor(domain).Contains(action, StringComparer.Ordinal);

    /// <summary>The service-data key a value-setting verb carries (null for plain verbs).</summary>
    public static string? DataKey(string action) => action switch
    {
        SetVolume => "volume_level",
        SetTemperature => "temperature",
        SetPosition => "position",
        SetPercentage => "percentage",
        _ => null,
    };

    /// <summary>Map (entity domain, rule action) to the HA service call to make.</summary>
    public static (string Domain, string Service) Resolve(string entityDomain, string action) => (entityDomain, action) switch
    {
        ("script", Run) => ("script", "turn_on"),
        ("scene", Run) => ("scene", "turn_on"),
        ("button", Run) => ("button", "press"),
        ("lock", TurnOn) => ("lock", "lock"),
        ("lock", TurnOff) => ("lock", "unlock"),
        ("media_player", SetVolume) => ("media_player", "volume_set"),
        ("climate", SetTemperature) => ("climate", "set_temperature"),
        ("cover", SetPosition) => ("cover", "set_cover_position"),
        ("fan", SetPercentage) => ("fan", "set_percentage"),
        (_, TurnOn) => ("homeassistant", "turn_on"), // forwards brightness_pct / colour data
        (_, TurnOff) => ("homeassistant", "turn_off"),
        (_, Toggle) => ("homeassistant", "toggle"),
        _ => throw new ArgumentException($"action '{action}' not valid for domain '{entityDomain}'"),
    };
}
