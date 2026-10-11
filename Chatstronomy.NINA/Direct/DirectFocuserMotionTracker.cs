using NINA.Equipment.Equipment.MyFocuser;

namespace Chatstronomy.NINA.Direct;

/// Primitive observations only: this does not query a driver, infer a target,
/// or claim that a move succeeded. N.I.N.A. reuses its mutable info object.
internal sealed record DirectFocuserMotionSnapshot(
    bool Connected,
    int? Position,
    double? Temperature,
    bool IsMoving,
    bool IsSettling)
{
    internal static DirectFocuserMotionSnapshot FromNina(FocuserInfo info) => new(
        info.Connected,
        info.Position >= 0 ? info.Position : null,
        double.IsFinite(info.Temperature) ? info.Temperature : null,
        info.IsMoving,
        info.IsSettling);
}

internal sealed record DirectFocuserMovement(
    int? From,
    int To,
    double? Temperature,
    bool ObservedInProgress,
    string EndDetection);

/// Coalesce busy/settling samples and require two matching idle snapshots.
/// A fast move can change position without any observed busy sample. Brief
/// idle gaps during backlash correction remain part of the same observation.
internal sealed class DirectFocuserMotionTracker
{
    private int? baseline;
    private bool pending;
    private bool observedMoving;
    private int? idleCandidate;

    internal DirectFocuserMovement? Observe(DirectFocuserMotionSnapshot snapshot)
    {
        if (!snapshot.Connected || snapshot.Position is not >= 0)
        {
            Reset();
            return null;
        }

        var position = snapshot.Position.Value;
        var busy = snapshot.IsMoving || snapshot.IsSettling;
        if (baseline is null)
        {
            // Connecting or enabling mid-move must not publish a completion
            // for activity whose start was never continuously shareable.
            if (!busy)
            {
                baseline = position;
            }
            return null;
        }

        if (busy)
        {
            pending = true;
            observedMoving |= snapshot.IsMoving;
            idleCandidate = null;
            return null;
        }

        if (!pending)
        {
            if (position == baseline)
            {
                return null;
            }
            pending = true;
        }

        if (idleCandidate != position)
        {
            idleCandidate = position;
            return null;
        }

        var previous = baseline;
        var inProgress = observedMoving;
        baseline = position;
        pending = false;
        observedMoving = false;
        idleCandidate = null;
        if (previous == position)
        {
            return null;
        }

        return new(
            previous,
            position,
            snapshot.Temperature is { } temperature && double.IsFinite(temperature)
                ? temperature : null,
            inProgress,
            inProgress ? "motion_state" : "position_change");
    }

    internal void Reset()
    {
        baseline = null;
        pending = false;
        observedMoving = false;
        idleCandidate = null;
    }
}
