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
}
