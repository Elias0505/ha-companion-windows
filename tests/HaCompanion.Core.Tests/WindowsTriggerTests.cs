// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class WindowsTriggerTests
{
    [Fact]
    public void Every_trigger_round_trips_through_its_key()
    {
        foreach (var trigger in WindowsTriggers.All)
        {
            var key = WindowsTriggers.ToKey(trigger);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(WindowsTriggers.TryParse(key, out var parsed));
            Assert.Equal(trigger, parsed);
        }
    }

    [Fact]
    public void All_contains_every_enum_member_exactly_once()
    {
        var enumMembers = (WindowsTrigger[])Enum.GetValues(typeof(WindowsTrigger));
        Assert.Equal(enumMembers.Length, WindowsTriggers.All.Count);
        Assert.Equal(enumMembers.Length, WindowsTriggers.All.Distinct().Count());
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = WindowsTriggers.All.Select(WindowsTriggers.ToKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LOCK")]     // keys are case-sensitive by contract
    [InlineData("unknown")]
    public void TryParse_rejects_unknown_keys(string? key)
    {
        Assert.False(WindowsTriggers.TryParse(key, out _));
    }

    [Theory]
    [InlineData(WindowsTrigger.IdleStart, TriggerParamKind.Minutes)]
    [InlineData(WindowsTrigger.IdleEnd, TriggerParamKind.Minutes)]
    [InlineData(WindowsTrigger.AppStart, TriggerParamKind.ProcessName)]
    [InlineData(WindowsTrigger.AppStop, TriggerParamKind.ProcessName)]
    [InlineData(WindowsTrigger.Lock, TriggerParamKind.None)]
    [InlineData(WindowsTrigger.Startup, TriggerParamKind.None)]
    [InlineData(WindowsTrigger.MicOn, TriggerParamKind.None)]
    public void ParamKind_matches_the_trigger(WindowsTrigger trigger, TriggerParamKind expected) =>
        Assert.Equal(expected, WindowsTriggers.ParamKind(trigger));

    [Theory]
    [InlineData(WindowsTrigger.Lock, true)]
    [InlineData(WindowsTrigger.MicOn, true)]
    [InlineData(WindowsTrigger.FullscreenEnd, true)]
    [InlineData(WindowsTrigger.Startup, false)]
    [InlineData(WindowsTrigger.Resume, false)]
    [InlineData(WindowsTrigger.Shutdown, false)]
    public void IsStateLike_separates_states_from_pulses(WindowsTrigger trigger, bool expected) =>
        Assert.Equal(expected, WindowsTriggers.IsStateLike(trigger));
}
