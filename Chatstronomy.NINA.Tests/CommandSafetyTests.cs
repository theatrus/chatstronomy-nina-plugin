using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Sequencing;
using Chatstronomy.NINA.Settings;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.ViewModel.Interfaces;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;

namespace Chatstronomy.NINA.Tests;

internal static class CommandSafetyTests
{
    internal static async Task RunAsync()
    {
        using (var fixture = new Fixture())
        {
            fixture.AdvancedRunning = true;
            foreach (var kind in new[] { DirectRigCommandKind.UnparkMount, DirectRigCommandKind.HomeMount,
                DirectRigCommandKind.ParkMount, DirectRigCommandKind.StartGuiding, DirectRigCommandKind.StopGuiding,
                DirectRigCommandKind.AbortExposure, DirectRigCommandKind.StartSequence })
            {
                await Reject(() => fixture.Send(kind), "sequence is running");
            }
            Check(fixture.HardwareCalls == 0, "Rejected commands touched hardware");
            await fixture.Send(DirectRigCommandKind.CoolCamera, temperature: -10, minutes: 1);
            await WaitUntil(() => fixture.TemperatureCalls == 1, "Cooling was not dispatched");
            await fixture.Send(DirectRigCommandKind.WarmCamera, minutes: 1);
            await WaitUntil(() => fixture.TemperatureCalls == 2, "Warming was not dispatched");
            Check(fixture.TemperatureCalls == 2, "Temperature changes should remain available");
            await fixture.Send(DirectRigCommandKind.StopSequence);
            await WaitUntil(() => fixture.SequenceStops == 1, "Advanced stop was not dispatched");
            Check(fixture.SequenceStops == 1, "Advanced stop was not dispatched");
        }
        using (var fixture = new Fixture())
        {
            fixture.SimpleProxy.IsRunning = true;
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "sequence is running");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartSequence), "sequence is running");
            await fixture.Send(DirectRigCommandKind.StopSequence);
            await WaitUntil(() => fixture.SequenceStops == 1, "Simple sequence stop was not dispatched");
            Check(fixture.SequenceStops == 1, "Simple sequence stop was not dispatched");
        }
        using (var fixture = new Fixture())
        {
            fixture.CameraOwner = new object();
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "camera is busy");
            await Reject(() => fixture.Send(DirectRigCommandKind.ChangeFilter, filter: 0), "camera is busy");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartSequence), "camera is busy");
            Check(fixture.AutofocusCalls == 0, "Autofocus started without owning the camera");
        }
        using (var fixture = new Fixture())
        {
            await fixture.Send(DirectRigCommandKind.StartAutofocus);
            await fixture.WaitForAutofocus();
            Check(fixture.AutofocusCalls == 1 && fixture.CameraOwner is not null, "Idle autofocus did not acquire ownership");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "still running");
            await fixture.Send(DirectRigCommandKind.CancelAutofocus);
            await WaitUntil(() => fixture.AutofocusToken.IsCancellationRequested, "Owned autofocus was not cancelled");
            Check(fixture.AutofocusToken.IsCancellationRequested, "Owned autofocus was not cancelled");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartSequence), "still running");
            // Driver cancellation can unwind slowly: keep the block until its
            // actual task completes, not merely until Cancel() is called.
            Check(fixture.CameraOwner is not null, "Cancellation released ownership too early");
            fixture.AutofocusCompletion.SetCanceled();
            await fixture.WaitForRelease();
            await fixture.Send(DirectRigCommandKind.CancelAutofocus);
            Check(fixture.AutofocusCalls == 1, "Cancel touched unrelated autofocus");
        }
        using (var fixture = new Fixture())
        {
            await fixture.Send(DirectRigCommandKind.StartAutofocus);
            await fixture.WaitForAutofocus();
            await fixture.Send(DirectRigCommandKind.CancelAutofocus);
            await WaitUntil(() => fixture.AutofocusToken.IsCancellationRequested, "Autofocus cancellation was not signalled");
            // Some autofocus implementations return a partial report while
            // unwinding cancellation rather than throwing cancellation.
            fixture.AutofocusCompletion.SetResult(new AutoFocusReport { Timestamp = DateTime.Now, Filter = "L" });
            await fixture.WaitForRelease();
            Check(fixture.AutofocusHistory == 0 && fixture.WindowCloses == 1,
                "A cancelled autofocus report was published as completed");
        }
        using (var fixture = new Fixture())
        {
            await fixture.Send(DirectRigCommandKind.StartAutofocus);
            await fixture.WaitForAutofocus();
            fixture.Provider.RevokeRemoteControl();
            await WaitUntil(() => fixture.AutofocusToken.IsCancellationRequested, "Revocation did not cancel autofocus");
            Check(fixture.AutofocusToken.IsCancellationRequested, "Revocation did not cancel owned autofocus");
            Check(fixture.CameraOwner is not null, "Revocation released ownership before driver cleanup");
            fixture.AutofocusCompletion.SetException(new InvalidOperationException("Simulated AF failure"));
            await fixture.WaitForRelease();
        }
        using (var fixture = new Fixture())
        {
            fixture.AutofocusCompletion.SetResult(new AutoFocusReport { Timestamp = DateTime.Now, Filter = "L" });
            await fixture.Send(DirectRigCommandKind.StartAutofocus);
            await fixture.WaitForRelease();
            Check(fixture.AutofocusHistory == 1 && fixture.WindowCloses == 1,
                "Autofocus report/history/window lifecycle was lost");
            await fixture.Send(DirectRigCommandKind.StartSequence);
            Check(fixture.SequenceStarts == 1, "Start sequence was not dispatched");
        }
        await HardwarePreamblesDoNotBlockAdmission();
        await CancellationCallbacksDoNotBlockAndRetainOwnership();
        await ConcurrentAutofocusRequestsHaveOneOwner();
        await NativeAutofocusCannotBeReenteredOrCancelled();
        await FalseHardwareResultsRemainVisible();
        await ProviderQueuesThroughNativeSequenceTriggers();
        await ProviderNeverFallsBackWhenSequenceTriggerIsUnavailable();
        await ProviderRejectsSequenceEpochChangedDuringAdmission();
    }

    private static async Task ProviderQueuesThroughNativeSequenceTriggers()
    {
        foreach (var kind in new[] { DirectRigCommandKind.StartAutofocus, DirectRigCommandKind.ChangeFilter })
        {
            using var fixture = new Fixture();
            var firstStarted = Signal();
            var finishFirst = Signal();
            var hardwareStarted = Signal();
            var secondStarted = Signal();
            var filterCompleted = new TaskCompletionSource<FilterInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterTask = filterCompleted.Task;
            fixture.AutofocusStarted = _ => hardwareStarted.TrySetResult();
            fixture.FilterPreamble = () => hardwareStarted.TrySetResult();
            var (root, scope, owner) = fixture.NativeSequence();
            scope.Add(CreateTrigger(kind, fixture.ProfileService));
            scope.Add(new Exposure(async token => { firstStarted.TrySetResult(); await finishFirst.Task.WaitAsync(token); }));
            scope.Add(new Exposure(_ => { secondStarted.TrySetResult(); return Task.CompletedTask; }));
            using var stop = new CancellationTokenSource();
            var running = root.Run(new Progress<ApplicationStatus>(), stop.Token);
            try
            {
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(ReferenceEquals(NinaDirectSequenceSnapshot.TryGetSequenceRoot(fixture.Sequence), root),
                    "Provider did not discover the real native sequencer root");
                var reply = await fixture.Send(kind, filter: 0).WaitAsync(TimeSpan.FromSeconds(1));
                AssertAccepted(reply);
                Check(((DirectApiEnvelope<string>)reply!).Response.Contains("queued", StringComparison.OrdinalIgnoreCase),
                    "Provider chose an immediate hardware path during a sequence");
                Check(fixture.AutofocusCalls == 0 && fixture.FilterCalls == 0,
                    "Queued hardware ran before the active exposure finished");
                Check(ReferenceEquals(fixture.CameraOwner, owner), "Queued command stole the sequencer's capture ownership");

                finishFirst.TrySetResult();
                await hardwareStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(25);
                Check(!secondStarted.Task.IsCompleted && !running.IsCompleted,
                    "Native exposure boundary did not await the actual hardware operation");
                Check(ReferenceEquals(fixture.CameraOwner, owner), "Queued execution changed the sequencer's capture ownership");
                fixture.AutofocusCompletion.TrySetResult(new AutoFocusReport { Timestamp = DateTime.Now, Filter = "L" });
                filterCompleted.TrySetResult(fixture.SelectedFilter);
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Check(secondStarted.Task.IsCompleted, "Sequence did not resume after queued hardware completion");
                Check(kind == DirectRigCommandKind.StartAutofocus
                    ? fixture.AutofocusCalls == 1 && fixture.FilterCalls == 0 && fixture.AutofocusHistory == 1
                    : fixture.FilterCalls == 1 && fixture.AutofocusCalls == 0,
                    "Provider did not route exactly one operation through its matching native trigger");
                Check(ReferenceEquals(fixture.CameraOwner, owner), "Queued completion released ownership belonging to N.I.N.A.");
            }
            finally
            {
                finishFirst.TrySetResult();
                fixture.AutofocusCompletion.TrySetCanceled();
                filterCompleted.TrySetCanceled();
                stop.Cancel();
                await FinishNativeSequence(running, stop.Token);
                fixture.CameraOwner = null;
            }
        }
    }

    private static async Task ProviderNeverFallsBackWhenSequenceTriggerIsUnavailable()
    {
        foreach (var kind in new[] { DirectRigCommandKind.StartAutofocus, DirectRigCommandKind.ChangeFilter })
        foreach (var mode in new[] { "absent", "wrong", "disabled" })
        {
            using var fixture = new Fixture();
            var firstStarted = Signal();
            var finishFirst = Signal();
            var (root, scope, owner) = fixture.NativeSequence();
            if (mode != "absent")
            {
                var triggerKind = mode == "wrong"
                    ? kind == DirectRigCommandKind.StartAutofocus ? DirectRigCommandKind.ChangeFilter : DirectRigCommandKind.StartAutofocus
                    : kind;
                var trigger = CreateTrigger(triggerKind, fixture.ProfileService);
                if (mode == "disabled") trigger.Status = SequenceEntityStatus.DISABLED;
                scope.Add(trigger);
            }
            scope.Add(new Exposure(async token => { firstStarted.TrySetResult(); await finishFirst.Task.WaitAsync(token); }));
            using var stop = new CancellationTokenSource();
            var running = root.Run(new Progress<ApplicationStatus>(), stop.Token);
            try
            {
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Reject(() => fixture.Send(kind, filter: 0), "trigger");
                Check(fixture.AutofocusCalls == 0 && fixture.FilterCalls == 0,
                    "Missing, wrong, or disabled trigger caused a direct hardware fallback");
                Check(!SequenceCommandCoordinator.For(fixture.ProfileService).IsBusy,
                    "Unavailable trigger left an accepted command behind");
                Check(ReferenceEquals(fixture.CameraOwner, owner), "Rejected sequence command changed native capture ownership");
            }
            finally
            {
                finishFirst.TrySetResult();
                stop.Cancel();
                await FinishNativeSequence(running, stop.Token);
                fixture.CameraOwner = null;
            }
        }
    }

    private static async Task ProviderRejectsSequenceEpochChangedDuringAdmission()
    {
        using var fixture = new Fixture();
        var firstStarted = Signal();
        var finishFirst = Signal();
        var (root, scope, owner) = fixture.NativeSequence();
        scope.Add(new ChatstronomyAutofocusTrigger(fixture.ProfileService));
        scope.Add(new Exposure(async token => { firstStarted.TrySetResult(); await finishFirst.Task.WaitAsync(token); }));
        using var stop = new CancellationTokenSource();
        var running = root.Run(new Progress<ApplicationStatus>(), stop.Token);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var epoch = typeof(NinaDirectDataProvider).GetField("sequenceControlEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var changed = false;
            fixture.CameraInfoRead = () =>
            {
                if (changed) return;
                changed = true;
                // Reproduce the native lifecycle counter transition while an
                // equipment snapshot is being checked, retaining the same
                // root and profile so only the sequence-run guard can reject.
                epoch.SetValue(fixture.Provider, (long)epoch.GetValue(fixture.Provider)! + 1);
            };
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "no longer permitted");
            Check(changed && fixture.AutofocusCalls == 0 && fixture.FilterCalls == 0,
                "An old sequence context reached hardware after its epoch changed");
            Check(!SequenceCommandCoordinator.For(fixture.ProfileService).IsBusy,
                "Stale admission left a request queued for another sequence run");
            Check(ReferenceEquals(fixture.CameraOwner, owner), "Stale admission changed native capture ownership");
        }
        finally
        {
            fixture.CameraInfoRead = null;
            finishFirst.TrySetResult();
            stop.Cancel();
            await FinishNativeSequence(running, stop.Token);
            fixture.CameraOwner = null;
        }
    }

    private static ChatstronomyCommandTrigger CreateTrigger(DirectRigCommandKind kind, IProfileService profile) =>
        kind == DirectRigCommandKind.StartAutofocus ? new ChatstronomyAutofocusTrigger(profile) : new ChatstronomyFilterChangeTrigger(profile);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task FinishNativeSequence(Task running, CancellationToken stop)
    {
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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

    private static async Task HardwarePreamblesDoNotBlockAdmission()
    {
        foreach (var kind in new[] { DirectRigCommandKind.StartAutofocus,
            DirectRigCommandKind.ChangeFilter, DirectRigCommandKind.CoolCamera })
        {
            using var fixture = new Fixture();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action preamble = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test hardware preamble was not released");
            };
            if (kind == DirectRigCommandKind.StartAutofocus) fixture.AutofocusPreamble = preamble;
            else if (kind == DirectRigCommandKind.ChangeFilter) fixture.FilterPreamble = preamble;
            else fixture.TemperaturePreamble = preamble;
            var reply = Task.Run(() => fixture.Send(kind, filter: 0, temperature: -10, minutes: 1));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                AssertAccepted(await reply.WaitAsync(TimeSpan.FromSeconds(1)));
                Check(kind == DirectRigCommandKind.CoolCamera || fixture.CameraOwner is not null,
                    "Hardware ownership was not held during a blocking driver preamble");
                Check(kind != DirectRigCommandKind.CoolCamera || fixture.CameraOwner is null,
                    "Cooling unexpectedly reserved capture ownership");
            }
            finally
            {
                release.Set();
                fixture.AutofocusCompletion.TrySetCanceled();
                await reply.WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.WaitForRelease();
            }
        }
    }

    private static async Task CancellationCallbacksDoNotBlockAndRetainOwnership()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        fixture.AutofocusStarted = token => registration = token.Register(() =>
        {
            callbackEntered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test cancellation callback was not released");
        });
        await fixture.Send(DirectRigCommandKind.StartAutofocus);
        await fixture.WaitForAutofocus();
        var cancellationReply = Task.Run(() => fixture.Send(DirectRigCommandKind.CancelAutofocus));
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertAccepted(await cancellationReply.WaitAsync(TimeSpan.FromSeconds(1)));
            // Repeated cancellation must not replace the first callback-drain
            // task with an already-completed task and release ownership early.
            AssertAccepted(await fixture.Send(DirectRigCommandKind.CancelAutofocus)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            fixture.AutofocusCompletion.TrySetCanceled();
            await WaitUntil(() => fixture.WindowCloses == 1, "AF task did not finish while cancellation callback drained");
            await Task.Delay(50);
            Check(fixture.CameraOwner is not null,
                "Capture ownership was released while a driver cancellation callback was still running");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartSequence), "still running");
        }
        finally
        {
            release.Set();
            fixture.AutofocusCompletion.TrySetCanceled();
            await cancellationReply.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.WaitForRelease();
            registration.Dispose();
        }
    }

    private static async Task ConcurrentAutofocusRequestsHaveOneOwner()
    {
        using var fixture = new Fixture();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                AssertAccepted(await fixture.Send(DirectRigCommandKind.StartAutofocus));
                return true;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("still running", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        })).ToArray();
        start.SetResult();
        try
        {
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.WaitForAutofocus();
            Check(results.Count(accepted => accepted) == 1 && fixture.AutofocusCalls == 1,
                "Concurrent autofocus requests created multiple native runs");
        }
        finally
        {
            fixture.AutofocusCompletion.TrySetCanceled();
            await fixture.WaitForRelease();
        }
    }

    private static async Task NativeAutofocusCannotBeReenteredOrCancelled()
    {
        foreach (var canceled in new[] { false, true })
        {
            using var fixture = new Fixture();
            // N.I.N.A.'s autofocus tool owns capture until cleanup completes.
            // Its success event is not an end event: failures/cancellations
            // never emit it, and successful runs still have cleanup to do.
            fixture.CameraOwner = new object();
            fixture.Provider.AutoFocusRunStarting();
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "camera is busy");
            var cancellationReply = await fixture.Send(DirectRigCommandKind.CancelAutofocus);
            Check(cancellationReply is DirectApiEnvelope<string> { StatusCode: 200 },
                "Cancel should report no owned request when only native autofocus is running");
            await Reject(() => fixture.Send(DirectRigCommandKind.StartAutofocus), "camera is busy");
            Check(fixture.AutofocusCalls == 0 && fixture.HardwareCalls == 0,
                "A remote request reentered or cancelled native/Hocus autofocus");

            fixture.CameraOwner = null; // Native failure/cancel cleanup, without a success event.
            fixture.AutofocusStarted = _ => fixture.Provider.AutoFocusRunStarting();
            AssertAccepted(await fixture.Send(DirectRigCommandKind.StartAutofocus));
            await fixture.WaitForAutofocus();
            if (canceled) fixture.AutofocusCompletion.SetCanceled();
            else fixture.AutofocusCompletion.SetException(new InvalidOperationException("Autofocus failed"));
            await fixture.WaitForRelease();

            // An owned failure/cancellation likewise cannot latch the profile
            // into a permanently busy state after its awaited cleanup exits.
            fixture.AutofocusCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AssertAccepted(await fixture.Send(DirectRigCommandKind.StartAutofocus));
            await WaitUntil(() => fixture.AutofocusCalls == 2, "Autofocus did not recover after cleanup without a success event");
            fixture.AutofocusCompletion.SetCanceled();
            await fixture.WaitForRelease();
        }
    }

    private static async Task FalseHardwareResultsRemainVisible()
    {
        using var fixture = new Fixture();
        var mountResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HardwareTask = mountResult.Task;
        AssertAccepted(await fixture.Send(DirectRigCommandKind.UnparkMount));
        await WaitUntil(() => fixture.HardwareCalls == 1, "Unpark was not dispatched");
        mountResult.SetResult(false);
        await fixture.WaitForRelease();
        var failure = await WaitForCommandFailure(fixture, "Unpark mount");
        Check(failure.GetProperty("Error").GetString()!.Contains("did not complete", StringComparison.Ordinal),
            "Native boolean failure lost its terminal command outcome");

        var temperatureResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.TemperatureTask = temperatureResult.Task;
        AssertAccepted(await fixture.Send(DirectRigCommandKind.CoolCamera, temperature: -10, minutes: 1));
        await WaitUntil(() => fixture.TemperatureCalls == 1, "Cooling was not dispatched");
        temperatureResult.SetResult(false);
        _ = await WaitForCommandFailure(fixture, "Cool camera");

        fixture.StopGuidingResult = false; // Native "already stopped" result.
        AssertAccepted(await fixture.Send(DirectRigCommandKind.StopGuiding));
        await WaitUntil(() => fixture.GuidingStops == 1, "Guiding stop was not dispatched");
        await fixture.WaitForRelease();
        var events = await fixture.SnapshotEvents();
        Check(!events.Any(item => item.GetProperty("Event").GetString() == "CHATSTRONOMY-COMMAND-FAILED"
            && item.GetProperty("Command").GetString() == "Stop guiding"),
            "Already-stopped guiding was incorrectly reported as a failure");
    }

    private static async Task<JsonElement> WaitForCommandFailure(Fixture fixture, string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var events = await fixture.SnapshotEvents();
            foreach (var item in events)
                if (item.GetProperty("Event").GetString() == "CHATSTRONOMY-COMMAND-FAILED"
                    && item.GetProperty("Command").GetString() == command) return item;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (!condition()) await Task.Delay(10, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception(message); }
    }

    private static void AssertAccepted(object? response) =>
        Check(response is DirectApiEnvelope<string> { StatusCode: 202, Success: true },
            "Asynchronous command did not return an accepted/pending outcome");

    private static async Task Reject(Func<Task<object?>> operation, string fragment)
    {
        try { await operation(); }
        catch (InvalidOperationException exception) when (exception.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception($"Expected command rejection containing '{fragment}'");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static T Mock<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var mock = DispatchProxy.Create<T, CommandProxy>();
        ((CommandProxy)(object)mock).Handler = handler;
        return mock;
    }

    internal sealed class Fixture : IDisposable
    {
        internal bool AdvancedRunning;
        private object? cameraOwner;
        internal object? CameraOwner { get => Volatile.Read(ref cameraOwner); set => Volatile.Write(ref cameraOwner, value); }
        internal volatile int HardwareCalls, TemperatureCalls, AutofocusCalls, SequenceStops, SequenceStarts, AutofocusHistory, WindowCloses, GuidingStops, FilterCalls;
        internal bool HardwareResult = true, TemperatureResult = true, StopGuidingResult = true;
        internal Task<bool>? HardwareTask, TemperatureTask;
        internal Task<FilterInfo>? FilterTask;
        internal Action? AutofocusPreamble, FilterPreamble, TemperaturePreamble, CameraInfoRead;
        internal Action<CancellationToken>? AutofocusStarted;
        internal CancellationToken AutofocusToken;
        internal TaskCompletionSource<AutoFocusReport> AutofocusCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CommandProxy SimpleProxy;
        internal CommandProxy AdvancedProxy;
        internal IProfileService ProfileService;
        internal FilterInfo SelectedFilter;
        internal SequenceMediator Sequence;
        internal ICameraMediator Camera;
        internal ITelescopeMediator Telescope;
        internal NinaDirectDataProvider Provider;
        internal DirectAccessPolicy Access = new(new(true, false, (DirectCommandPermissions)ushort.MaxValue));

        internal Fixture(ITelescopeMediator? telescopeOverride = null)
        {
            var simple = Mock<ISimpleSequenceVM>();
            SimpleProxy = (CommandProxy)(object)simple;
            SimpleProxy.CancelSequenceCommand = new TestCommand(() => SequenceStops++);
            var seqVm = Mock<ISequence2VM>((method, args) => method.Name switch
            {
                "get_IsRunning" => AdvancedRunning,
                "get_CancelSequenceCommand" => new TestCommand(() => SequenceStops++),
                "get_StartSequenceCommand" => Mock<IAsyncCommand>((m, a) =>
                {
                    if (m.Name == "ExecuteAsync") { SequenceStarts++; return Task.CompletedTask; }
                    return null;
                }),
                _ => null,
            });
            AdvancedProxy = (CommandProxy)(object)seqVm;
            Sequence = new SequenceMediator();
            Sequence.RegisterSequenceNavigation(Mock<ISequenceNavigationVM>((method, args) => method.Name switch
            {
                "get_Initialized" => true,
                "get_Sequence2VM" => seqVm,
                "get_SimpleSequenceVM" => simple,
                _ => null,
            }));
            var cameraInfo = new CameraInfo { Connected = true, CanSetTemperature = true };
            Camera = Mock<ICameraMediator>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetInfo": CameraInfoRead?.Invoke(); return cameraInfo;
                    case "IsFreeToCapture": return CameraOwner is null || ReferenceEquals(CameraOwner, args![0]);
                    case "RegisterCaptureBlock":
                        Check(CameraOwner is null, "Duplicate capture owner"); CameraOwner = args![0]; return null;
                    case "ReleaseCaptureBlock":
                        if (ReferenceEquals(CameraOwner, args![0])) CameraOwner = null;
                        return null;
                    case "CoolCamera": case "WarmCamera":
                        Interlocked.Increment(ref TemperatureCalls);
                        TemperaturePreamble?.Invoke();
                        return TemperatureTask ?? Task.FromResult(TemperatureResult);
                    case "AbortExposure": HardwareCalls++; return null;
                    default: return null;
                }
            });
            Telescope = telescopeOverride ?? Mock<ITelescopeMediator>((method, args) =>
            {
                if (method.Name == "GetInfo") return new TelescopeInfo { Connected = true, AtPark = true, CanPark = true };
                Interlocked.Increment(ref HardwareCalls);
                return method.ReturnType == typeof(Task<bool>) ? HardwareTask ?? Task.FromResult(HardwareResult) : null;
            });
            var filter = new FilterInfo { Name = "L", Position = 0 };
            SelectedFilter = filter;
            var filterSettings = Mock<IFilterWheelSettings>((method, args) => method.Name == "get_FilterWheelFilters"
                ? new ObserveAllCollection<FilterInfo> { filter } : null);
            var profile = Mock<IProfile>((method, args) => method.Name switch
            {
                "get_FilterWheelSettings" => filterSettings,
                "get_CameraSettings" => Mock<ICameraSettings>(),
                "get_FocuserSettings" => Mock<IFocuserSettings>(),
                _ => null,
            });
            var profileService = Mock<IProfileService>((method, args) => method.Name == "get_ActiveProfile" ? profile : null);
            ProfileService = profileService;
            var window = Mock<IWindowService>((method, args) =>
            {
                if (method.Name == "Close") { WindowCloses++; return Task.CompletedTask; }
                return null;
            });
            var factory = Mock<IAutoFocusVMFactory>((method, args) => method.Name == "Create"
                ? Mock<IAutoFocusVM>((m, a) =>
                {
                    if (m.Name != "StartAutoFocus") return null;
                    AutofocusToken = (CancellationToken)a![1]!;
                    AutofocusStarted?.Invoke(AutofocusToken);
                    Interlocked.Increment(ref AutofocusCalls);
                    AutofocusPreamble?.Invoke();
                    return AutofocusCompletion.Task;
                }) : null);
            Provider = new(profileService, Telescope, Camera,
                Mock<IFilterWheelMediator>((method, args) =>
                {
                    if (method.Name == "GetInfo") return new FilterWheelInfo { Connected = true, SelectedFilter = filter };
                    if (method.Name == "ChangeFilter")
                    {
                        Interlocked.Increment(ref FilterCalls);
                        FilterPreamble?.Invoke();
                        return FilterTask ?? Task.FromResult(filter);
                    }
                    return null;
                }),
                Mock<IGuiderMediator>((method, args) =>
                {
                    if (method.Name == "GetInfo") return new global::NINA.Equipment.Equipment.MyGuider.GuiderInfo { Connected = true };
                    if (method.Name == "StopGuiding")
                    {
                        Interlocked.Increment(ref GuidingStops);
                        return Task.FromResult(StopGuidingResult);
                    }
                    return null;
                }), Mock<IRotatorMediator>(),
                Mock<IFocuserMediator>((method, args) => method.Name == "GetInfo" ? new FocuserInfo { Connected = true } : null),
                Sequence, null!, null!, null!, null!, null!, null!, Mock<IApplicationStatusMediator>(), factory,
                Mock<IImageHistoryVM>((method, args) => { if (method.Name == "AppendAutoFocusPoint") AutofocusHistory++; return null; }),
                Mock<IWindowServiceFactory>((method, args) => method.Name == "Create" ? window : null), null!,
                new(DirectEventDeliveryOptions.Default), Access);
        }

        internal Task<object?> Send(DirectRigCommandKind kind, int? filter = null, double? temperature = null, double? minutes = null) =>
            Provider.ExecuteAsync(new(Guid.NewGuid(), DirectQueryKind.Command,
                Command: new(kind, FilterId: filter, Temperature: temperature, Minutes: minutes),
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()), CancellationToken.None);

        internal (SequenceRootContainer Root, SequentialContainer Scope, object CaptureOwner) NativeSequence()
        {
            var root = new SequenceRootContainer();
            var scope = new SequentialContainer();
            root.Add(scope);
            AdvancedProxy.SequencerValue = new global::NINA.Sequencer.Sequencer(root);
            AdvancedRunning = true;
            var owner = new object();
            CameraOwner = owner; // Native Sequence2VM already owns capture.
            return (root, scope, owner);
        }

        internal async Task WaitForRelease()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (CameraOwner is not null) await Task.Delay(10, timeout.Token);
        }

        internal Task WaitForAutofocus() => WaitUntil(() => AutofocusCalls == 1, "Autofocus was not dispatched");

        internal async Task<JsonElement[]> SnapshotEvents()
        {
            var result = await Provider.ExecuteAsync(new(Guid.NewGuid(), DirectQueryKind.EventHistory), CancellationToken.None);
            return JsonSerializer.SerializeToElement(result, DirectProtocol.JsonOptions)
                .GetProperty("Response").EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        public void Dispose()
        {
            Provider.Dispose();
            AutofocusCompletion.TrySetCanceled();
        }
    }

    public class CommandProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        public bool IsRunning { get; set; }
        public ICommand? CancelSequenceCommand { get; set; }
        internal ISequencer? SequencerValue { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            var result = Handler?.Invoke(method, args);
            if (result is not null || method.ReturnType == typeof(void)) return result;
            if (method.Name == "get_IsRunning") return IsRunning;
            if (method.Name == "get_CancelSequenceCommand") return CancelSequenceCommand;
            if (method.Name == "get_Sequencer") return SequencerValue;
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var type = method.ReturnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type)
                    .Invoke(null, [type.IsValueType ? Activator.CreateInstance(type) : null]);
            }
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class TestCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
