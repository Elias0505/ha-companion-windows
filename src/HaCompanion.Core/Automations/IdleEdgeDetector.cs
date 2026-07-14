// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Automations;

/// <summary>Thresholds newly crossed upward plus whether activity resumed.</summary>
public readonly record struct IdleEdges(IReadOnlyList<int> Started, bool Ended)
{
    public static readonly IdleEdges None = new(Array.Empty<int>(), false);
}

/// <summary>
/// Turns the monitor's raw "minutes idle" stream into per-threshold edges: each configured
/// threshold fires exactly once per idle period when crossed upward (idle_start), and one
/// idle_end fires when input resumes after any threshold had fired. The monitor stays
/// rule-agnostic; the engine owns one instance and feeds it the enabled rules' thresholds.
/// </summary>
public sealed class IdleEdgeDetector
{
    private readonly HashSet<int> _thresholds = new();
    private readonly HashSet<int> _fired = new();
    private int _lastMinutes;

    /// <summary>Replace the watched thresholds (distinct, minutes). Mid-idle state is kept
    /// for thresholds that remain; new thresholds already below the current idle time fire
    /// on the next advance (the user just created the rule — firing is what they expect).</summary>
    public void SetThresholds(IEnumerable<int> minutes)
    {
        _thresholds.Clear();
        foreach (var m in minutes)
            if (m >= 1)
                _thresholds.Add(m);
        _fired.RemoveWhere(f => !_thresholds.Contains(f));
    }

    public IdleEdges Advance(int idleMinutes)
    {
        List<int>? started = null;
        var ended = false;

        if (idleMinutes < _lastMinutes)
        {
            // Input resumed (the counter dropped). One idle_end per idle period, and only
            // if some threshold actually announced the period.
            ended = _fired.Count > 0;
            _fired.Clear();
        }

        foreach (var threshold in _thresholds)
        {
            if (idleMinutes >= threshold && _fired.Add(threshold))
                (started ??= new List<int>()).Add(threshold);
        }

        _lastMinutes = idleMinutes;
        return started is null && !ended
            ? IdleEdges.None
            : new IdleEdges((IReadOnlyList<int>?)started ?? Array.Empty<int>(), ended);
    }
}
