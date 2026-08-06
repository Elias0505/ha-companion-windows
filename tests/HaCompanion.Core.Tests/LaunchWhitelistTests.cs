// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class LaunchWhitelistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hac-wl-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly string _bat;

    public LaunchWhitelistTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "app.exe");
        _bat = Path.Combine(_dir, "app.bat");
        File.WriteAllText(_exe, "");
        File.WriteAllText(_bat, "");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Existing_absolute_exe_is_accepted_and_canonicalized()
    {
        var messy = Path.Combine(_dir, ".", "app.exe");
        Assert.True(LaunchWhitelist.TryValidateEntry(messy, out var full));
        Assert.Equal(_exe, full);
    }

    [Fact]
    public void Bare_name_is_rejected() =>
        Assert.False(LaunchWhitelist.TryValidateEntry("notepad", out _));

    [Fact]
    public void Relative_path_is_rejected() =>
        Assert.False(LaunchWhitelist.TryValidateEntry(Path.Combine("sub", "app.exe"), out _));

    [Fact]
    public void Non_exe_extension_is_rejected() =>
        Assert.False(LaunchWhitelist.TryValidateEntry(_bat, out _));

    [Fact]
    public void Missing_file_is_rejected() =>
        Assert.False(LaunchWhitelist.TryValidateEntry(Path.Combine(_dir, "gone.exe"), out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://example.com/app.exe")]
    public void Empty_and_non_path_entries_are_rejected(string? entry) =>
        Assert.False(LaunchWhitelist.TryValidateEntry(entry, out _));

    [Fact]
    public void Rejected_entry_yields_an_empty_path()
    {
        LaunchWhitelist.TryValidateEntry("notepad", out var full);
        Assert.Equal("", full);
    }
}
