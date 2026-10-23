using System.Text.Json;

namespace Chatstronomy.NINA.Direct;

/// <summary>Small in-process state, independent of chat delivery and history eviction.</summary>
internal sealed class DirectObservationState
{
    private readonly object gate = new();
    private Guid epoch = Guid.NewGuid();
    private ulong revision;
    private object? owner;
    private ObservationTarget? target;
    private ulong? count;
    private DateTimeOffset? countAt;
    private readonly HashSet<DateTimeOffset> completed = [];
    private ObservationTemperature? focuser;
    private ObservationTemperature? external;
    private DateTimeOffset? focuserAt;
    private DateTimeOffset? externalAt;

    internal void Reset()
    {
        lock (gate)
        {
            epoch = Guid.NewGuid();
            revision = 0;
            owner = null;
            target = null;
            count = null;
            countAt = null;
            completed.Clear();
            focuser = external = null;
            focuserAt = externalAt = null;
        }
    }

    internal void BeginTarget(object identity, string? name, DateTimeOffset at, bool complete, bool force = false)
    {
        lock (gate)
        {
            name = Label(name);
            if (name is null) { ClearTargetLocked(); return; }
            if (!force && ReferenceEquals(owner, identity) && target?.Name == name) return;
            owner = identity;
            target = new(Guid.NewGuid(), name, complete ? at.ToUniversalTime() : null);
            count = complete ? 0UL : null;
            countAt = complete ? at.ToUniversalTime() : null;
            completed.Clear();
            revision++;
        }
    }

    internal void EndTarget(object? identity = null)
    {
        lock (gate)
            if (identity is null || ReferenceEquals(owner, identity)) ClearTargetLocked();
    }

    private void ClearTargetLocked()
    {
        if (target is not null) revision++;
        owner = null;
        target = null;
        count = null;
        countAt = null;
        completed.Clear();
    }

    internal void Temperature(bool isFocuser, double? celsius, DateTimeOffset at)
    {
        lock (gate)
        {
            // Keep the watermark even when a disconnect/invalid reading clears
            // the value, so a delayed capture cannot resurrect that sensor.
            var previousAt = isFocuser ? focuserAt : externalAt;
            if (previousAt > at) return;
            var value = celsius is { } number && double.IsFinite(number)
                ? new ObservationTemperature(number, at.ToUniversalTime(), at.ToUniversalTime().AddMinutes(2)) : null;
            if (isFocuser) { focuser = value; focuserAt = at; }
            else { external = value; externalAt = at; }
            revision++;
        }
    }

    internal void ImageSaved(string? name, string? imageType, DateTimeOffset? exposureStart, DateTimeOffset savedAt)
    {
        lock (gate)
        {
            if (target is null || count is null) return;
            if (string.IsNullOrWhiteSpace(imageType)) { count = null; revision++; return; }
            if (!string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) return;
            // A delayed save from another target must not enter the current visit.
            // A missing association invalidates completeness instead of inventing a total.
            if (exposureStart is null || string.IsNullOrWhiteSpace(name)) { count = null; revision++; return; }
            if (exposureStart < target.StartedAt) return;
            if (name != target.Name || exposureStart > savedAt) { count = null; revision++; return; }
            // Acquisition is serial per N.I.N.A. profile. Equal starts are
            // duplicates; distinct out-of-order saves still count without
            // rewinding the latest included completion time.
            if (!completed.Add(exposureStart.Value)) return;
            if (completed.Count > 100_000) { count = null; completed.Clear(); revision++; return; }
            count++;
            var completedAt = savedAt.ToUniversalTime();
            if (countAt is null || completedAt > countAt) countAt = completedAt;
            revision++;
        }
    }

    internal JsonElement Snapshot(DateTimeOffset now, bool enabled, bool targets, bool images, bool focus, bool weather)
    {
        lock (gate)
        {
            var active = enabled && targets ? target : null;
            var state = new ObservationSnapshot(epoch, revision, now.ToUniversalTime(), enabled,
                active, new(
                    enabled && focus && focuser?.ValidUntil > now ? focuser : null,
                    enabled && weather && external?.ValidUntil > now ? external : null),
                active is not null && images && count is { } value && countAt is { } at
                    ? new ObservationCount(value, "LIGHT", "successfully_saved", at) : null);
            return JsonSerializer.SerializeToElement(state, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            });
        }
    }

    private static string? Label(string? name)
    {
        var value = new string((name ?? "").Where(ch => !char.IsControl(ch)
            && ch is not (>= '\u202a' and <= '\u202e') and not (>= '\u2066' and <= '\u2069')).Take(120).ToArray()).Trim();
        return value.Length == 0 || value == "Sequential Instruction Set" ? null : value;
    }
}

internal sealed record ObservationTarget(Guid RunId, string Name, DateTimeOffset? StartedAt);
internal sealed record ObservationTemperature(double Celsius, DateTimeOffset ObservedAt, DateTimeOffset ValidUntil);
internal sealed record ObservationTemperatures(ObservationTemperature? Focuser, ObservationTemperature? External);
internal sealed record ObservationCount(ulong Count, string ImageType, string Basis, DateTimeOffset AsOf);
internal sealed record ObservationSnapshot(Guid StateEpoch, ulong Revision, DateTimeOffset ObservedAt,
    bool Available, ObservationTarget? Target, ObservationTemperatures Temperatures, ObservationCount? CompletedExposures);
