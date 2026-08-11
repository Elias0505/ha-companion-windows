// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class PcCommandsTests
{
    [Theory]
    [InlineData("command_lock", PcCommand.Lock)]
    [InlineData("command_sleep", PcCommand.Sleep)]
    [InlineData("command_shutdown", PcCommand.Shutdown)]
    [InlineData("command_monitor_off", PcCommand.MonitorOff)]
    [InlineData("command_volume", PcCommand.Volume)]
    [InlineData("command_mute", PcCommand.Mute)]
    [InlineData("command_launch", PcCommand.Launch)]
    public void Known_commands_parse_and_round_trip(string key, PcCommand expected)
    {
        Assert.True(PcCommands.TryParse(key, out var cmd));
        Assert.Equal(expected, cmd);
        Assert.Equal(key, PcCommands.ToKey(cmd));
    }

    [Theory]
    [InlineData("Waschmaschine fertig")]
    [InlineData("command_unknown")]
    [InlineData("COMMAND_LOCK")] // case-sensitive by contract
    [InlineData("")]
    public void Ordinary_messages_are_not_commands(string message) =>
        Assert.False(PcCommands.TryParse(message, out _));

    [Theory]
    [InlineData(PcCommand.Shutdown, true)]
    [InlineData(PcCommand.Sleep, true)]
    [InlineData(PcCommand.Launch, true)]
    [InlineData(PcCommand.Lock, false)]
    [InlineData(PcCommand.MonitorOff, false)]
    [InlineData(PcCommand.Volume, false)]
    [InlineData(PcCommand.Mute, false)]
    public void Critical_commands_are_flagged(PcCommand cmd, bool critical) =>
        Assert.Equal(critical, PcCommands.IsCritical(cmd));

    [Theory]
    [InlineData(PcCommand.Volume, "level")]
    [InlineData(PcCommand.Launch, "app")]
    [InlineData(PcCommand.Lock, null)]
    public void Param_fields_are_declared(PcCommand cmd, string? field) =>
        Assert.Equal(field, PcCommands.ParamField(cmd));

    [Fact]
    public void Android_style_volume_alias_parses_to_volume()
    {
        // The HA docs' Android examples use command_volume_level — copy-paste must work.
        Assert.True(PcCommands.TryParse("command_volume_level", out var cmd));
        Assert.Equal(PcCommand.Volume, cmd);
        // The canonical key stays command_volume (history/i18n round-trips through it).
        Assert.Equal("command_volume", PcCommands.ToKey(cmd));
    }

    [Theory]
    [InlineData("40", 40)]
    [InlineData(" 55 ", 55)]
    [InlineData("40.6", 41)]   // decimals round
    [InlineData("40,4", 40)]   // decimal comma accepted
    [InlineData("85%", 85)]    // percent suffix stripped
    [InlineData("120", 100)]   // clamps instead of failing
    [InlineData("-5", 0)]
    public void Volume_levels_parse_leniently(string raw, int expected)
    {
        Assert.True(PcCommands.TryParseLevel(raw, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("NaN")]
    public void Garbage_levels_are_rejected(string? raw) =>
        Assert.False(PcCommands.TryParseLevel(raw, out _));
}
