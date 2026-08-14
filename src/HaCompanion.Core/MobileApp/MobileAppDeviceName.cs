// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.MobileApp;

/// <summary>
/// Normalizes the user-configurable Home Assistant device name (#8). Lives in Core so the
/// rules are unit-tested — the App project has no test coverage by construction.
/// </summary>
public static class MobileAppDeviceName
{
    /// <summary>
    /// HA device names are free-form, but an unbounded or control-character-laden name only
    /// invites broken slugs and UI glitches. 64 characters is generous for a PC name.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The effective device name: the user's choice — trimmed, control characters stripped,
    /// length-capped — or <paramref name="fallback"/> (the computer name) when the setting is
    /// empty or collapses to nothing after cleanup. Never returns an empty string as long as
    /// the fallback is non-empty.
    /// </summary>
    public static string Resolve(string? configured, string fallback)
    {
        var clean = new string((configured ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxLength)
            clean = clean[..MaxLength].TrimEnd();
        return clean.Length > 0 ? clean : fallback;
    }
}
