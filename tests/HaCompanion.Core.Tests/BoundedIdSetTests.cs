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

    [Fact]
    public void A_forgotten_id_counts_as_new_again()
    {
        // Used when a delivery could NOT be queued after all: keeping the id would make the app
        // suppress Home Assistant's redelivery of a command it never ran.
        var set = new BoundedIdSet(8);
        Assert.True(set.TryAdd("cmd-1"));
        Assert.False(set.TryAdd("cmd-1"));
        set.Forget("cmd-1");
        Assert.True(set.TryAdd("cmd-1"));
    }

    [Fact]
    public void Forgetting_an_unknown_id_is_harmless()
    {
        var set = new BoundedIdSet(4);
        set.Forget("never-seen");
        Assert.True(set.TryAdd("never-seen"));
    }

    [Fact]
    public void Forgetting_then_re_adding_does_not_evict_the_live_id()
    {
        // Forget used to leave the id in the ordering queue. Re-adding it enqueued a SECOND
        // entry, and when the stale one aged out it removed the live id from the set — so the
        // next redelivery counted as new and the command ran twice.
        var set = new BoundedIdSet(3);
        Assert.True(set.TryAdd("a"));
        set.Forget("a");
        Assert.True(set.TryAdd("a"));   // re-added (HA redelivered it)
        Assert.True(set.TryAdd("b"));
        Assert.True(set.TryAdd("c"));   // would evict the stale "a" entry in the buggy version
        Assert.False(set.TryAdd("a"));  // must still be remembered
    }
}
