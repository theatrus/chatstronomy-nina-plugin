using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;

namespace ThirdParty.Unrecognized;

/// <summary>
/// Deliberately reuses a built-in name to prove simple-name collisions do not
/// bypass the Other Events consent boundary.
/// </summary>
internal sealed class TakeExposure : SequenceItem
{
    public override object Clone() => new TakeExposure();

    public override Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token) => Task.CompletedTask;
}
