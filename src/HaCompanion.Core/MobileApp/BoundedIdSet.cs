// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.MobileApp;

/// <summary>
/// Remembers the last N ids (thread-safe). Used to suppress re-execution of HA push
/// redeliveries: HA re-sends a delivery whose confirm never arrived, and the second
/// copy carries the same <c>hass_confirm_id</c>.
/// </summary>
public sealed class BoundedIdSet
{
    private readonly object _gate = new();
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly int _capacity;

    public BoundedIdSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>True when the id is new (now recorded); false when it was seen within the last N ids.</summary>
    public bool TryAdd(string id)
    {
        lock (_gate)
        {
            if (!_set.Add(id))
                return false;
            _order.Enqueue(id);
            if (_order.Count > _capacity)
                _set.Remove(_order.Dequeue());
            return true;
        }
    }
}
