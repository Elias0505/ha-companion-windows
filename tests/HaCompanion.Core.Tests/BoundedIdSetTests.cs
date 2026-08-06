// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class BoundedIdSetTests
{
    [Fact]
    public void First_add_wins_second_is_a_duplicate()
    {
        var set = new BoundedIdSet(8);
        Assert.True(set.TryAdd("a"));
        Assert.False(set.TryAdd("a"));
    }

    [Fact]
    public void Distinct_ids_within_capacity_are_all_recorded()
    {
        var set = new BoundedIdSet(3);
        Assert.True(set.TryAdd("a"));
        Assert.True(set.TryAdd("b"));
        Assert.True(set.TryAdd("c"));
        Assert.False(set.TryAdd("b"));
    }

    [Fact]
    public void Oldest_id_is_forgotten_beyond_capacity()
    {
        var set = new BoundedIdSet(3);
        set.TryAdd("a");
        set.TryAdd("b");
        set.TryAdd("c");
        set.TryAdd("d"); // evicts "a"

        Assert.True(set.TryAdd("a"));  // forgotten → addable again
        Assert.False(set.TryAdd("c")); // still remembered
        Assert.False(set.TryAdd("d"));
    }

    [Fact]
    public void Parallel_adds_of_the_same_id_admit_exactly_one()
    {
        var set = new BoundedIdSet(128);
        var admitted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (set.TryAdd("same-id"))
                Interlocked.Increment(ref admitted);
        });
        Assert.Equal(1, admitted);
    }
}
