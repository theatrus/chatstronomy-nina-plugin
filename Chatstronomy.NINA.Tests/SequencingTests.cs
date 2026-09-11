using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chatstronomy.NINA.Sequencing;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace Chatstronomy.NINA.Tests;

internal static class SequencingTests
{
    internal static async Task RunAsync()
    {
        ExportedTriggersCloneWithoutPendingState();
        MefContainersDiscoverAllTriggersAndShareProfileCoordinator();
        await NativeSequencerAwaitsCommandBetweenNestedExposures();
        await FailureReachesCompletionDespiteNativeTriggerCatchingIt();
        await PendingCancellationAndPermissionRevocationAreTerminal();
        await InvalidationDuringAdmissionCannotPublishAnOldRunRequest();
        await NativeTriggerRemovalCannotInvertCoordinatorAndContainerLocks();
        await CancellationDoesNotBlockCallerOrReleaseWhileDriverCleanupRuns();
        await DetachAndExternalAutofocusCancelPendingRequests();
        await NativeAutofocusCompletionSupersedesARequestQueuedDuringItsRun();
        await ExpirationDoesNotWaitForAnotherExposure();
        await ReusedTargetContainerCannotRedirectAnyQueuedCommand();
        await NativeTargetCommandsRecheckPreparatoryReadsAndPreserveGuiding();
        await MeridianDeferralDoesNotConsumeTheRequest();
        RejectMissingDisabledAndParallelTriggers();
        await TargetSchedulerSequencingTests.RunAsync();
    }

    private static void ExportedTriggersCloneWithoutPendingState()
    {
        var profile = Fake<IProfileService>();
        ChatstronomyCommandTrigger[] triggers =
        [
            new ChatstronomyAutofocusTrigger(profile), new ChatstronomyFilterChangeTrigger(profile),
            new ChatstronomySlewToTargetTrigger(profile), new ChatstronomyCenterTargetTrigger(profile),
            new ChatstronomyCenterRotateTargetTrigger(profile),
        ];
        Check(triggers.Select(item => item.Kind).Distinct().Count() == 5, "Each command needs its own trigger.");
        foreach (var trigger in triggers)
        {
            Check(trigger.GetType().GetCustomAttributes<ExportAttribute>().Any(item => item.ContractType == typeof(ISequenceTrigger)), "Trigger must be MEF exported.");
            var clone = (ChatstronomyCommandTrigger)trigger.Clone();
            Check(clone.Kind == trigger.Kind && clone.Parent is null, "Clone retains metadata, not an active registration.");
        }
    }

