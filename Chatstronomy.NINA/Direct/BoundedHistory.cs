namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Thread-safe insertion-ordered history used for N.I.N.A. callbacks. The
/// oldest item is discarded at capacity so a long imaging session cannot
/// grow the plugin's memory use without bound.
/// </summary>
internal sealed class BoundedHistory<T>
{
    private readonly object gate = new();
    private readonly Queue<T> items;

    internal BoundedHistory(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        items = new Queue<T>(capacity);
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

    internal void Add(T item)
    {
        lock (gate)
        {
            if (items.Count == Capacity)
            {
                items.Dequeue();
            }
            items.Enqueue(item);
        }
    }

    internal IReadOnlyList<T> Snapshot()
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

            item = items.ElementAt(index);
            return true;
        }
    }
}
