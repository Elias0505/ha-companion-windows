// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class ScheduleSpecTests
{
    [Fact]
    public void Parses_time_and_weekdays()
    {
        Assert.True(ScheduleSpec.TryParse("07:00;12345", out var spec));
        Assert.Equal(new TimeOnly(7, 0), spec.Time);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, spec.Days);
    }

    [Fact]
    public void Empty_day_set_means_every_day()
    {
        Assert.True(ScheduleSpec.TryParse("22:30;", out var spec));
        Assert.Empty(spec.Days);
        Assert.True(ScheduleSpec.TryParse("22:30", out var spec2)); // no ';' at all
        Assert.Empty(spec2.Days);
    }

    [Theory]
    [InlineData("")]
    [InlineData("7:00;1")]     // hour must be zero-padded HH:mm
    [InlineData("25:00;1")]
    [InlineData("07:00;8")]    // weekday out of 1..7
    [InlineData("07:00;0")]
    [InlineData("nope")]
    public void Rejects_malformed(string param)
    {
        Assert.False(ScheduleSpec.TryParse(param, out _));
    }

    [Fact]
    public void Roundtrips_through_ToParam()
    {
        Assert.True(ScheduleSpec.TryParse("06:05;246", out var spec));
        Assert.Equal("06:05;246", spec.ToParam());
    }

    [Fact]
    public void Duplicate_days_are_collapsed_and_sorted()
    {
        Assert.True(ScheduleSpec.TryParse("09:00;5115", out var spec));
        Assert.Equal(new[] { 1, 5 }, spec.Days);
    }

    [Fact]
    public void Matches_only_on_the_exact_minute_and_an_allowed_weekday()
    {
        // 2026-07-16 is a Thursday (ISO day 4).
        Assert.True(ScheduleSpec.TryParse("07:00;12345", out var weekday));
        Assert.True(weekday.Matches(new DateTime(2026, 7, 16, 7, 0, 30)));   // Thu 07:00
        Assert.False(weekday.Matches(new DateTime(2026, 7, 16, 7, 1, 0)));   // 07:01
        Assert.False(weekday.Matches(new DateTime(2026, 7, 18, 7, 0, 0)));   // Saturday

        Assert.True(ScheduleSpec.TryParse("07:00;", out var everyDay));
        Assert.True(everyDay.Matches(new DateTime(2026, 7, 18, 7, 0, 0)));   // Saturday allowed
    }
}
