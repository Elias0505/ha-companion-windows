// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class CloseAppWhitelistTests
{
    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("Notepad.EXE", "notepad")]     // .exe suffix stripped, lowercased
    [InlineData("  spotify  ", "spotify")]     // trimmed
    [InlineData("msedgewebview2", "msedgewebview2")]
    [InlineData("app.exe.exe", "app.exe")]     // only ONE suffix stripped (image name keeps the rest)
    public void Valid_names_normalize(string raw, string expected)
    {
        Assert.True(CloseAppWhitelist.TryValidateName(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]                        // empty after suffix strip
    [InlineData(@"C:\Windows\notepad.exe")]     // paths reject — names only
    [InlineData("dir/notepad")]
    [InlineData("note*")]                       // wildcards reject
    [InlineData("note?pad")]
    [InlineData("note;pad")]                    // list separator can never hide inside a name
    [InlineData("note\u0001pad")]               // control characters
    [InlineData("a:b")]
    [InlineData("note\"pad")]
    public void Invalid_names_are_rejected(string? raw)
    {
        Assert.False(CloseAppWhitelist.TryValidateName(raw, out var normalized));
        Assert.Equal("", normalized);
    }

    [Fact]
    public void Overlong_names_are_rejected() =>
        Assert.False(CloseAppWhitelist.TryValidateName(
            new string('a', CloseAppWhitelist.MaxNameLength + 1), out _));

    [Fact]
    public void Max_length_name_is_accepted() =>
        Assert.True(CloseAppWhitelist.TryValidateName(
            new string('a', CloseAppWhitelist.MaxNameLength), out _));
}
