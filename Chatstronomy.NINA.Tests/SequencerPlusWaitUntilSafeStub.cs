using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;

namespace SequencerPlus.Instructions;

/// <summary>
/// Contract-shaped test double for Sequencer+'s instruction. The production
/// projection intentionally recognizes it by simple type name so the N.I.N.A.
/// plugin does not need to reference SequencerPlus at runtime.
/// </summary>
internal sealed class WaitUntilSafe : SequenceItem
{
    internal TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(5);

    public override object Clone() => new WaitUntilSafe
    {
        WaitInterval = WaitInterval,
    };

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}
