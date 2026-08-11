// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using HaCompanion.Core.Diagnostics;
using Xunit;

namespace HaCompanion.Core.Tests;

public class DiagnosticsRedactorTests
{
    [Fact]
    public void Redacts_every_occurrence_of_every_secret()
    {
        var text = "token=abc123 webhook=whX url=ok token again: abc123";
        var result = DiagnosticsRedactor.Redact(text, new[] { "abc123", "whX" });
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("whX", result);
        Assert.Equal(3, CountOf(result, DiagnosticsRedactor.Redacted));
        Assert.Contains("url=ok", result);
    }

    [Fact]
    public void Null_and_empty_secrets_are_ignored()
    {
        var text = "nothing to hide";
        Assert.Equal(text, DiagnosticsRedactor.Redact(text, new string?[] { null, "" }));
    }

    [Fact]
    public void Tail_of_missing_file_is_empty()
    {
        Assert.Equal(string.Empty,
            DiagnosticsRedactor.TailFile("/nonexistent/dir/app.log", 1024));
    }

    [Fact]
    public void Tail_reads_only_the_end_of_a_large_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, new string('a', 5000) + "END-MARKER");
            var tail = DiagnosticsRedactor.TailFile(path, 100);
            Assert.EndsWith("END-MARKER", tail);
            Assert.True(tail.Length <= 100);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Truncated_cut_lands_on_a_utf8_character_boundary()
    {
        // "ä" is 2 bytes in UTF-8; an odd cut position would split it into mojibake.
        var bytes = Encoding.UTF8.GetBytes("ääää");
        var partial = bytes.Skip(1).ToArray(); // starts mid-character
        var decoded = DiagnosticsRedactor.Decode(partial, skipLeadingPartial: true);
        Assert.Equal("äää", decoded);
        Assert.DoesNotContain('�', decoded); // no replacement characters
    }

    [Fact]
    public void Full_reads_keep_the_first_character()
    {
        var bytes = Encoding.UTF8.GetBytes("äbc");
        Assert.Equal("äbc", DiagnosticsRedactor.Decode(bytes, skipLeadingPartial: false));
    }

    [Fact]
    public void A_rotated_token_is_still_redacted_by_shape()
    {
        // The exact-value pass only knows the CURRENT secrets. An older log tail can hold
        // a token that has since been rotated — the report must not leak it either.
        var oldToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJhYmNkZWYwMTIzNDU2Nzg5"
                       + "IiwiaWF0IjoxNzAwMDAwMDAwfQ.Zm9vYmFyYmF6cXV4c2lnbmF0dXJl";
        var text = $"connect failed with token {oldToken} — retrying";

        var redacted = DiagnosticsRedactor.Redact(text, new[] { "current-token" });

        Assert.DoesNotContain(oldToken, redacted, StringComparison.Ordinal);
        Assert.Contains(DiagnosticsRedactor.Redacted, redacted, StringComparison.Ordinal);
        Assert.Contains("retrying", redacted, StringComparison.Ordinal); // context survives
    }

    [Fact]
    public void A_webhook_id_shape_is_redacted()
    {
        var text = "POST /api/webhook/0123456789abcdef0123456789abcdef01234567 -> 200";
        var redacted = DiagnosticsRedactor.Redact(text, Array.Empty<string?>());
        Assert.DoesNotContain("0123456789abcdef", redacted, StringComparison.Ordinal);
        Assert.Contains("-> 200", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_survives_the_shape_pass()
    {
        // Short hex (colours, ids) and normal words must not be mangled.
        const string text = "theme #ff8800, entity light.kitchen, build 1.6.0, 8 devices";
        Assert.Equal(text, DiagnosticsRedactor.Redact(text, Array.Empty<string?>()));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
