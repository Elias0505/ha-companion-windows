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
            // Reject UNC and the device/verbatim namespaces (\\server\share, \\?\, \\.\)
            // outright: a config-import attacker (issue: import writes the whitelist) could
            // otherwise point an entry at a binary on a share they control, which
            // command_launch would then pull and run on every invocation.
            if (entry.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            var full = Path.GetFullPath(entry);
            // The only legitimate colon in a fully-qualified DOS path is the drive letter's.
            // A second colon is an NTFS alternate-data-stream suffix (C:\ok.txt:evil.exe),
            // which reports extension ".exe" and is executable — never launch it.
            if (full.IndexOf(':', 2) >= 0)
                return false;
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full))
            {
                return false;
            }
            // A launch target must live on a fixed local disk — never a mapped network
            // drive (Z:\) or removable media that someone else can swap under it.
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
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
