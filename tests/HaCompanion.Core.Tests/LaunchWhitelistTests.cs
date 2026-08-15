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

    [Theory]
    [InlineData(@"\\server\share\app.exe")] // UNC — remote share the attacker controls
    [InlineData(@"\\?\C:\Windows\System32\calc.exe")] // verbatim/device namespace
    [InlineData(@"\\.\C:\Windows\System32\calc.exe")]
    public void Unc_and_device_paths_are_rejected(string entry) =>
        Assert.False(LaunchWhitelist.TryValidateEntry(entry, out _));

    [Fact]
    public void Alternate_data_stream_suffix_is_rejected()
    {
        // C:\real.exe:evil.exe reports extension ".exe" but is an NTFS ADS — not launchable.
        Assert.False(LaunchWhitelist.TryValidateEntry(_exe + ":evil.exe", out _));
    }

    // ----- TryParseEntry: path + optional arguments (issue #17) -----

    [Fact]
    public void Plain_path_parses_with_empty_args()
    {
        Assert.True(LaunchWhitelist.TryParseEntry(_exe, out var path, out var args));
        Assert.Equal(_exe, path);
        Assert.Equal("", args);
    }

    [Fact]
    public void Quoted_path_with_args_parses()
    {
        Assert.True(LaunchWhitelist.TryParseEntry($"\"{_exe}\" -c -g \"United Kingdom\"",
            out var path, out var args));
        Assert.Equal(_exe, path);
        Assert.Equal("-c -g \"United Kingdom\"", args);
    }

    [Fact]
    public void Quoted_path_without_args_parses()
    {
        Assert.True(LaunchWhitelist.TryParseEntry($"\"{_exe}\"", out var path, out var args));
        Assert.Equal(_exe, path);
        Assert.Equal("", args);
    }

    [Fact]
    public void Unquoted_path_with_args_splits_at_the_exe_boundary()
    {
        Assert.True(LaunchWhitelist.TryParseEntry(_exe + " -c -g uk", out var path, out var args));
        Assert.Equal(_exe, path);
        Assert.Equal("-c -g uk", args);
    }

    [Fact]
    public void Exe_inside_a_directory_name_does_not_fool_the_split()
    {
        // A directory literally named "tools.exe files" — the first ".exe " boundary is
        // NOT the executable; only File.Exists on the real path decides.
        var sub = Path.Combine(_dir, "tools.exe files");
        Directory.CreateDirectory(sub);
        var inner = Path.Combine(sub, "app.exe");
        File.WriteAllText(inner, "");

        Assert.True(LaunchWhitelist.TryParseEntry(inner + " --flag", out var path, out var args));
        Assert.Equal(inner, path);
        Assert.Equal("--flag", args);

        Assert.True(LaunchWhitelist.TryParseEntry(inner, out path, out args));
        Assert.Equal(inner, path);
        Assert.Equal("", args);
    }

    [Fact]
    public void Control_characters_reject_the_whole_entry() =>
        Assert.False(LaunchWhitelist.TryParseEntry(_exe + " -flag\u0001evil", out _, out _));

    [Fact]
    public void Unterminated_quote_is_rejected() =>
        Assert.False(LaunchWhitelist.TryParseEntry("\"" + _exe, out _, out _));

    [Fact]
    public void Oversized_args_are_rejected() =>
        Assert.False(LaunchWhitelist.TryParseEntry(
            $"\"{_exe}\" {new string('a', LaunchWhitelist.MaxArgsLength + 1)}", out _, out _));

    [Theory]
    [InlineData(@"""\\server\share\app.exe"" -x")] // UNC stays rejected with args too
    [InlineData(@"\\server\share\app.exe -x")]
    public void Unc_paths_with_args_are_rejected(string entry) =>
        Assert.False(LaunchWhitelist.TryParseEntry(entry, out _, out _));

    [Fact]
    public void Ads_suffix_with_args_is_rejected() =>
        Assert.False(LaunchWhitelist.TryParseEntry($"\"{_exe}:evil.exe\" -x", out _, out _));

    [Fact]
    public void Canonical_form_round_trips()
    {
        Assert.Equal(_exe, LaunchWhitelist.CanonicalEntry(_exe, ""));
        var canonical = LaunchWhitelist.CanonicalEntry(_exe, "-c 1");
        Assert.Equal($"\"{_exe}\" -c 1", canonical);
        Assert.True(LaunchWhitelist.TryParseEntry(canonical, out var path, out var args));
        Assert.Equal(_exe, path);
        Assert.Equal("-c 1", args);
    }
}
