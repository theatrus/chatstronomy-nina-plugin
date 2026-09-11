using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Validations;

namespace Chatstronomy.NINA.Sequencing;

/// <summary>
/// Resolves the sequencer's current native target at execution. No coordinates
/// or position angle are accepted from the chat command or a cached event.
/// </summary>
internal sealed class NativeTargetCommands(
    IProfileService profileService,
    ITelescopeMediator telescope,
    IImagingMediator imaging,
    IRotatorMediator rotator,
    IFilterWheelMediator filterWheel,
    IGuiderMediator guider,
    IDomeMediator dome,
    IDomeFollower domeFollower,
    IPlateSolverFactory plateSolverFactory,
    IWindowServiceFactory windowFactory)
{
    internal static TargetSnapshot CaptureTarget(ISequenceContainer context) =>
        TryCaptureTarget(context) ?? throw new InvalidOperationException(
            "The active instruction has no native sequence target. Put the trigger in the active target's instruction set.");

    internal static ContextCoordinates ResolveTarget(ISequenceContainer context) => CaptureTarget(context).Coordinates;

    internal static TargetSnapshot? TryCaptureTarget(ISequenceContainer context)
    {
        foreach (var ancestor in SequenceCommandCoordinator.Ancestors(context))
        {
            if (ancestor is not IDeepSkyObjectContainer targetContainer) continue;
            var target = targetContainer.Target;
            var coordinates = target?.InputCoordinates?.Coordinates;
            if (coordinates is null || !double.IsFinite(coordinates.RA) || coordinates.RA < 0 || coordinates.RA >= 24
                || !double.IsFinite(coordinates.Dec) || coordinates.Dec < -90 || coordinates.Dec > 90
                || target?.DeepSkyObject is null)
                throw new InvalidOperationException("The active sequence target has no valid coordinates.");
            // Snapshot once: target schedulers can update their native target
            // between visits, but cannot retarget an already accepted operation.
            return new TargetSnapshot(ancestor, target, target.DeepSkyObject, target.TargetName,
                new ContextCoordinates(coordinates.Clone(), target.PositionAngle, target.DeepSkyObject.ShiftTrackingRate));
        }
        return null;
    }

    internal Task ExecuteAsync(SequenceCommandKind kind, ISequenceContainer context,
        IProgress<ApplicationStatus> progress, CancellationToken token) =>
        ExecuteAsync(kind, CaptureTarget(context), context, progress, token);

    internal async Task ExecuteAsync(SequenceCommandKind kind, TargetSnapshot snapshot, ISequenceContainer context,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (kind is not (SequenceCommandKind.SlewToTarget or SequenceCommandKind.CenterTarget or SequenceCommandKind.CenterRotateTarget))
            throw new ArgumentOutOfRangeException(nameof(kind));
        EnsureCurrent(snapshot, context, token);
        var target = snapshot.Coordinates;
        var telescopeInfo = telescope.GetInfo();
        EnsureCurrent(snapshot, context, token);
        if (!telescopeInfo.Connected || telescopeInfo.AtPark)
            throw new InvalidOperationException("The telescope must be connected and unparked to return to the active target.");
        if (kind == SequenceCommandKind.SlewToTarget)
        {
            // N.I.N.A. 3.2 exposes no IsGuiding snapshot. Its native slew and
            // center instructions use StopGuiding's result as the resume flag;
            // PHD2 and DirectGuider return false when guiding was already idle.
            var stoppedGuiding = await guider.StopGuiding(token).ConfigureAwait(false);
            try
            {
                EnsureCurrent(snapshot, context, token);
                if (!await telescope.SlewToCoordinatesAsync(target.Coordinates.Clone(), token).ConfigureAwait(false))
                    throw new InvalidOperationException("The telescope did not complete the slew to the active target.");
                EnsureCurrent(snapshot, context, token);
            }
            finally
            {
                if (stoppedGuiding && !token.IsCancellationRequested && snapshot.IsCurrent(context)
                    && !await guider.StartGuiding(false, progress, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Guiding could not be restored after the target slew.");
            }
            EnsureCurrent(snapshot, context, token);
            return;
        }

        Center instruction;
        if (kind == SequenceCommandKind.CenterTarget)
        {
            instruction = new Center(profileService, telescope, imaging, filterWheel, guider, dome,
                domeFollower, plateSolverFactory, windowFactory);
        }
        else if (kind == SequenceCommandKind.CenterRotateTarget)
        {
            if (!double.IsFinite(target.PositionAngle))
                throw new InvalidOperationException("The active target has no valid position angle.");
            instruction = new CenterAndRotate(profileService, telescope, imaging, rotator, filterWheel,
                guider, dome, domeFollower, plateSolverFactory, windowFactory)
            {
                PositionAngle = target.PositionAngle,
            };
        }
        else throw new ArgumentOutOfRangeException(nameof(kind));

        // No parent attachment: these transient instructions neither inherit
        // mutable scheduler coordinates nor belong to the user's item list.
        instruction.Coordinates = new InputCoordinates { Coordinates = target.Coordinates.Clone() };
        instruction.Inherited = false;
        if (instruction is CenterAndRotate rotate) rotate.PositionAngle = target.PositionAngle;
        if (!instruction.Validate())
            throw new SequenceEntityFailedValidationException(string.Join(", ", instruction.Issues));
        EnsureCurrent(snapshot, context, token);
        // Execute (rather than Run) preserves native solver/filter/guider/dome
        // behavior while allowing failures to reach the chat command outcome.
        await instruction.Execute(progress, token).ConfigureAwait(false);
        EnsureCurrent(snapshot, context, token);
    }

    private static void EnsureCurrent(TargetSnapshot snapshot, ISequenceContainer context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!snapshot.IsCurrent(context))
            throw new InvalidOperationException("The active sequence target changed after this command was requested.");
    }

    internal sealed class TargetSnapshot(ISequenceContainer owner, object target, object deepSkyObject, string? targetName, ContextCoordinates coordinates)
    {
        internal ContextCoordinates Coordinates { get; } = coordinates;
        internal bool IsCurrent(ISequenceContainer context)
        {
            foreach (var ancestor in SequenceCommandCoordinator.Ancestors(context))
            {
                if (ancestor is not IDeepSkyObjectContainer candidate) continue;
                var current = candidate.Target;
                var position = current?.InputCoordinates?.Coordinates;
                return ReferenceEquals(ancestor, owner) && ReferenceEquals(current, target)
                    && ReferenceEquals(current?.DeepSkyObject, deepSkyObject)
                    && string.Equals(current?.TargetName, targetName, StringComparison.Ordinal)
                    && position is not null && position.RA.Equals(Coordinates.Coordinates.RA)
                    && position.Dec.Equals(Coordinates.Coordinates.Dec)
                    && position.Epoch == Coordinates.Coordinates.Epoch
                    && current!.PositionAngle.Equals(Coordinates.PositionAngle);
            }
            return false;
        }
    }
}
