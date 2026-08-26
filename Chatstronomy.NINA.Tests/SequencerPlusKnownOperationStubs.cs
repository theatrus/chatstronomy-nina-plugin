using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;

namespace SequencerPlus.TestDoubles;

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
