// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.MobileApp;

/// <summary>
/// Rules for the <c>command_launch</c> whitelist. Entries must be existing absolute
/// .exe paths — no URLs, scripts, shortcuts or bare names that the shell would resolve
/// on its own (PATH, App Paths, file associations).
/// </summary>
public static class LaunchWhitelist
{
    /// <summary>
    /// True when the entry is launchable; <paramref name="fullPath"/> then holds its
    /// canonical form (that path, never the string Home Assistant sent, is started).
    /// </summary>
    public static bool TryValidateEntry(string? entry, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(entry))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(entry))
                return false;
            var full = Path.GetFullPath(entry);
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full))
            {
                return false;
            }
            fullPath = full;
            return true;
        }
        catch (Exception)
        {
            // Invalid path characters, a path that is too long, … — not launchable.
            return false;
        }
    }
}
