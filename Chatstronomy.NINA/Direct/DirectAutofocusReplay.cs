using System.Diagnostics;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Notification receipts and bounded retries, independent of report caching.
/// All access is serialized by the provider's history-generation gate. These
/// local sequence numbers and monotonic timestamps never cross the transport.
/// </summary>
internal sealed class DirectAutofocusReplay
{
    internal static readonly TimeSpan MaximumReplayAge = TimeSpan.FromMinutes(10);
    private readonly Dictionary<long, Receipt> receipts = [];
    private readonly Func<long> timestamp;
    private readonly int capacity;
    private long retiredThrough;

    internal DirectAutofocusReplay(int capacity, Func<long>? timestamp = null)
    {
        this.capacity = capacity;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    internal long CaptureTimestamp() => timestamp();

    internal void Observe(long sequence, string eventName,
        IReadOnlyDictionary<string, object?> item, long? capturedAt = null)
    {
        if (eventName == "AUTOFOCUS-STARTING")
        {
            retiredThrough = timestamp();
            foreach (var receipt in receipts.Values)
            {
                receipt.Retired = true;
            }
        }
        if (eventName != "AUTOFOCUS-FINISHED")
        {
            return;
        }
        foreach (var old in receipts.Keys.Where(key => key <= sequence - capacity).ToArray())
        {
            receipts.Remove(old);
        }
        var captured = capturedAt ?? timestamp();
        var reportTimestamp = item.TryGetValue("ReportTimestamp", out var value)
            ? value switch
            {
                DateTimeOffset offset => offset,
                DateTime dateTime => new DateTimeOffset(dateTime),
                _ => (DateTimeOffset?)null,
            }
            : null;
        receipts[sequence] = new(reportTimestamp, captured,
            capturedAt.HasValue && captured < retiredThrough);
    }

    internal bool Acknowledge(DateTimeOffset reportTimestamp)
    {
        var acknowledged = false;
        foreach (var receipt in receipts.Values)
        {
            if (receipt.ReportTimestamp == reportTimestamp && !receipt.Retired)
            {
                receipt.Retired = true;
                acknowledged = true;
            }
        }
        return acknowledged;
    }

    internal bool CanReplay(long sequence) =>
        receipts.TryGetValue(sequence, out var receipt)
        && !receipt.Retired
        && Stopwatch.GetElapsedTime(receipt.CapturedAt, timestamp()) < MaximumReplayAge;

    internal HashSet<long> RetiredSequences() =>
        receipts.Keys.Where(sequence => !CanReplay(sequence)).ToHashSet();

    internal void Clear()
    {
        receipts.Clear();
        retiredThrough = 0;
    }

    private sealed class Receipt(DateTimeOffset? reportTimestamp, long capturedAt, bool retired)
    {
        internal DateTimeOffset? ReportTimestamp { get; } = reportTimestamp;
        internal long CapturedAt { get; } = capturedAt;
        internal bool Retired { get; set; } = retired;
    }
}
