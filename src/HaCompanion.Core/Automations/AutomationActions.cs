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

    private static readonly string[] RunOnly = { Run };
    private static readonly string[] OnOffToggle = { TurnOn, TurnOff, Toggle };
    private static readonly string[] OnOff = { TurnOn, TurnOff };
    private static readonly string[] None = Array.Empty<string>();

    /// <summary>Actions a rule may use for an entity of the given domain (UI chip order).</summary>
    public static IReadOnlyList<string> AllowedFor(string domain) => domain switch
    {
        "script" or "scene" or "button" => RunOnly,
        "lock" => OnOff, // "toggle a lock" is a footgun — force an explicit direction
        _ when DomainCatalog.Actionable.Contains(domain) => OnOffToggle,
        _ => None,
    };

    public static bool IsAllowed(string domain, string action) =>
        AllowedFor(domain).Contains(action, StringComparer.Ordinal);

    /// <summary>Map (entity domain, rule action) to the HA service call to make.</summary>
    public static (string Domain, string Service) Resolve(string entityDomain, string action) => (entityDomain, action) switch
    {
        ("script", Run) => ("script", "turn_on"),
        ("scene", Run) => ("scene", "turn_on"),
        ("button", Run) => ("button", "press"),
        ("lock", TurnOn) => ("lock", "lock"),
        ("lock", TurnOff) => ("lock", "unlock"),
        (_, TurnOn) => ("homeassistant", "turn_on"),
        (_, TurnOff) => ("homeassistant", "turn_off"),
        (_, Toggle) => ("homeassistant", "toggle"),
        _ => throw new ArgumentException($"action '{action}' not valid for domain '{entityDomain}'"),
    };
}
