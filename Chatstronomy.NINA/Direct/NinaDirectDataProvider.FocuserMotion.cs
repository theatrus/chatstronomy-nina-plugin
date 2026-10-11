using NINA.Equipment.Equipment.MyFocuser;

namespace Chatstronomy.NINA.Direct;

internal sealed partial class NinaDirectDataProvider
{
    private readonly object focuserMotionGate = new();
    private readonly DirectFocuserMotionTracker focuserMotionTracker = new();
    // Retain identity weakly, never a mutable object as a position baseline.
    private WeakReference<FocuserInfo>? focuserMotionSource;
    private long focuserMotionEpoch;
    private long focuserMotionHistoryGeneration = -1;
    private bool focuserMotionSharingBlocked;

    private void RecordFocuserMotion(FocuserInfo info)
    {
        if (!started || info is null)
        {
            return;
        }
        var generation = CaptureHistoryGeneration();
        var epoch = Volatile.Read(ref focuserMotionEpoch);
        try
        {
            var snapshot = DirectFocuserMotionSnapshot.FromNina(info);
            lock (focuserMotionGate)
            {
                if (!started || epoch != focuserMotionEpoch
                    || !IsHistoryGenerationCurrent(generation))
                {
                    return;
                }
                if (focuserMotionSharingBlocked || !eventDelivery.Current.FocuserMotion)
                {
                    focuserMotionTracker.Reset();
                    focuserMotionSource = null;
                    return;
                }
                if (generation != focuserMotionHistoryGeneration
                    || focuserMotionSource is null
                    || !focuserMotionSource.TryGetTarget(out var source)
                    || !ReferenceEquals(source, info))
                {
                    focuserMotionTracker.Reset();
                    focuserMotionSource = new(info);
                    focuserMotionHistoryGeneration = generation;
                }
                var movement = focuserMotionTracker.Observe(snapshot);
                if (movement is null)
                {
                    return;
                }
                var details = new List<(string Name, object? Value)>
                {
                    ("From", movement.From),
                    ("To", movement.To),
                    ("ObservedInProgress", movement.ObservedInProgress),
                    ("EndDetection", movement.EndDetection),
                };
                if (movement.Temperature is { } temperature)
                {
                    details.Add(("Temperature", temperature));
                }
                // Local bounded-history insertion only. Neither the poller
                // nor N.I.N.A.'s sequencer waits for transport or chat here.
                AddEventAt(generation, utcNow(), "FOCUSER-MOVED", details.ToArray());
            }
        }
        catch (Exception)
        {
            LogMotionCaptureWarning("focuser");
        }
    }

    private void ResetFocuserMotionCapture(bool? blockSharing = null, bool purgeHistory = false)
    {
        lock (focuserMotionGate)
        {
            focuserMotionEpoch++;
            focuserMotionTracker.Reset();
            focuserMotionSource = null;
            focuserMotionHistoryGeneration = -1;
            // A connection edge can rebaseline but must never reopen the
            // stronger gate closed by profile revocation or PolicyChanging.
            if (blockSharing is { } blocked)
            {
                focuserMotionSharingBlocked = blocked;
            }
            if (purgeHistory)
            {
                events.RemoveWhere(item =>
                    item.TryGetValue("Event", out var name)
                    && name is string eventName && eventName == "FOCUSER-MOVED");
            }
        }
    }
}
