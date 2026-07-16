// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.WebSocket;
using Xunit;

namespace HaCompanion.Core.Tests;

public class OutageLogGateTests
{
    [Fact]
    public void Warns_exactly_once_per_outage()
    {
        var gate = new OutageLogGate();
        Assert.True(gate.OnFailure());   // first failure of the outage -> log
        Assert.False(gate.OnFailure());  // every retry during the same outage -> silent
        Assert.False(gate.OnFailure());
    }

    [Fact]
    public void Logs_restore_only_after_an_outage()
    {
        var gate = new OutageLogGate();
        Assert.False(gate.OnRestored()); // healthy startup: nothing to restore from

        gate.OnFailure();
        Assert.True(gate.OnRestored());  // recovery after an outage -> log once
        Assert.False(gate.OnRestored()); // repeated Connected events stay silent
    }

    [Fact]
    public void Rearms_for_the_next_outage()
    {
        var gate = new OutageLogGate();
        gate.OnFailure();
        gate.OnRestored();

        Assert.True(gate.OnFailure());   // a NEW outage warns again
        Assert.True(gate.OnRestored());
    }
}
