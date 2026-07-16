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

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
