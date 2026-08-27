using NINA.Astrometry;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;

namespace Chatstronomy.NINA.Tests;

/// <summary>
/// Minimal target container used to verify that disabling Target Scheduler
/// sharing keeps nested instructions without exposing user-supplied names.
/// </summary>
internal sealed class PrivateTargetContainer : SequenceContainer, IDeepSkyObjectContainer
{
    internal PrivateTargetContainer(string name, string targetName) : base(new SequentialStrategy())
    {
        Name = name;
        Target = new InputTarget(Angle.Zero, Angle.Zero, null!)
        {
            TargetName = targetName,
        };
    }

    public InputTarget Target { get; set; }

    public NighttimeData NighttimeData => null!;

    public override object Clone() => new PrivateTargetContainer(Name, Target.TargetName);
}
