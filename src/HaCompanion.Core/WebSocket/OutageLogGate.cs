// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.WebSocket;

/// <summary>
/// Once-per-outage logging state: during a prolonged outage the reconnect loop fails every
/// few seconds — without this gate each failure logs a warning (spam), and the recovery is
/// never logged at all. Exactly one "lost" per outage, exactly one "restored" per recovery.
/// </summary>
public sealed class OutageLogGate
{
    private bool _outageLogged;

    /// <summary>True only for the FIRST failure of an outage — log the warning then.</summary>
    public bool OnFailure()
    {
        if (_outageLogged)
            return false;
        _outageLogged = true;
        return true;
    }

    /// <summary>True only if an outage was in progress — log the recovery then.</summary>
    public bool OnRestored()
    {
        if (!_outageLogged)
            return false;
        _outageLogged = false;
        return true;
    }
}
