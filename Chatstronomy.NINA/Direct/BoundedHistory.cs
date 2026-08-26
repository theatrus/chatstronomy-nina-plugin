namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Thread-safe insertion-ordered history used for N.I.N.A. callbacks. The
/// oldest item is discarded at capacity so a long imaging session cannot
/// grow the plugin's memory use without bound.
/// </summary>
internal sealed class BoundedHistory<T>
{
    private readonly object gate = new();
    private readonly Queue<BoundedHistoryEntry<T>> items;
    private long sequence;

    internal BoundedHistory(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        items = new Queue<BoundedHistoryEntry<T>>(capacity);
    }

    internal int Capacity { get; }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return items.Count;
            }
        }
    }

    internal long Add(T item)
    {
        lock (gate)
        {
            if (items.Count == Capacity)
            {
                items.Dequeue();
            }
            var next = ++sequence;
            items.Enqueue(new BoundedHistoryEntry<T>(next, item));
            return next;
        }
    }

    internal IReadOnlyList<T> Snapshot()
    {
        lock (gate)
        {
            return items.Select(entry => entry.Item).ToArray();
        }
    }

    /// <summary>
    /// Returns the same bounded snapshot with an internal monotonic sequence.
    /// The sequence is never serialized; it exists only so a replacement
    /// Direct session can replay items that arrived during reconnection.
    /// </summary>
    internal IReadOnlyList<BoundedHistoryEntry<T>> SnapshotEntries()
    {
        lock (gate)
        {
            return items.ToArray();
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            items.Clear();
        }
    }

    internal bool TryGetAt(int index, out T? item)
    {
        lock (gate)
        {
            if (index < 0 || index >= items.Count)
            {
                item = default;
                return false;
            }

            item = items.ElementAt(index).Item;
            return true;
        }
    }
}

internal sealed record BoundedHistoryEntry<T>(long Sequence, T Item);