    private static void MefContainersDiscoverAllTriggersAndShareProfileCoordinator()
    {
        // Match N.I.N.A.'s PluginLoader: manifest and sequencer parts have
        // separate MEF containers but receive the same profile service export.
        using var catalog = new AssemblyCatalog(typeof(ChatstronomyAutofocusTrigger).Assembly);
        using var manifestContainer = new CompositionContainer(catalog);
        using var sequenceContainer = new CompositionContainer(catalog);
        var profile = Fake<IProfileService>();
        manifestContainer.ComposeExportedValue(profile);
        sequenceContainer.ComposeExportedValue(profile);
        var first = manifestContainer.GetExportedValues<ISequenceTrigger>().OfType<ChatstronomyCommandTrigger>().ToArray();
        var second = sequenceContainer.GetExportedValues<ISequenceTrigger>().OfType<ChatstronomyCommandTrigger>().ToArray();
        Check(first.Length == 5 && second.Length == 5, "Both native MEF containers must discover all five exported triggers.");
        var field = typeof(ChatstronomyCommandTrigger).GetField("coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var providerCoordinator = SequenceCommandCoordinator.For(profile);
        foreach (var trigger in first.Concat(second))
            Check(ReferenceEquals(field.GetValue(trigger), providerCoordinator),
                "MEF-created triggers and the manually-created provider share the same profile-scoped coordinator.");
        Check(!ReferenceEquals(SequenceCommandCoordinator.For(Fake<IProfileService>()), providerCoordinator),
            "Separate N.I.N.A. service instances cannot share queued hardware requests.");
    }

    private static async Task NativeSequencerAwaitsCommandBetweenNestedExposures()
    {
        var fixture = new Fixture();
        var started = NewSignal();
        var finish = NewSignal();
        var second = NewSignal();
        fixture.Inner.Add(new Exposure(async token => { started.TrySetResult(); await finish.Task.WaitAsync(token); }));
        fixture.Inner.Add(new Exposure(_ => { second.TrySetResult(); return Task.CompletedTask; }));
        var running = fixture.Root.Run(new Progress<ApplicationStatus>(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var autofocusStarted = NewSignal();
        var autofocusFinish = NewSignal();
        var completion = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner, () => true,
            async (context, _, token) =>
            {
                Check(ReferenceEquals(context, fixture.Inner), "Ancestor trigger receives the actual nested exposure parent.");
                Check(fixture.Coordinator.IsExecutingAutofocus, "Execution must be claimed before AF broadcasts start.");
                fixture.Coordinator.NotifyExternalAutofocus();
                autofocusStarted.TrySetResult();
                await autofocusFinish.Task.WaitAsync(token);
            }, deduplicationKey: "af");
        Check(!completion.IsCompleted && !autofocusStarted.Task.IsCompleted, "AF must wait for current exposure.");
        finish.TrySetResult();
        await autofocusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!second.Task.IsCompleted, "Next exposure must wait for the entire command.");
        var duplicate = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner, () => true,
            (_, _, _) => throw new Exception("Duplicate executed"), deduplicationKey: "af");
        Check(ReferenceEquals(completion, duplicate), "Explicit identical requests deduplicate to one completion.");
        autofocusFinish.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Check(second.Task.IsCompleted && !fixture.Coordinator.IsBusy, "Sequence resumes and command releases ownership.");
        Check(!fixture.Coordinator.HasActiveTrigger(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner), "Native block teardown removes registration.");
    }

    private static async Task FailureReachesCompletionDespiteNativeTriggerCatchingIt()
    {
        var fixture = Fixture.Active();
        var next = fixture.Next();
        var error = new InvalidOperationException("autofocus failed");
        var completion = fixture.Queue((_, _, _) => Task.FromException(error));
        Check(fixture.Trigger.ShouldTrigger(null!, next), "Queued command should reach trigger.");
        await fixture.Trigger.Run(fixture.Inner, new Progress<ApplicationStatus>(), CancellationToken.None);
        await ExpectFailure(completion);
        Check(fixture.Trigger.Status == SequenceEntityStatus.FAILED && !fixture.Coordinator.IsBusy,
            "Native failure is visible and pending ownership released.");
    }

