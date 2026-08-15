// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.MobileApp;

/// <summary>
/// Rules for the <c>command_launch</c> whitelist. Entries are existing absolute .exe paths,
/// optionally followed by ARGUMENTS (issue #17) — no URLs, scripts, shortcuts or bare names
/// that the shell would resolve on its own (PATH, App Paths, file associations).
///
/// Security invariant: arguments live in the LOCAL whitelist entry only. The Home Assistant
/// message merely selects WHICH pre-approved entry runs — it can never add or alter
/// arguments, so a compromised HA cannot compose commands.
/// </summary>
public static class LaunchWhitelist
{
    /// <summary>Arguments longer than this reject the entry (nothing legitimate needs more).</summary>
    public const int MaxArgsLength = 1024;

    /// <summary>
    /// Parse a whitelist entry into its executable path and (optional) arguments.
    /// Accepted shapes, tried in order:
    /// <list type="number">
    /// <item><c>"C:\Program Files\App\app.exe" -flag "a value"</c> — quoted path, rest = args;</item>
    /// <item><c>C:\Apps\tool.exe</c> — the whole entry is the path (the pre-#17 format);</item>
    /// <item><c>C:\Program Files\App\app.exe -flag value</c> — unquoted: split at each
    /// ".exe" boundary and take the first prefix that passes ALL path checks.</item>
    /// </list>
    /// The path part always goes through the full validation (absolute, no UNC/device
    /// namespace, no ADS, .exe, exists, fixed local drive).
    /// </summary>
    public static bool TryParseEntry(string? entry, out string fullPath, out string args)
    {
        fullPath = "";
        args = "";
        if (string.IsNullOrWhiteSpace(entry))
            return false;
        entry = entry.Trim();

        // Arguments with control characters could smuggle log-forging or invisible payloads
        // into a value the UI shows back to the user — reject the whole entry.
        if (entry.Any(char.IsControl))
            return false;

        if (entry.StartsWith('"'))
        {
            var close = entry.IndexOf('"', 1);
            if (close < 0)
                return false; // unterminated quote
            var candidate = entry[1..close];
            var rest = entry[(close + 1)..].Trim();
            if (rest.Length > MaxArgsLength || !TryValidateEntry(candidate, out fullPath))
                return false;
            args = rest;
            return true;
        }

        // The whole string as a path — the original format, and also the common case.
        if (TryValidateEntry(entry, out fullPath))
            return true;

        // Unquoted path + args: candidate boundaries are each ".exe" followed by whitespace.
        // Validation (File.Exists among others) picks the real boundary, which also handles
        // a directory literally containing ".exe " in its name.
        var search = 0;
        while (true)
        {
            var idx = entry.IndexOf(".exe", search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;
            var end = idx + 4;
            if (end < entry.Length && char.IsWhiteSpace(entry[end]))
            {
                var candidate = entry[..end];
                var rest = entry[end..].Trim();
                if (rest.Length <= MaxArgsLength && TryValidateEntry(candidate, out fullPath))
                {
                    args = rest;
                    return true;
                }
            }
            search = end;
        }
    }

    /// <summary>
    /// Render a parsed entry back into its canonical stored form: the bare path, or
    /// <c>"path" args</c> when arguments are present (quoting makes every later parse
    /// take the deterministic first branch).
    /// </summary>
    public static string CanonicalEntry(string fullPath, string args) =>
        string.IsNullOrEmpty(args) ? fullPath : $"\"{fullPath}\" {args}";

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

/// <summary>
/// Rules for the <c>command_close_app</c> allowlist (issue #17): bare process image names
/// only. Home Assistant may only pick from this locally approved list — it can never close
/// an arbitrary process (a compromised HA could otherwise target backup or security tools).
/// </summary>
public static class CloseAppWhitelist
{
    public const int MaxNameLength = 64;

    /// <summary>
    /// True when the value is a plain process name; <paramref name="normalized"/> holds the
    /// canonical form (lowercase, without the optional <c>.exe</c> suffix) used for both
    /// storage and matching. Paths, wildcards, drive colons and control characters reject.
    /// </summary>
    public static bool TryValidateName(string? name, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var clean = name.Trim();
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^4];
        if (clean.Length is 0 or > MaxNameLength)
            return false;
        // A process image name is matched by name, never resolved as a path — anything
        // path- or pattern-shaped signals a misunderstanding (or an injection attempt).
        foreach (var c in clean)
        {
            if (char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ';')
                return false;
        }
        normalized = clean.ToLowerInvariant();
        return true;
    }
}
