using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Sequencing;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Utility.AutoFocus;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace Chatstronomy.NINA.Direct;

internal sealed partial class NinaDirectDataProvider
{
    private readonly SequenceCommandCoordinator sequenceCommands;
    private readonly NativeTargetCommands? targetCommands;
    private CommandAdmission? commandAdmission;
    private CommandAdmission? idleCommand;
    private readonly object commandAdmissionGate = new();
    private long sequenceControlEpoch;

    // Commands share the same admission queue as N.I.N.A.'s UI start buttons.
    // Never block that dispatcher waiting for the hardware task to finish.
    private static async Task<object?> DispatchCommandAsync(
        Func<object?> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        return await dispatcher.InvokeAsync(action,
            System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task
            .ConfigureAwait(false);
    }

    private DirectApiEnvelope<string> ExecuteGuardedCommand(
        DirectRigCommand command, DirectQuery query, CancellationToken cancellationToken,
        long generation, Action authorize)
    {
        lock (commandAdmissionGate)
        {
            return AdmitCommand(command, query, cancellationToken, generation, authorize);
        }
    }

    private DirectApiEnvelope<string> AdmitCommand(
        DirectRigCommand command, DirectQuery query, CancellationToken cancellationToken,
        long generation, Action authorize)
    {
        authorize();
        // Temperature changes do not take camera/exposure ownership. They
        // remain available during a sequence or an injected operation.
        if (command.Kind is DirectRigCommandKind.CoolCamera or DirectRigCommandKind.WarmCamera)
        {
            return ExecuteLegacyCommand(command, query, cancellationToken, generation, authorize);
        }
        if (command.Kind == DirectRigCommandKind.CancelAutofocus)
        {
            var cancelled = sequenceCommands.CancelAutofocus();
            var current = Volatile.Read(ref idleCommand);
            if (current?.Kind == DirectRigCommandKind.StartAutofocus)
            {
                current.Cancel();
                cancelled = true;
            }
            return cancelled
                ? DirectApiEnvelope<string>.Accepted("Chatstronomy autofocus cancellation requested")
                : DirectApiEnvelope<string>.Ok("No Chatstronomy autofocus request is pending or running");
        }
        var state = ReadSequenceCommandState();
        if (command.Kind == DirectRigCommandKind.StopSequence)
        {
            sequenceCommands.Invalidate("Sequence stop requested.");
            if (state.SimpleRunning)
            {
                if (state.SimpleOwner is null
                    || ReadCommandProperty(state.SimpleOwner, "CancelSequenceCommand") is not ICommand cancel
                    || !cancel.CanExecute(null))
                {
                    throw new InvalidOperationException("The simple sequence cannot be stopped remotely. Stop it in N.I.N.A.");
                }
                authorize();
                var epoch = Volatile.Read(ref sequenceControlEpoch);
                var stopTask = Task.Run(() =>
                {
                    authorize();
                    if (epoch != Volatile.Read(ref sequenceControlEpoch))
                        throw new InvalidOperationException("The sequence run changed before the stop request.");
                    cancel.Execute(null);
                });
                ObserveCommand(stopTask, "Stop simple sequence", generation);
                return DirectApiEnvelope<string>.Accepted("Simple sequence stop requested");
            }
            if (!state.AdvancedRunning && !state.ObservedRunning)
            {
                return DirectApiEnvelope<string>.Ok("No sequence is running");
            }
            return ExecuteLegacyCommand(command, query, cancellationToken, generation, authorize);
        }

        var queuedKind = SequenceKind(command.Kind);
        if (state.AnyRunning)
        {
            if (queuedKind is null || !state.AdvancedRunning || state.SimpleRunning)
            {
                throw new InvalidOperationException(
                    $"{command.Kind} is not available while a sequence is running. "
                    + "Use /stop-sequence and wait for it to stop first.");
            }
            if (Volatile.Read(ref idleCommand) is not null)
            {
                throw new InvalidOperationException("A previous Chatstronomy hardware operation is still finishing.");
            }
            var sequenceEpoch = Volatile.Read(ref sequenceControlEpoch);
            var (root, scope) = ActiveSequenceCommandContext();
            var targetSnapshot = NativeTargetCommands.TryCaptureTarget(scope);
            ValidateInjectedEquipment(command);
            authorize();
            var task = sequenceCommands.Queue(queuedKind.Value, root, scope,
                validate: () => IsQueuedCommandCurrent(command, generation, sequenceEpoch, root),
                execute: (context, progress, token) => Task.Run(() => ExecuteInjectedCommandAsync(
                    command, context, progress, token, generation, targetSnapshot)),
                failure: exception => AddCommandFailureIfCurrent(
                    command.Kind.ToString(), exception.Message, generation),
                estimatedDuration: EstimateSequenceCommand(command),
                deduplicationKey: command.FilterId?.ToString(),
                cancellationToken: profileSession.Token);
            // The coordinator emits a terminal failure when an accepted
            // request is dropped. Observe its task without blocking the Hub.
            _ = task.ContinueWith(completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return DirectApiEnvelope<string>.Accepted(
                $"{CommandLabel(command.Kind)} queued for the next light exposure boundary in this sequence target");
        }

        if (sequenceCommands.IsBusy || Volatile.Read(ref idleCommand) is not null)
        {
            throw new InvalidOperationException("A Chatstronomy hardware operation is still running or cancelling. Wait for it to finish.");
        }
        if (command.Kind == DirectRigCommandKind.StartSequence)
        {
            RequireCaptureAvailable();
            // N.I.N.A. itself takes ownership synchronously when starting.
            return ExecuteLegacyCommand(command, query, cancellationToken, generation, authorize);
        }
        if (command.Kind == DirectRigCommandKind.AbortExposure)
        {
            // A standalone imaging exposure owns the camera; unlike an AF
            // or filter request, abort is meant to act on that exposure.
            return ExecuteLegacyCommand(command, query, cancellationToken, generation, () =>
            {
                authorize();
                if (ReadSequenceCommandState().AnyRunning || Volatile.Read(ref idleCommand) is not null)
                    throw new InvalidOperationException("A sequence or Chatstronomy operation acquired the camera before the abort request.");
            });
        }

        RequireCaptureAvailable();
        var admission = new CommandAdmission(command.Kind, camera, profileSession.Token);
        camera.RegisterCaptureBlock(admission);
        Volatile.Write(ref idleCommand, admission);
        commandAdmission = admission;
        try
        {
            authorize();
            DirectApiEnvelope<string> response;
            if (queuedKind is not null)
            {
                ValidateInjectedEquipment(command);
                ISequenceContainer? context = null;
                if (command.Kind is DirectRigCommandKind.SlewToTarget
                    or DirectRigCommandKind.CenterTarget or DirectRigCommandKind.CenterRotateTarget)
                {
                    var targets = sequence.GetAllTargetsInAdvancedSequence();
                    if (targets is null || targets.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "Load exactly one advanced-sequence target for an idle target move, "
                            + "or run the target with the matching Chatstronomy trigger.");
                    }
                    context = targets[0];
                }
                authorize();
                var snapshot = context is null ? null : NativeTargetCommands.CaptureTarget(context);
                admission.Operation = Task.Run(() => ExecuteInjectedCommandAsync(command, context,
                    CreateProgress(), admission.Stop.Token, generation, snapshot));
                ObserveCommand(admission.Operation, command.Kind.ToString(), generation);
                response = DirectApiEnvelope<string>.Accepted($"{CommandLabel(command.Kind)} requested");
            }
            else
            {
                response = ExecuteLegacyCommand(command, query, cancellationToken, generation, authorize);
            }
            return response;
        }
        finally
        {
            commandAdmission = null;
            _ = CompleteAdmissionAsync(admission);
        }
    }

    private async Task CompleteAdmissionAsync(CommandAdmission admission)
    {
        try
        {
            if (admission.Operation is not null)
            {
                await admission.Operation.ConfigureAwait(false);
            }
        }
        catch (Exception) { /* ObserveCommand reports the operation outcome. */ }
        finally
        {
            // Keep the capture block through cancellation cleanup too. A
            // second request must never overtake a still-unwinding AF run.
            await admission.DrainCancellationAsync().ConfigureAwait(false);
            await DispatchCommandAsync(() =>
            {
                lock (commandAdmissionGate)
                {
                    admission.Dispose();
                    Interlocked.CompareExchange(ref idleCommand, null, admission);
                }
                return null;
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void RequireCaptureAvailable()
    {
        if (!camera.IsFreeToCapture(this))
        {
            throw new InvalidOperationException(
                "N.I.N.A.'s camera is busy with another operation. Wait for it to finish before this command.");
        }
    }

    private void CancelIdleCommand() => Volatile.Read(ref idleCommand)?.Cancel();

    private bool IsQueuedCommandCurrent(DirectRigCommand command, long generation, long sequenceEpoch,
        ISequenceRootContainer root)
    {
        if (generation != Volatile.Read(ref commandGeneration)
            || sequenceEpoch != Volatile.Read(ref sequenceControlEpoch) || profileSession.IsCancellationRequested)
        {
            return false;
        }
        accessPolicy.RequireRemoteControl(command);
        var state = ReadSequenceCommandState();
        return sequenceEpoch == Volatile.Read(ref sequenceControlEpoch)
            && state.AdvancedRunning && !state.SimpleRunning
            && ReferenceEquals(root, NinaDirectSequenceSnapshot.TryGetSequenceRoot(sequence));
    }

    private (ISequenceRootContainer Root, ISequenceContainer Scope) ActiveSequenceCommandContext()
    {
        var root = NinaDirectSequenceSnapshot.TryGetSequenceRoot(sequence)
            ?? throw new InvalidOperationException("The active sequence context is unavailable.");
        var running = sequence.GetAdvancedSequencerCurrentRunningItems();
        var scopes = running?.Where(item => item is not ISequenceContainer)
            .Select(item => item.Parent).Where(parent => parent is not null).Distinct().ToArray();
        if (scopes is not { Length: 1 })
        {
            throw new InvalidOperationException(
                "There is no single active sequence instruction to queue against. "
                + "Try again while the target is running sequentially.");
        }
        return (root, scopes[0]!);
    }

    private void ValidateInjectedEquipment(DirectRigCommand command)
    {
        if (command.Kind == DirectRigCommandKind.StartAutofocus)
        {
            if (!camera.GetInfo().Connected || !focuser.GetInfo().Connected)
            {
                throw new InvalidOperationException("Autofocus requires a connected camera and focuser.");
            }
        }
        else if (command.Kind == DirectRigCommandKind.ChangeFilter)
        {
            _ = ResolveCommandFilter(command.FilterId);
        }
        else if (!telescope.GetInfo().Connected || telescope.GetInfo().AtPark)
        {
            throw new InvalidOperationException("Target moves require a connected, unparked mount.");
        }
        else if (telescope.GetInfo().Slewing)
        {
            throw new InvalidOperationException("The mount is already moving. Wait for it to stop before requesting a target move.");
        }
        if (command.Kind is DirectRigCommandKind.CenterTarget or DirectRigCommandKind.CenterRotateTarget)
        {
            if (!camera.GetInfo().Connected)
                throw new InvalidOperationException("Centering requires a connected camera.");
            if (command.Kind == DirectRigCommandKind.CenterRotateTarget && !rotator.GetInfo().Connected)
                throw new InvalidOperationException("Centering and rotating requires a connected rotator.");
        }
    }

    private global::NINA.Core.Model.Equipment.FilterInfo ResolveCommandFilter(int? filterId)
    {
        if (!filterWheel.GetInfo().Connected)
        {
            throw new InvalidOperationException("Filter wheel is not connected.");
        }
        if (filterId is null)
        {
            throw new InvalidOperationException("A filter ID is required.");
        }
        var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
        return filters.FirstOrDefault(filter => filter.Position == filterId.Value)
            ?? (filterId >= 0 && filterId < filters.Count ? filters[filterId.Value] : null)
            ?? throw new InvalidOperationException($"Filter ID {filterId.Value} does not exist.");
    }

    private async Task ExecuteInjectedCommandAsync(DirectRigCommand command,
        ISequenceContainer? context, IProgress<ApplicationStatus> progress,
        CancellationToken cancellationToken, long generation,
        NativeTargetCommands.TargetSnapshot? targetSnapshot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref commandGeneration))
        {
            throw new InvalidOperationException("The hardware command was revoked.");
        }
        accessPolicy.RequireRemoteControl(command);
        ValidateInjectedEquipment(command);
        void Reauthorize()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref commandGeneration))
                throw new InvalidOperationException("The hardware command was revoked.");
            accessPolicy.RequireRemoteControl(command);
            if (context is not null && (targetSnapshot?.IsCurrent(context) == false
                || targetSnapshot is null && NativeTargetCommands.TryCaptureTarget(context) is not null))
                throw new InvalidOperationException("The active target changed after this command was requested.");
        }
        Reauthorize();
        switch (command.Kind)
        {
            case DirectRigCommandKind.StartAutofocus:
                await RunOwnedAutofocusAsync(progress, cancellationToken, generation, Reauthorize).ConfigureAwait(false);
                break;
            case DirectRigCommandKind.ChangeFilter:
                var selected = ResolveCommandFilter(command.FilterId);
                Reauthorize();
                var changed = await filterWheel.ChangeFilter(selected, cancellationToken, progress).ConfigureAwait(false);
                if (changed?.Position != selected.Position)
                {
                    throw new InvalidOperationException("The requested filter change did not complete.");
                }
                break;
            default:
                if (targetCommands is null || context is null || targetSnapshot is null)
                {
                    throw new InvalidOperationException("N.I.N.A.'s target operation services are unavailable.");
                }
                await targetCommands.ExecuteAsync(SequenceKind(command.Kind)!.Value,
                    targetSnapshot, context, progress, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunOwnedAutofocusAsync(IProgress<ApplicationStatus> progress,
        CancellationToken cancellationToken, long commandEpoch, Action reauthorize)
    {
        global::NINA.Core.Utility.WindowService.IWindowService? window = null;
        global::NINA.WPF.Base.Interfaces.ViewModel.IAutoFocusVM? vm = null;
        var autofocusEpoch = Volatile.Read(ref autofocusCaptureGeneration);
        try
        {
            await DispatchCommandAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (commandEpoch != Volatile.Read(ref commandGeneration))
                {
                    throw new InvalidOperationException("Autofocus was revoked before execution.");
                }
                accessPolicy.RequireRemoteControl(new(DirectRigCommandKind.StartAutofocus));
                reauthorize();
                window = windowFactory.Create();
                vm = autoFocusFactory.Create();
                window.Show(vm, "Chatstronomy Autofocus", ResizeMode.CanResize, WindowStyle.ToolWindow);
                return null;
            }, cancellationToken).ConfigureAwait(false);
            var report = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (commandEpoch != Volatile.Read(ref commandGeneration))
                    throw new InvalidOperationException("Autofocus was revoked before execution.");
                accessPolicy.RequireRemoteControl(new(DirectRigCommandKind.StartAutofocus));
                var selected = filterWheel.GetInfo().SelectedFilter;
                reauthorize();
                return (vm ?? throw new InvalidOperationException("Autofocus did not start."))
                    .StartAutoFocus(selected, cancellationToken, progress);
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (report is null)
            {
                throw new InvalidOperationException("No autofocus report was returned.");
            }
            // Keep the selected native/Hocus Focus implementation and normal
            // report/history path. Do not let a privacy toggle suppress NINA's
            // own local autofocus history.
            if (commandEpoch == Volatile.Read(ref commandGeneration))
            {
                TryCacheObservedAutofocusReport(SerializeObservedAutofocusReport(report),
                    autofocusEpoch, eventDelivery.Current.Autofocus);
                imageHistory.AppendAutoFocusPoint(report);
            }
        }
        finally
        {
            if (window is not null)
            {
                await DispatchCommandAsync(() => { _ = window.Close(); return null; }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static SequenceCommandKind? SequenceKind(DirectRigCommandKind command) => command switch
    {
        DirectRigCommandKind.StartAutofocus => SequenceCommandKind.Autofocus,
        DirectRigCommandKind.ChangeFilter => SequenceCommandKind.ChangeFilter,
        DirectRigCommandKind.SlewToTarget => SequenceCommandKind.SlewToTarget,
        DirectRigCommandKind.CenterTarget => SequenceCommandKind.CenterTarget,
        DirectRigCommandKind.CenterRotateTarget => SequenceCommandKind.CenterRotateTarget,
        _ => null,
    };

    private static string CommandLabel(DirectRigCommandKind command) => command switch
    {
        DirectRigCommandKind.StartAutofocus => "Autofocus",
        DirectRigCommandKind.ChangeFilter => "Filter change",
        DirectRigCommandKind.SlewToTarget => "Slew to target",
        DirectRigCommandKind.CenterTarget => "Target centering",
        DirectRigCommandKind.CenterRotateTarget => "Target centering and rotation",
        _ => command.ToString(),
    };

    private TimeSpan EstimateSequenceCommand(DirectRigCommand command)
    {
        if (command.Kind == DirectRigCommandKind.ChangeFilter) return TimeSpan.FromSeconds(30);
        var estimate = TimeSpan.FromMinutes(10);
        if (command.Kind == DirectRigCommandKind.StartAutofocus)
        {
            // Use the native exposure/step/settling/attempt estimate rather
            // than underestimating slow autofocus configurations near a flip.
            var native = new global::NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus(
                profileService, imageHistory, camera, filterWheel, focuser, autoFocusFactory)
                .GetEstimatedDuration();
            if (native > estimate) estimate = native;
        }
        return estimate;
    }

    private Task RunHardwareCommand(Func<CancellationToken, Task> operation,
        Action authorize, bool falseMeansFailure = true)
    {
        var token = commandAdmission?.Stop.Token ?? profileSession.Token;
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            authorize();
            var task = operation(token);
            await task.ConfigureAwait(false);
            if (falseMeansFailure && task is Task<bool> result && !result.Result)
                throw new InvalidOperationException("N.I.N.A. reported that the requested hardware operation did not complete.");
        });
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception) { /* Cancellation callback failures must not block N.I.N.A. */ }
    }

    private SequenceCommandState ReadSequenceCommandState()
    {
        EnsureSequenceReady();
        var advanced = sequence.IsAdvancedSequenceRunning();
        var navigation = sequence.GetType().GetField("sequenceNavigation",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sequence);
        var simpleOwner = ReadCommandProperty(navigation, "SimpleSequenceVM");
        var simple = ReadCommandProperty(simpleOwner, "IsRunning") as bool?;
        if (simple is null)
        {
            throw new InvalidOperationException(
                "N.I.N.A.'s simple-sequence state is unavailable; hardware commands are paused for safety.");
        }
        return new(advanced, simple.Value, Volatile.Read(ref sequenceRunning), simpleOwner);
    }

    private static object? ReadCommandProperty(object? owner, string name) => owner?.GetType()
        .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?.GetValue(owner);

    private sealed record SequenceCommandState(bool AdvancedRunning, bool SimpleRunning,
        bool ObservedRunning, object? SimpleOwner)
    {
        internal bool AnyRunning => AdvancedRunning || SimpleRunning || ObservedRunning;
    }

    private sealed class CommandAdmission : IDisposable
    {
        private readonly object cancellationGate = new();
        private readonly ICameraMediator camera;
        private readonly CancellationTokenRegistration profileCancellation;
        private Task cancellation = Task.CompletedTask;
        private bool closing;
        internal DirectRigCommandKind Kind { get; }
        internal CancellationTokenSource Stop { get; } = new();
        internal Task? Operation { get; set; }
        internal CommandAdmission(DirectRigCommandKind kind, ICameraMediator camera, CancellationToken profileToken)
        {
            Kind = kind;
            this.camera = camera;
            profileCancellation = profileToken.Register(Cancel);
        }
        internal void Cancel()
        {
            lock (cancellationGate)
            {
                if (!closing && !Stop.IsCancellationRequested)
                    cancellation = ObserveCancellationAsync(Stop.CancelAsync());
            }
        }
        internal Task DrainCancellationAsync()
        {
            lock (cancellationGate)
            {
                closing = true;
                profileCancellation.Unregister();
                return cancellation;
            }
        }
        public void Dispose()
        {
            camera.ReleaseCaptureBlock(this);
            Stop.Dispose();
        }
    }
}
