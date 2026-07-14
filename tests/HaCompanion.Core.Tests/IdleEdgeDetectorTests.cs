// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class IdleEdgeDetectorTests
{
    [Fact]
    public void Threshold_fires_once_when_crossed()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 5 });

        Assert.Equal(IdleEdges.None, d.Advance(0));
        Assert.Equal(IdleEdges.None, d.Advance(4));
        var edge = d.Advance(5);
        Assert.Equal(new[] { 5 }, edge.Started);
        Assert.False(edge.Ended);
        // stays silent while idle continues
        Assert.Equal(IdleEdges.None, d.Advance(6));
        Assert.Equal(IdleEdges.None, d.Advance(90));
    }

    [Fact]
    public void Multiple_thresholds_fire_at_their_own_minute()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 5, 15 });

        Assert.Equal(new[] { 5 }, d.Advance(5).Started);
        Assert.Equal(IdleEdges.None, d.Advance(10));
        Assert.Equal(new[] { 15 }, d.Advance(15).Started);
    }

    [Fact]
    public void Activity_resume_fires_one_end_and_rearms()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 5 });

        d.Advance(5);
        var resume = d.Advance(0);
        Assert.True(resume.Ended);
        Assert.Empty(resume.Started);
        // re-armed: next idle period fires again
        Assert.Equal(new[] { 5 }, d.Advance(5).Started);
    }

    [Fact]
    public void No_end_without_a_prior_start()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 30 });

        d.Advance(10);
        var resume = d.Advance(0); // was idle, but below every threshold
        Assert.False(resume.Ended);
    }

    [Fact]
    public void Jumping_past_a_threshold_still_fires_it()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 5 });
        Assert.Equal(new[] { 5 }, d.Advance(12).Started);
    }

    [Fact]
    public void SetThresholds_keeps_fired_state_of_surviving_thresholds()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 5, 15 });
        d.Advance(6); // 5 fired

        d.SetThresholds(new[] { 5, 10 }); // 15 removed, 10 added mid-idle
        var edge = d.Advance(11);
        Assert.Equal(new[] { 10 }, edge.Started); // 5 must NOT re-fire
    }

    [Fact]
    public void Invalid_thresholds_are_ignored()
    {
        var d = new IdleEdgeDetector();
        d.SetThresholds(new[] { 0, -3, 5 });
        Assert.Equal(new[] { 5 }, d.Advance(9).Started);
    }
}
