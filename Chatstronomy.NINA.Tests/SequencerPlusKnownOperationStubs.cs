using NINA.Core.Model;
using NINA.Astrometry;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace NINA.Plugin.SequencerPlus;

/// <summary>
/// Contract-shaped test doubles for instructions recognized by simple type
/// name. Production code intentionally has no Sequencer+ assembly reference.
/// </summary>
internal sealed class SlewToRADec : SequenceItem
{
    public override object Clone() => new SlewToRADec();

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal sealed class SlewToAltAz : SequenceItem
{
    public override object Clone() => new SlewToAltAz();

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal sealed class WaitUntil : SequenceItem
{
    internal TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal int Time { get; set; } = 43_200;

    internal string Predicate { get; set; } = "PRIVATE_EXPRESSION";

    internal string PredicateExpr { get; set; } = "PRIVATE_PREDICATE_OBJECT";

    public override object Clone() => new WaitUntil
    {
        WaitInterval = WaitInterval,
        Time = Time,
        Predicate = Predicate,
        PredicateExpr = PredicateExpr,
    };

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal sealed class WaitIndefinitely : SequenceItem
{
    internal int Time { get; set; } = 43_200;

    public override object Clone() => new WaitIndefinitely { Time = Time };

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal sealed class Break : SequenceItem
{
    internal int Time { get; set; } = 43_200;

    internal string Reason { get; set; } = "PRIVATE_BREAK_REASON";

    public override object Clone() => new Break { Time = Time, Reason = Reason };

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal abstract class NoOpItem : SequenceItem
{
    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}

internal sealed class TakeExposure : NoOpItem
{
    public override object Clone() => new TakeExposure();
}

internal sealed class RunAutofocus : NoOpItem
{
    public override object Clone() => new RunAutofocus();
}

internal sealed class StartGuiding : NoOpItem
{
    public override object Clone() => new StartGuiding();
}

internal sealed class WarmCamera : NoOpItem
{
    internal double Duration { get; set; } = 12;

    public override object Clone() => new WarmCamera { Duration = Duration };
}

internal sealed class SwitchFilter : NoOpItem
{
    public override object Clone() => new SwitchFilter();
}

internal sealed class OpenDomeShutter : NoOpItem
{
    public override object Clone() => new OpenDomeShutter();
}

internal sealed class ConnectAllEquipment : NoOpItem
{
    public override object Clone() => new ConnectAllEquipment();
}

internal sealed class SolveAndSync : NoOpItem
{
    public object PlateSolveStatusVM { get; } = new PlateSolveStatusStub();

    public override object Clone() => new SolveAndSync();
}

internal sealed class PlateSolveStatusStub
{
    public object PlateSolveResult { get; } = new
    {
        Success = true,
        PositionAngle = 31.5,
    };
}

internal sealed class WaitForSunAltitude : NoOpItem
{
    public WaitLoopDataStub Data { get; } = new();

    public override object Clone() => new WaitForSunAltitude();
}

internal sealed class WaitForTime : NoOpItem
{
    public TimeSpan EstimatedDuration { get; set; } = TimeSpan.FromMinutes(15);

    public DateTimeProviderStub DateTime { get; } = new();

    public override TimeSpan GetEstimatedDuration() => EstimatedDuration;

    public override object Clone() => new WaitForTime
    {
        EstimatedDuration = EstimatedDuration,
    };
}

internal sealed class DateTimeProviderStub
{
    public DateTime Now { get; set; } = new(
        2026,
        8,
        26,
        20,
        0,
        0,
        DateTimeKind.Unspecified);
}

internal sealed class WaitLoopDataStub
{
    public double TargetAltitude { get; set; } = -12;
    public string Comparator { get; set; } = "LESS_THAN_OR_EQUAL";
    public DateTime ExpectedDateTime { get; set; } =
        new(2026, 8, 26, 21, 30, 0, DateTimeKind.Unspecified);
}

internal sealed class UnknownPluginItem : NoOpItem
{
    public override object Clone() => new UnknownPluginItem();
}

internal sealed class SetSwitchValue : NoOpItem
{
    public double Value { get; set; } = 42.5;

    public SwitchStub SelectedSwitch { get; } = new();

    public override object Clone() => new SetSwitchValue { Value = Value };
}

internal sealed class SwitchStub
{
    public int Id { get; set; } = 7;
}

/// <summary>
/// Sequencer+ proxy containers implement IDeepSkyObjectContainer only to pass
/// coordinate context. They are not observing targets.
/// </summary>
internal sealed class IfContainer : SequenceContainer, IDeepSkyObjectContainer
{
    internal IfContainer() : base(new SequentialStrategy())
    {
        Name = "Sequencer+ if";
        Target = new InputTarget(Angle.Zero, Angle.Zero, null!)
        {
            TargetName = "PROXY_TARGET_MUST_NOT_PROJECT",
        };
    }

    public InputTarget Target { get; set; }

    public NighttimeData NighttimeData => null!;

    public override object Clone() => new IfContainer();
}

internal sealed class SafetyMonitorCondition : SequenceCondition
{
    public bool IsSafe { get; set; }

    public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) => IsSafe;

    public override object Clone() => new SafetyMonitorCondition { IsSafe = IsSafe };
}

internal sealed class TriggerOnUnsafe : SequenceTrigger
{
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;

    public override Task Execute(
        ISequenceContainer context,
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;

    public override object Clone() => new TriggerOnUnsafe();
}
