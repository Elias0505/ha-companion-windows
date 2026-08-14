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

    /// <summary>
    /// Forget an id again, so a later delivery carrying it counts as new.
    /// Used when the work the id stands for could NOT be accepted after all (queue full):
    /// keeping it would make the app suppress Home Assistant's redelivery of a command it
    /// never actually ran. The id stays in the ordering queue and simply ages out.
    /// </summary>
    public void Forget(string id)
    {
        lock (_gate)
        {
            if (!_set.Remove(id))
                return;
            // Drop it from the ordering queue too (set and queue always hold the same ids).
            // Leaving it there meant a LATER TryAdd of the same id enqueued a second entry, and
            // when the stale first one aged out it removed the live id from the set — so a third
            // delivery counted as new and the command (e.g. command_shutdown) ran a second time,
            // the very thing this class prevents.
            var kept = _order.Where(x => !string.Equals(x, id, StringComparison.Ordinal)).ToArray();
            _order.Clear();
            foreach (var x in kept)
                _order.Enqueue(x);
        }
    }
}
