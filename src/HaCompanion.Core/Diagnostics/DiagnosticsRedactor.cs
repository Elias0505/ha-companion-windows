// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;

namespace HaCompanion.Core.Diagnostics;

/// <summary>
/// Pure helpers for the diagnostics export: secret redaction and a UTF-8-safe file tail.
/// Lives in Core so the platform-independent test suite covers it.
/// </summary>
public static class DiagnosticsRedactor
{
    public const string Redacted = "<redacted>";

    /// <summary>Replace every occurrence of the given secrets with a placeholder.</summary>
    public static string Redact(string text, IEnumerable<string?> secrets)
    {
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// Read at most <paramref name="maxBytes"/> from the end of a (possibly still open)
    /// log file, starting on a UTF-8 character boundary so multi-byte characters at the
    /// cut never turn into mojibake.
    /// </summary>
    public static string TailFile(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - maxBytes);
            fs.Position = start;
            var buffer = new byte[fs.Length - start];
            fs.ReadExactly(buffer);
            return Decode(buffer, skipLeadingPartial: start > 0);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>Decode UTF-8, optionally skipping a partial character at the buffer start.</summary>
    internal static string Decode(byte[] buffer, bool skipLeadingPartial)
    {
        var offset = 0;
        if (skipLeadingPartial)
            while (offset < buffer.Length && (buffer[offset] & 0b1100_0000) == 0b1000_0000)
                offset++; // skip UTF-8 continuation bytes until a character start
        return Encoding.UTF8.GetString(buffer, offset, buffer.Length - offset);
    }
}
