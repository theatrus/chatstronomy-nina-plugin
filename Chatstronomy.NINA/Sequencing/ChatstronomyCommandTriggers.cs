using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace Chatstronomy.NINA.Sequencing;

/// <summary>Opt-in sequencer boundary for an explicitly requested chat command.</summary>
[JsonObject(MemberSerialization.OptIn)]
public abstract class ChatstronomyCommandTrigger : SequenceTrigger
{
    private protected readonly IProfileService ProfileService;
    private readonly SequenceCommandCoordinator coordinator;
    internal SequenceCommandKind Kind { get; }

    private protected ChatstronomyCommandTrigger(IProfileService profileService, SequenceCommandKind kind)
    {
        ProfileService = profileService;
        coordinator = SequenceCommandCoordinator.For(profileService);
        Kind = kind;
        Name = DisplayName(kind);
        Description = "Run a requested Chatstronomy command before the next light exposure.";
        Category = "Chatstronomy";
    }

    internal static string DisplayName(SequenceCommandKind kind) => kind switch
    {
        SequenceCommandKind.Autofocus => "Chatstronomy Autofocus",
        SequenceCommandKind.ChangeFilter => "Chatstronomy Filter Change",
        SequenceCommandKind.SlewToTarget => "Chatstronomy Slew to Target",
        SequenceCommandKind.CenterTarget => "Chatstronomy Center Target",
        SequenceCommandKind.CenterRotateTarget => "Chatstronomy Center and Rotate Target",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public override void SequenceBlockInitialize() => coordinator.Register(this);
    public override void SequenceBlockTeardown() => coordinator.Unregister(this);
    public override void Teardown() => coordinator.Unregister(this);
    public override void AfterParentChanged() => coordinator.Unregister(this);
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        coordinator.ShouldExecute(this, previousItem, nextItem);
    public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        coordinator.ExecuteAsync(this, context, progress, token);

    private protected T Copy<T>(T clone) where T : ChatstronomyCommandTrigger
    {
        clone.CopyMetaData(this);
        // Runtime registration and pending commands deliberately never clone.
        return clone;
    }

    public override string ToString() => Name;
}

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Chatstronomy Autofocus")]
[ExportMetadata("Description", "Run requested autofocus before the next light exposure.")]
[ExportMetadata("Icon", "AutoFocusSVG")]
[ExportMetadata("Category", "Chatstronomy")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ChatstronomyAutofocusTrigger : ChatstronomyCommandTrigger
{
    [ImportingConstructor]
    public ChatstronomyAutofocusTrigger(IProfileService profileService) : base(profileService, SequenceCommandKind.Autofocus) { }
    public override object Clone() => Copy(new ChatstronomyAutofocusTrigger(ProfileService));
}

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Chatstronomy Filter Change")]
[ExportMetadata("Description", "Change to the requested filter before the next light exposure.")]
[ExportMetadata("Icon", "FilterWheelSVG")]
[ExportMetadata("Category", "Chatstronomy")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ChatstronomyFilterChangeTrigger : ChatstronomyCommandTrigger
{
    [ImportingConstructor]
    public ChatstronomyFilterChangeTrigger(IProfileService profileService) : base(profileService, SequenceCommandKind.ChangeFilter) { }
    public override object Clone() => Copy(new ChatstronomyFilterChangeTrigger(ProfileService));
}

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Chatstronomy Slew to Target")]
[ExportMetadata("Description", "Slew back to the active sequence target before the next light exposure.")]
[ExportMetadata("Icon", "SlewToRaDecSVG")]
[ExportMetadata("Category", "Chatstronomy")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ChatstronomySlewToTargetTrigger : ChatstronomyCommandTrigger
{
    [ImportingConstructor]
    public ChatstronomySlewToTargetTrigger(IProfileService profileService) : base(profileService, SequenceCommandKind.SlewToTarget) { }
    public override object Clone() => Copy(new ChatstronomySlewToTargetTrigger(ProfileService));
}

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Chatstronomy Center Target")]
[ExportMetadata("Description", "Center the active sequence target before the next light exposure.")]
[ExportMetadata("Icon", "PlatesolveSVG")]
[ExportMetadata("Category", "Chatstronomy")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ChatstronomyCenterTargetTrigger : ChatstronomyCommandTrigger
{
    [ImportingConstructor]
    public ChatstronomyCenterTargetTrigger(IProfileService profileService) : base(profileService, SequenceCommandKind.CenterTarget) { }
    public override object Clone() => Copy(new ChatstronomyCenterTargetTrigger(ProfileService));
}

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Chatstronomy Center and Rotate Target")]
[ExportMetadata("Description", "Center and rotate to the active sequence target before the next light exposure.")]
[ExportMetadata("Icon", "PlatesolveAndRotateSVG")]
[ExportMetadata("Category", "Chatstronomy")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ChatstronomyCenterRotateTargetTrigger : ChatstronomyCommandTrigger
{
    [ImportingConstructor]
    public ChatstronomyCenterRotateTargetTrigger(IProfileService profileService) : base(profileService, SequenceCommandKind.CenterRotateTarget) { }
    public override object Clone() => Copy(new ChatstronomyCenterRotateTargetTrigger(ProfileService));
}
