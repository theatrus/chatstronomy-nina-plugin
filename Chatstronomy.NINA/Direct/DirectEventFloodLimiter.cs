using System.Diagnostics;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Drops diagnostic chatter before it enters history or either transport.
/// It never waits, schedules work, or retains message text. One instance
/// belongs to one profile capture session, independent of reconnects.
/// </summary>
internal sealed class DirectEventFloodLimiter
{
    private const int RecentCapacity = 128;
    private const int MaximumFingerprintFieldCharacters = 1_024;
    private const double DuplicateCooldownSeconds = 60;
    private readonly object gate = new();
    private readonly Func<long> timestamp;
    private readonly RecentFingerprint[] recent = new RecentFingerprint[RecentCapacity];
    private int recentCount;
    private int nextRecent;
    private double diagnosticTokens = 5;
    private double ordinaryTokens = 10;
    private long lastTimestamp;

    internal DirectEventFloodLimiter(Func<long>? timestamp = null)
    {
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        lastTimestamp = this.timestamp();
    }

    internal bool TryAdmit(string eventName, params (string Name, object? Value)[] details)
    {
        var category = Classify(eventName, details);
        if (category == Category.Unlimited)
        {
            return true;
        }
        // Chat forwarding must not block N.I.N.A.'s sequence or popup thread,
        // including when several device failures arrive concurrently.
        if (!Monitor.TryEnter(gate))
        {
            return false;
        }
        try
        {
            var now = Math.Max(lastTimestamp, timestamp());
            var elapsed = (now - lastTimestamp) / (double)Stopwatch.Frequency;
            diagnosticTokens = Math.Min(5, diagnosticTokens + elapsed / 12);
            ordinaryTokens = Math.Min(10, ordinaryTokens + elapsed / 6);
            lastTimestamp = now;
            ref var tokens = ref (category == Category.Diagnostic
                ? ref diagnosticTokens
                : ref ordinaryTokens);
            if (tokens < 1)
            {
                return false;
            }

            var fingerprint = Fingerprint(eventName, details);
            for (var index = 0; index < recentCount; index++)
            {
                var previous = recent[index];
                if (previous.Hash == fingerprint
                    && (now - previous.Timestamp) / (double)Stopwatch.Frequency
                        < DuplicateCooldownSeconds)
                {
                    return false;
                }
            }
            tokens -= 1;
            recent[nextRecent] = new(fingerprint, now);
            nextRecent = (nextRecent + 1) % RecentCapacity;
            recentCount = Math.Min(recentCount + 1, RecentCapacity);
            return true;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    private static Category Classify(string eventName, (string Name, object? Value)[] details)
    {
        // Accepted remote commands must always retain their terminal outcome.
        if (eventName == "CHATSTRONOMY-COMMAND-FAILED")
        {
            return Category.Unlimited;
        }
        if (eventName is "NINA-LOG" or "NINA-NOTIFICATION")
        {
            foreach (var (name, value) in details)
            {
                if (name == "Level" && value is string level)
                {
                    level = level.Trim();
                    if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("FATAL", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("WARN", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
                    {
                        return Category.Diagnostic;
                    }
                }
            }
            return Category.Ordinary;
        }
        return eventName.StartsWith("ERROR-", StringComparison.Ordinal)
            || eventName.EndsWith("-FAILED", StringComparison.Ordinal)
            || eventName.EndsWith("-TIMEOUT", StringComparison.Ordinal)
                ? Category.Diagnostic
                : Category.Unlimited;
    }

    private static ulong Fingerprint(string eventName, (string Name, object? Value)[] details)
    {
        // FNV-1a over bounded diagnostic fields. Timestamps, coordinates, line
        // numbers and other volatile telemetry cannot defeat deduplication.
        // Only the hash is retained; disabled categories never reach here.
        const ulong offset = 14695981039346656037;
        var hash = Append(offset, eventName);
        foreach (var (name, value) in details)
        {
            if (name is "Entity" or "EntityType" or "Error" or "Message"
                    or "Level" or "Header" or "Source" or "Member" or "Stage"
                && value is string text)
            {
                hash = Append(Append(hash, name), text);
            }
        }
        return hash;
    }

    private static ulong Append(ulong hash, string text)
    {
        const ulong prime = 1099511628211;
        foreach (var character in text.AsSpan(0, Math.Min(text.Length, MaximumFingerprintFieldCharacters)))
        {
            hash = unchecked((hash ^ character) * prime);
        }
        return unchecked((hash ^ 0xffff) * prime);
    }

    private enum Category { Unlimited, Diagnostic, Ordinary }
    private readonly record struct RecentFingerprint(ulong Hash, long Timestamp);
}