    private static async Task PendingCancellationAndPermissionRevocationAreTerminal()
    {
        var fixture = Fixture.Active();
        var invoked = false;
        var completion = fixture.Queue((_, _, _) => { invoked = true; return Task.CompletedTask; });
        Check(fixture.Coordinator.CancelAutofocus(), "Queued AF should cancel.");
        await ExpectFailure(completion);
        Check(!fixture.Trigger.ShouldTrigger(null!, fixture.Next()) && !invoked, "Canceled callback never executes.");
        var permitted = true;
        completion = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner,
            () => permitted, (_, _, _) => { invoked = true; return Task.CompletedTask; });
        permitted = false;
        fixture.Coordinator.ReconcilePermissions();
        await ExpectFailure(completion);
        Check(!fixture.Coordinator.IsBusy && !invoked, "Revocation clears pending command immediately.");
    }

    private static async Task InvalidationDuringAdmissionCannotPublishAnOldRunRequest()
    {
        var fixture = Fixture.Active();
        var validationEntered = NewSignal();
        using var releaseValidation = new ManualResetEventSlim();
        var attempt = Task.Run(() => fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner,
            () =>
            {
                validationEntered.TrySetResult();
                if (!releaseValidation.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Validation was not released.");
                return true;
            }, (_, _, _) => throw new Exception("An invalidated request executed.")));
        await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Coordinator.Invalidate("The prior sequence run ended during admission.");
        releaseValidation.Set();
        await ExpectFailure(attempt);
        Check(!fixture.Coordinator.IsBusy, "Invalidation before publication must not leave a request from the old sequence epoch.");
    }

    private static async Task NativeTriggerRemovalCannotInvertCoordinatorAndContainerLocks()
    {
        var profile = Fake<IProfileService>();
        var coordinator = SequenceCommandCoordinator.For(profile);
        var root = new SequenceRootContainer { Status = SequenceEntityStatus.RUNNING };
        var parent = new InstrumentedTriggerContainer { Status = SequenceEntityStatus.RUNNING };
        root.Add(parent);
        var trigger = new ChatstronomyAutofocusTrigger(profile);
        parent.Add(trigger);
        trigger.SequenceBlockInitialize();
        var next = new Exposure(_ => Task.CompletedTask);
        parent.Add(next);
        var completion = coordinator.Queue(SequenceCommandKind.Autofocus, root, parent, () => true,
            (_, _, _) => throw new Exception("Detached trigger executed."));
        var collectionLocked = NewSignal();
        using var snapshotRequested = new ManualResetEventSlim();
        var nativeLock = typeof(SequenceContainer).GetField("lockObj", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        parent.BeforeSnapshot = () => snapshotRequested.Set();
        var removing = Task.Run(() =>
        {
            // Native Remove holds this collection lock and calls into the
            // trigger's AfterParentChanged -> coordinator.Unregister hook.
            lock (nativeLock)
            {
                collectionLocked.TrySetResult();
                if (!snapshotRequested.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Trigger snapshot was not requested.");
                parent.Remove(trigger);
            }
        });
        await collectionLocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deciding = Task.Run(() => trigger.ShouldTrigger(null!, next));
        await Task.WhenAll(removing, deciding).WaitAsync(TimeSpan.FromSeconds(5));
        await ExpectFailure(completion);
        Check(!deciding.Result && !coordinator.IsBusy, "Container removal and trigger eligibility must not hold each other's locks.");
    }

    private sealed class InstrumentedTriggerContainer : SequentialContainer, ITriggerable
    {
        internal Action? BeforeSnapshot;
        ICollection<ISequenceTrigger> ITriggerable.GetTriggersSnapshot()
        {
            BeforeSnapshot?.Invoke();
            return GetTriggersSnapshot();
        }
    }

    private static async Task CancellationDoesNotBlockCallerOrReleaseWhileDriverCleanupRuns()
    {
        var fixture = Fixture.Active();
        var started = NewSignal();
        var cancellationEntered = NewSignal();
        var hardwareFinish = NewSignal();
        using var releaseCancellation = new ManualResetEventSlim();
        CancellationTokenRegistration driverCallback = default;
        var completion = fixture.Queue(async (_, _, token) =>
        {
            // A misbehaving driver can finish its operation before its token
            // cleanup returns. Neither condition alone releases ownership.
            driverCallback = token.Register(() =>
            {
                cancellationEntered.TrySetResult();
                releaseCancellation.Wait(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("driver cancellation callback failed");
            });
            started.TrySetResult();
            await hardwareFinish.Task;
        });
        Check(fixture.Trigger.ShouldTrigger(null!, fixture.Next()), "Queued command should reach trigger.");
        var running = fixture.Trigger.Run(fixture.Inner, new Progress<ApplicationStatus>(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var canceled = Task.Run(fixture.Coordinator.CancelAutofocus);
            Check(await canceled.WaitAsync(TimeSpan.FromSeconds(1)), "Cancel should return without waiting for driver callbacks.");
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            hardwareFinish.TrySetResult();
            Check(fixture.Coordinator.IsBusy && !completion.IsCompleted,
                "Ownership and completion must wait for the cancellation callback to drain.");
            ExpectRejected(() => fixture.Queue((_, _, _) => Task.CompletedTask));
        }
        finally
        {
            hardwareFinish.TrySetResult();
            releaseCancellation.Set();
        }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await ExpectFailure(completion);
        driverCallback.Unregister();
        Check(!fixture.Coordinator.IsBusy, "Ownership releases after the driver task and cancellation cleanup finish.");
    }

    private static async Task DetachAndExternalAutofocusCancelPendingRequests()
    {
        var fixture = Fixture.Active();
        var completion = fixture.Queue((_, _, _) => Task.CompletedTask);
        fixture.Coordinator.NotifyExternalAutofocus();
        await ExpectFailure(completion);
        completion = fixture.Queue((_, _, _) => Task.CompletedTask);
        fixture.Trigger.AttachNewParent(null!);
        await ExpectFailure(completion);
        Check(!fixture.Coordinator.IsBusy, "Detached active trigger revokes pending request.");
    }

    private static async Task NativeAutofocusCompletionSupersedesARequestQueuedDuringItsRun()
    {
        var fixture = Fixture.Active();
        fixture.Coordinator.NotifyExternalAutofocus(); // Native run began before the request existed.
        var completion = fixture.Queue((_, _, _) => throw new Exception("Superseded AF executed"));
        Check(!completion.IsCompleted, "A request can be queued while another native trigger is awaited.");
        fixture.Coordinator.NotifyExternalAutofocus(); // N.I.N.A.'s successful AF report arrived.
        await ExpectFailure(completion);
        Check(!fixture.Trigger.ShouldTrigger(null!, fixture.Next()) && !fixture.Coordinator.IsBusy,
            "An already-completed native run satisfies and clears the redundant pending autofocus.");
    }

    private static async Task ExpirationDoesNotWaitForAnotherExposure()
    {
        var fixture = Fixture.Active();
        var completion = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, fixture.Inner,
            () => true, (_, _, _) => throw new Exception("Expired callback executed"), timeout: TimeSpan.FromMilliseconds(20));
        await ExpectFailure(completion);
        Check(!fixture.Coordinator.IsBusy, "Expiry frees bounded queue without another event.");
    }

    private static async Task ReusedTargetContainerCannotRedirectAnyQueuedCommand()
    {
        foreach (var kind in Enum.GetValues<SequenceCommandKind>())
        {
            var profile = Fake<IProfileService>();
            var coordinator = SequenceCommandCoordinator.For(profile);
            var root = new SequenceRootContainer { Status = SequenceEntityStatus.RUNNING };
            var target = new TestTargetContainer { Status = SequenceEntityStatus.RUNNING, Target = FakeTarget() };
            root.Add(target);
            var trigger = CreateTrigger(kind, profile);
            target.Add(trigger);
            trigger.SequenceBlockInitialize();
            var next = new Exposure(_ => Task.CompletedTask);
            target.Add(next);
            var captured = NativeTargetCommands.CaptureTarget(target);
            var invoked = false;
            var completion = coordinator.Queue(kind, root, target, () => true,
                (_, _, _) => { invoked = true; return Task.CompletedTask; });
            target.Target.InputCoordinates.Coordinates.RA = 7;
            Check(!captured.IsCurrent(target), "Mutating a reused target changes its snapshot identity.");
            Check(!trigger.ShouldTrigger(null!, next), "Changed target must revoke queued command.");
            await ExpectFailure(completion);
            Check(!invoked, "No command can follow Target Scheduler into a replacement target.");
            trigger.Teardown();
        }
    }

    private static async Task NativeTargetCommandsRecheckPreparatoryReadsAndPreserveGuiding()
    {
        foreach (var mutationPoint in new[] { "device read", "guiding stop", "canceled guiding stop", "validation" })
        {
            var context = new TestTargetContainer { Target = FakeTarget() };
            var snapshot = NativeTargetCommands.CaptureTarget(context);
            using var cancellation = new CancellationTokenSource();
            var reads = 0;
            var slews = 0;
            var stops = 0;
            var starts = 0;
            var telescope = Fake<ITelescopeMediator>((method, _) =>
            {
                if (method.Name == nameof(ITelescopeMediator.GetInfo))
                {
                    reads++;
                    if ((mutationPoint == "device read" && reads == 1) || (mutationPoint == "validation" && reads == 2))
                        context.Target.InputCoordinates.Coordinates.RA = 7;
                    return new TelescopeInfo { Connected = true };
                }
                if (method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync))
                {
                    slews++;
                    return Task.FromResult(true);
                }
                return null;
            });
            var guider = Fake<IGuiderMediator>((method, _) =>
            {
                if (method.Name == nameof(IGuiderMediator.StopGuiding))
                {
                    stops++;
                    if (mutationPoint == "guiding stop") context.Target.InputCoordinates.Coordinates.RA = 7;
                    if (mutationPoint == "canceled guiding stop") cancellation.Cancel();
                    return Task.FromResult(true);
                }
                if (method.Name == nameof(IGuiderMediator.StartGuiding))
                {
                    starts++;
                    return Task.FromResult(true);
                }
                return null;
            });
            var helper = new NativeTargetCommands(Fake<IProfileService>(), telescope, null!, null!, null!, guider,
                null!, null!, null!, null!);
            await ExpectFailure(helper.ExecuteAsync(mutationPoint == "validation" ? SequenceCommandKind.CenterTarget : SequenceCommandKind.SlewToTarget,
                snapshot, context, new Progress<ApplicationStatus>(), cancellation.Token));
            Check(slews == 0 && starts == 0, "A changed target or cancellation during preparation cannot actuate a slew or restart guiding.");
            if (mutationPoint == "device read") Check(stops == 0, "A blocking device read must be rechecked before any hardware change.");
        }

        foreach (var wasGuiding in new[] { false, true })
        {
            var context = new TestTargetContainer { Target = FakeTarget() };
            var snapshot = NativeTargetCommands.CaptureTarget(context);
            var slews = 0;
            var starts = 0;
            var telescope = Fake<ITelescopeMediator>((method, args) =>
            {
                if (method.Name == nameof(ITelescopeMediator.GetInfo)) return new TelescopeInfo { Connected = true };
                if (method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync))
                {
                    Check(args?[0] is Coordinates coordinates && coordinates.RA == 6 && coordinates.Dec == 30,
                        "The native slew uses the locally snapshotted target.");
                    slews++;
                    return Task.FromResult(true);
                }
                return null;
            });
            var guider = Fake<IGuiderMediator>((method, _) =>
            {
                if (method.Name == nameof(IGuiderMediator.StopGuiding)) return Task.FromResult(wasGuiding);
                if (method.Name == nameof(IGuiderMediator.StartGuiding)) { starts++; return Task.FromResult(true); }
                return null;
            });
            var helper = new NativeTargetCommands(Fake<IProfileService>(), telescope, null!, null!, null!, guider,
                null!, null!, null!, null!);
            await helper.ExecuteAsync(SequenceCommandKind.SlewToTarget, snapshot, context,
                new Progress<ApplicationStatus>(), CancellationToken.None);
            Check(slews == 1 && starts == (wasGuiding ? 1 : 0), "Restore only guiding that N.I.N.A. actually stopped.");
        }
    }

    private static async Task MeridianDeferralDoesNotConsumeTheRequest()
    {
        var fixture = Fixture.Active();
        var flip = new Flip { Due = true };
        fixture.Inner.Add(flip);
        var next = fixture.Next();
        var invoked = false;
        var completion = fixture.Queue((_, _, _) => { invoked = true; return Task.CompletedTask; });
        Check(!fixture.Trigger.ShouldTrigger(null!, next), "An imminent target-local flip defers the ancestor command.");
        Check(!completion.IsCompleted && fixture.Coordinator.IsBusy, "Deferral preserves the request.");
        flip.Due = false;
        Check(fixture.Trigger.ShouldTrigger(null!, next), "Request becomes eligible after the flip.");
        await fixture.Trigger.Run(fixture.Inner, new Progress<ApplicationStatus>(), CancellationToken.None);
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(invoked, "Deferred request eventually executes once.");
    }

    private static void RejectMissingDisabledAndParallelTriggers()
    {
        var fixture = Fixture.Active();
        fixture.Trigger.Status = SequenceEntityStatus.DISABLED;
        ExpectRejected(() => fixture.Queue((_, _, _) => Task.CompletedTask));
        fixture.Trigger.Status = SequenceEntityStatus.CREATED;
        var parallel = new ParallelContainer { Status = SequenceEntityStatus.RUNNING };
        fixture.Outer.Add(parallel);
        ExpectRejected(() => fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, parallel,
            () => true, (_, _, _) => Task.CompletedTask));
        fixture.Trigger.Teardown();
        ExpectRejected(() => fixture.Queue((_, _, _) => Task.CompletedTask));
    }

    private static ChatstronomyCommandTrigger CreateTrigger(SequenceCommandKind kind, IProfileService profile) => kind switch
    {
        SequenceCommandKind.Autofocus => new ChatstronomyAutofocusTrigger(profile),
        SequenceCommandKind.ChangeFilter => new ChatstronomyFilterChangeTrigger(profile),
        SequenceCommandKind.SlewToTarget => new ChatstronomySlewToTargetTrigger(profile),
        SequenceCommandKind.CenterTarget => new ChatstronomyCenterTargetTrigger(profile),
        SequenceCommandKind.CenterRotateTarget => new ChatstronomyCenterRotateTargetTrigger(profile),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static InputTarget FakeTarget()
    {
        // Bypass chart/calculator setup: these tests need native mutable target
        // data, not sky-survey downloads or astronomy DLL initialization.
        var result = (InputTarget)RuntimeHelpers.GetUninitializedObject(typeof(InputTarget));
        typeof(InputTarget).GetField("inputCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(result,
            new InputCoordinates { Coordinates = new Coordinates(6, 30, Epoch.J2000, Coordinates.RAType.Hours) });
        typeof(InputTarget).GetField("deepSkyObject", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(result, Fake<IDeepSkyObject>());
        return result;
    }

    private sealed class TestTargetContainer : SequentialContainer, IDeepSkyObjectContainer
    {
        public InputTarget Target { get; set; } = null!;
        public NighttimeData NighttimeData => null!;
    }

    private sealed class Fixture
    {
        internal SequenceRootContainer Root { get; } = new();
        internal SequentialContainer Outer { get; } = new();
        internal SequentialContainer Inner { get; } = new();
        internal ChatstronomyAutofocusTrigger Trigger { get; }
        internal SequenceCommandCoordinator Coordinator { get; }
        internal Fixture()
        {
            var profile = Fake<IProfileService>();
            Coordinator = SequenceCommandCoordinator.For(profile);
            Trigger = new(profile);
            Root.Add(Outer);
            Outer.Add(Inner);
            Outer.Add(Trigger);
        }
        internal static Fixture Active()
        {
            var fixture = new Fixture();
            fixture.Root.Status = fixture.Outer.Status = fixture.Inner.Status = SequenceEntityStatus.RUNNING;
            fixture.Trigger.SequenceBlockInitialize();
            return fixture;
        }
        internal Exposure Next()
        {
            var next = new Exposure(_ => Task.CompletedTask);
            Inner.Add(next);
            return next;
        }
        internal Task Queue(Func<ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> execute) =>
            Coordinator.Queue(SequenceCommandKind.Autofocus, Root, Inner, () => true, execute);
    }

    private sealed class Exposure(Func<CancellationToken, Task> execute) : SequenceItem, IExposureItem
    {
        public double ExposureTime { get; set; } = 1;
        public int Gain { get; set; }
        public int Offset { get; set; }
        public string ImageType { get; set; } = "LIGHT";
        public BinningMode Binning { get; set; } = new(1, 1);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => execute(token);
        public override object Clone() => new Exposure(execute);
        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(ExposureTime);
    }

    private sealed class Flip : SequenceTrigger, IMeridianFlipTrigger
    {
        internal bool Due;
        public DateTime LatestFlipTime => Due ? DateTime.Now : DateTime.MinValue;
        public DateTime EarliestFlipTime => LatestFlipTime;
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => Due;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
        public override object Clone() => new Flip { Due = Due };
    }

    public class EmptyProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handler is not null ? Handler(method!, args) : method!.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
    private static T Fake<T>() where T : class => DispatchProxy.Create<T, EmptyProxy>();
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Fake<T>();
        ((EmptyProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectRejected(Func<Task> operation)
    {
        try { operation(); } catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe command should be rejected before queueing.");
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is not TimeoutException || task.IsFaulted) { return; }
        throw new Exception("Expected a terminal command failure.");
    }
}
