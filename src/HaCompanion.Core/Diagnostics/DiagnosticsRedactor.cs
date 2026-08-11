// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using System.Text.RegularExpressions;

namespace HaCompanion.Core.Diagnostics;

/// <summary>
/// Pure helpers for the diagnostics export: secret redaction and a UTF-8-safe file tail.
/// Lives in Core so the platform-independent test suite covers it.
/// </summary>
public static class DiagnosticsRedactor
{
    public const string Redacted = "<redacted>";

    // Home Assistant long-lived access tokens are JWTs: three base64url segments split by
    // dots, the first starting with the encoded '{"' header. Matching the SHAPE catches
    // secrets an exact-value pass cannot — above all a token the user has ROTATED since,
    // which may still sit in the rolled-over log tails the report bundles up.
    private static readonly Regex JwtLike = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // mobile_app webhook ids are long hex strings; they authenticate the webhook URL.
    private static readonly Regex WebhookLike = new(
        @"\b[0-9a-f]{32,}\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Replace every occurrence of the given secrets with a placeholder, then redact
    /// anything merely SHAPED like a token or webhook id — the exact-value pass only knows
    /// the secrets currently in settings, not the ones that were valid when an older log
    /// line was written.
    /// </summary>
    public static string Redact(string text, IEnumerable<string?> secrets)
    {
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        try
        {
            text = JwtLike.Replace(text, Redacted);
            text = WebhookLike.Replace(text, Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: the exact-value redaction above already ran, and the
            // report is still usable — better than failing the export outright.
        }
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
