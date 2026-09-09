using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;

namespace Chatstronomy.NINA.Direct;

/// Counts dropped diagnostics without retaining their contents. Fixed slots
/// bound memory and atomic increments also cover limiter-lock contention.
internal sealed class DirectEventElisionCounters(DirectEventDeliveryOptions policy)
{
    private readonly string epoch = Guid.NewGuid().ToString("D");
    private readonly Counter[] counters =
    [
        new("SEQUENCE-ENTITY-FAILED"),
        new("IMAGE-SAVE-FAILED"),
        new("ERROR-AF"),
        new("ERROR-PLATESOLVE"),
        new("CAMERA-DOWNLOAD-TIMEOUT"),
        new("NINA-NOTIFICATION"),
        new("NINA-LOG", "ERROR"),
        new("NINA-LOG", "WARNING"),
        new("NINA-LOG", "INFORMATION"),
        new("NINA-LOG", "DEBUG"),
        new("NINA-LOG", "TRACE"),
    ];

    internal void Record(
        string eventName,
        DirectEventDeliveryOptions current,
        (string Name, object? Value)[] details)
    {
        if (!ReferenceEquals(policy, current))
        {
            return;
        }
        string? level = null;
        var requiredScopes = 0;
        foreach (var (name, value) in details)
        {
            if (name == "Level" && value is string text && eventName == "NINA-LOG")
            {
                level = NormalizeLogLevel(text);
            }
            if (name == "ChatstronomyRequiredDeliveryScopes" && value is int scopes)
            {
                requiredScopes = scopes;
            }
        }
        foreach (var counter in counters)
        {
            if (counter.Event == eventName && counter.Level == level
                && Enabled(counter, current)
                && (eventName != "SEQUENCE-ENTITY-FAILED"
                    || NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(requiredScopes, current)))
            {
                // Publish the complete permission requirement before Count.
                // If mixed sequence-item scopes share this slot, a query must
                // permit every contributing scope or omit the whole count.
                Interlocked.Or(ref counter.RequiredScopes, requiredScopes);
                counter.Increment();
                return;
            }
        }
    }

    internal IReadOnlyList<DirectElidedEvent> Snapshot(DirectEventDeliveryOptions current)
    {
        if (!ReferenceEquals(policy, current))
        {
            return [];
        }
        var result = new List<DirectElidedEvent>(counters.Length);
        foreach (var counter in counters)
        {
            var count = Volatile.Read(ref counter.Count);
            if (count > 0 && Enabled(counter, current))
            {
                result.Add(new(counter.Event, counter.Level, (ulong)count, epoch));
            }
        }
        return result;
    }

    private static bool Enabled(Counter counter, DirectEventDeliveryOptions current) =>
        counter.Event == "NINA-LOG"
            ? counter.Level is not null && current.ShouldSendLogLevel(counter.Level)
            : current.ShouldSendEvent(counter.Event)
                && (counter.Event != "SEQUENCE-ENTITY-FAILED"
                    || NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(
                        Volatile.Read(ref counter.RequiredScopes), current));

    private static string? NormalizeLogLevel(string level) => level.Trim().ToUpperInvariant() switch
    {
        "ERROR" or "FATAL" or "CRITICAL" => "ERROR",
        "WARN" or "WARNING" => "WARNING",
        "INFO" or "INFORMATION" => "INFORMATION",
        "DEBUG" => "DEBUG",
        "TRACE" or "VERBOSE" => "TRACE",
        _ => null,
    };

    private sealed class Counter(string eventName, string? level = null)
    {
        internal string Event { get; } = eventName;
        internal string? Level { get; } = level;
        internal int RequiredScopes;
        internal long Count;

        internal void Increment()
        {
            var observed = Volatile.Read(ref Count);
            // The signed atomic storage saturates before overflow; the wire
            // value remains an unsigned count. No callback waits on a lock.
            while (observed != long.MaxValue)
            {
                var previous = Interlocked.CompareExchange(ref Count, observed + 1, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }
}
