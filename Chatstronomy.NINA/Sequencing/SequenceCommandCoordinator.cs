using System.Runtime.CompilerServices;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace Chatstronomy.NINA.Sequencing;

internal enum SequenceCommandKind
{
    Autofocus,
    ChangeFilter,
    SlewToTarget,
    CenterTarget,
    CenterRotateTarget,
}

/// <summary>
/// One bounded, run-scoped request shared by the plugin manifest and sequencer
/// parts. N.I.N.A. creates separate MEF containers for those parts, but exports
/// the same profile service to both. No hardware runs on the command thread.
/// </summary>
internal sealed class SequenceCommandCoordinator
{
    private static readonly ConditionalWeakTable<IProfileService, SequenceCommandCoordinator> Coordinators = new();
    private const int MaximumRegistrations = 128;
    private const int MaximumAncestry = 128;
    private readonly object gate = new();
    private readonly Dictionary<ChatstronomyCommandTrigger, Registration> registrations = new();
    private long registrationVersion;
    private Request? current;

    internal SequenceCommandCoordinator() { }

    internal static SequenceCommandCoordinator For(IProfileService profileService) =>
        Coordinators.GetValue(profileService, _ => new());

    internal bool IsBusy { get { lock (gate) return current is not null; } }
    internal bool IsExecutingAutofocus
    {
        get { lock (gate) return current is { Executing: true, Kind: SequenceCommandKind.Autofocus }; }
    }

    internal bool HasActiveTrigger(SequenceCommandKind kind, ISequenceRootContainer root, ISequenceContainer scope)
    {
        lock (gate) return FindRegistration(kind, root, scope) is not null;
    }

    /// <summary>
    /// Throws before accepting an unavailable/unsafe request. The returned task
    /// represents completion, never mere acceptance. Repeated requests with an
    /// explicit identical key share that task; different requests cannot queue.
    /// </summary>
    internal Task Queue(
        SequenceCommandKind kind,
        ISequenceRootContainer root,
        ISequenceContainer scope,
        Func<bool> validate,
        Func<ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> execute,
        Action<Exception>? failure = null,
        TimeSpan? timeout = null,
        TimeSpan? estimatedDuration = null,
        string? deduplicationKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(execute);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validate()) throw new InvalidOperationException("The command is no longer permitted in this N.I.N.A. profile.");
        var lifetime = timeout ?? TimeSpan.FromMinutes(30);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var estimate = estimatedDuration ?? (kind == SequenceCommandKind.ChangeFilter
            ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
        if (estimate < TimeSpan.Zero || estimate > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(estimatedDuration));

        var target = NativeTargetCommands.TryCaptureTarget(scope);
        if (target is null && kind is SequenceCommandKind.SlewToTarget or SequenceCommandKind.CenterTarget or SequenceCommandKind.CenterRotateTarget)
            throw new InvalidOperationException("There is no active native target for this sequence command.");
        Request request;
        lock (gate)
        {
            if (current is { } existing)
            {
                if (deduplicationKey is not null && existing.Kind == kind
                    && existing.Key == deduplicationKey && ReferenceEquals(existing.Root, root)
                    && ReferenceEquals(existing.Scope, scope)) return existing.Completion.Task;
                throw new InvalidOperationException("Another Chatstronomy sequence command is already queued or running.");
            }
            var registration = FindRegistration(kind, root, scope)
                ?? throw new InvalidOperationException($"Add an enabled {ChatstronomyCommandTrigger.DisplayName(kind)} trigger to the active sequential instruction set first.");
            request = new(kind, root, scope, registration, target, validate, execute, failure, estimate, deduplicationKey);
            current = request;
            // Start disabled, then arm after publication. Only one timer and
            // one cancellation registration exist for the single request.
            request.Deadline = new Timer(_ => Cancel(request,
                new TimeoutException("The queued sequence command expired before it completed.")), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            request.Deadline.Change(lifetime, Timeout.InfiniteTimeSpan);
        }
        var cancellation = cancellationToken.Register(() => Cancel(request,
            new OperationCanceledException("The queued sequence command was canceled.", cancellationToken)));
        request.SetCallerCancellation(cancellation);
        return request.Completion.Task;
    }

    internal void Invalidate(string reason)
    {
        Request? request;
        lock (gate) request = current;
        if (request is not null) Cancel(request, new OperationCanceledException(reason));
    }

    internal void ReconcilePermissions()
    {
        Request? request;
        lock (gate) request = current;
        if (request is null) return;
        try
        {
            if (!request.Validate()) Cancel(request, new OperationCanceledException("Permission for the sequence command was revoked."));
        }
        catch (Exception exception) { Cancel(request, exception); }
    }

    internal bool CancelAutofocus()
    {
        Request? request;
        lock (gate) request = current?.Kind == SequenceCommandKind.Autofocus ? current : null;
        if (request is null) return false;
        Cancel(request, new OperationCanceledException("Chatstronomy autofocus was canceled."));
        return true;
    }

    internal void NotifyExternalAutofocus()
    {
        Request? request;
        lock (gate) request = current is { Kind: SequenceCommandKind.Autofocus, Executing: false } ? current : null;
        if (request is not null) Cancel(request,
            new OperationCanceledException("The queued autofocus was superseded by an autofocus already started in N.I.N.A."));
    }

    internal void Register(ChatstronomyCommandTrigger trigger)
    {
        Unregister(trigger);
        var parent = trigger.Parent;
        var root = RootOf(parent);
        if (parent is null || root is null || trigger.Status == SequenceEntityStatus.DISABLED
            || !SafeAncestry(parent, root)) return;
        lock (gate)
        {
            if (registrations.Count < MaximumRegistrations)
                registrations[trigger] = new(trigger, parent, root, ++registrationVersion);
        }
    }

    internal void Unregister(ChatstronomyCommandTrigger trigger)
    {
        Request? request = null;
        lock (gate)
        {
            if (registrations.Remove(trigger) && current?.Registration.Trigger == trigger) request = current;
        }
        if (request is not null) Cancel(request,
            new OperationCanceledException("The sequence command trigger left its active instruction set."));
    }

    internal bool ShouldExecute(ChatstronomyCommandTrigger trigger, ISequenceItem? previous, ISequenceItem? next)
    {
        if (next is not IExposureItem exposure || !string.Equals(exposure.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase)
            || next.Parent is null) return false;
        Request? request;
        lock (gate)
        {
            request = current;
            if (request is null || request.Executing || request.Settling || request.CancellationReason is not null
                || request.Registration.Trigger != trigger) return false;
        }
        try
        {
            if (!request.Validate())
            {
                Cancel(request, new OperationCanceledException("The sequence command is no longer permitted."));
                return false;
            }
            if (!Eligible(request, trigger, next.Parent))
            {
                Cancel(request, new OperationCanceledException("The active sequence target or trigger changed before the command could run."));
                return false;
            }
            if (NearMeridianFlip(next.Parent, previous, next, request.EstimatedDuration)) return false;
            lock (gate)
            {
                if (!ReferenceEquals(current, request) || request.Executing || request.Settling
                    || request.CancellationReason is not null) return false;
                request.Next = next;
                return true;
            }
        }
        catch (Exception exception)
        {
            Cancel(request, exception);
            return false;
        }
    }

    internal async Task ExecuteAsync(ChatstronomyCommandTrigger trigger, ISequenceContainer context,
        IProgress<ApplicationStatus> progress, CancellationToken sequenceToken)
    {
        Request? request;
        lock (gate)
        {
            request = current;
            if (request is null || request.Executing || request.Settling || request.CancellationReason is not null
                || request.Registration.Trigger != trigger) return;
            request.Executing = true;
        }
        Exception? failure = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sequenceToken, request.Cancellation.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!request.Validate() || !Eligible(request, trigger, context)
                || request.Next?.Parent != context || request.Next is not IExposureItem exposure
                || !string.Equals(exposure.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sequence command's active target or permission changed.");
            // Do not create a competing capture block: this awaited callback
            // executes inside the sequencer's existing camera ownership.
            await request.Execute(context, progress, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Task cancellationDrain;
            lock (gate)
            {
                // Freeze settlement only after the hardware callback exits.
                // A cancellation already accepted must also finish every
                // registered driver callback before another command can run.
                request.Settling = true;
                cancellationDrain = request.CancellationDrain;
                failure = request.CancellationReason ?? failure;
            }
            try { await cancellationDrain.ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
            lock (gate) if (ReferenceEquals(current, request)) current = null;
            Finish(request, failure);
        }
        // Base SequenceTrigger.Run records failures in N.I.N.A.; our caller's
        // task was already completed explicitly because that base swallows them.
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool Eligible(Request request, ChatstronomyCommandTrigger trigger, ISequenceContainer context)
    {
        lock (gate)
        {
            return ReferenceEquals(current, request)
                && !request.Settling && request.CancellationReason is null
                && registrations.TryGetValue(trigger, out var active) && active.Version == request.Registration.Version
                && trigger.Status != SequenceEntityStatus.DISABLED && ReferenceEquals(trigger.Parent, active.Parent)
                && active.Parent.Status == SequenceEntityStatus.RUNNING && request.Root.Status == SequenceEntityStatus.RUNNING
                && IsDescendant(context, request.Scope) && IsDescendant(context, active.Parent)
                && (request.Target is null || request.Target.IsCurrent(context))
                && SafeAncestry(context, request.Root);
        }
    }

    private Registration? FindRegistration(SequenceCommandKind kind, ISequenceRootContainer root, ISequenceContainer scope)
    {
        if (root.Status != SequenceEntityStatus.RUNNING || scope.Status != SequenceEntityStatus.RUNNING
            || !SafeAncestry(scope, root)) return null;
        // Choose the closest active ancestor, so another target's trigger
        // cannot accidentally accept a request meant for the current target.
        foreach (var ancestor in Ancestors(scope))
        {
            foreach (var registration in registrations.Values)
                if (registration.Trigger.Kind == kind && registration.Trigger.Status != SequenceEntityStatus.DISABLED
                    && ReferenceEquals(registration.Trigger.Parent, registration.Parent)
                    && ReferenceEquals(registration.Parent, ancestor) && ReferenceEquals(registration.Root, root)
                    && registration.Parent.Status == SequenceEntityStatus.RUNNING) return registration;
        }
        return null;
    }

    internal static bool SafeAncestry(ISequenceContainer scope, ISequenceRootContainer root)
    {
        foreach (var ancestor in Ancestors(scope))
        {
            if (ancestor is ParallelContainer || ancestor.Strategy is ParallelStrategy) return false;
            if (ReferenceEquals(ancestor, root)) return true;
        }
        return false;
    }

    internal static ISequenceRootContainer? RootOf(ISequenceContainer? scope) =>
        Ancestors(scope).OfType<ISequenceRootContainer>().FirstOrDefault();

    private static bool IsDescendant(ISequenceContainer scope, ISequenceContainer ancestor) =>
        Ancestors(scope).Any(item => ReferenceEquals(item, ancestor));

    internal static IEnumerable<ISequenceContainer> Ancestors(ISequenceContainer? scope)
    {
        var seen = new HashSet<ISequenceContainer>(ReferenceEqualityComparer.Instance);
        while (scope is not null && seen.Count < MaximumAncestry && seen.Add(scope))
        {
            yield return scope;
            scope = scope.Parent;
        }
    }

    private static bool NearMeridianFlip(ISequenceContainer context, ISequenceItem? previous,
        ISequenceItem next, TimeSpan estimate)
    {
        var duration = estimate + next.GetEstimatedDuration();
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1)) return true;
        var finish = DateTime.Now + TimeSpan.FromSeconds(duration.TotalSeconds * 1.5);
        foreach (var ancestor in Ancestors(context))
        {
            if (ancestor is not ITriggerable triggerable) continue;
            foreach (var flip in triggerable.GetTriggersSnapshot().OfType<IMeridianFlipTrigger>())
            {
                if (flip.Status == SequenceEntityStatus.DISABLED) continue;
                // Refresh native flip timing even when the Chatstronomy trigger
                // appears before the flip in the UI. ShouldTrigger only decides;
                // the sequencer remains responsible for actually running it.
                if (flip.ShouldTrigger(previous!, next)) return true;
                if (flip.LatestFlipTime != DateTime.MinValue && finish >= flip.LatestFlipTime) return true;
            }
        }
        return false;
    }

    private void Cancel(Request request, Exception reason)
    {
        bool finish;
        lock (gate)
        {
            if (!ReferenceEquals(current, request) || request.Settling) return;
            request.CancellationReason ??= reason;
            // CancelAsync signals immediately but schedules third-party token
            // callbacks off the caller's thread. Publish its drain task under
            // the same lock as execution so completion cannot race cleanup.
            if (!request.Cancellation.IsCancellationRequested)
                request.CancellationDrain = request.Cancellation.CancelAsync();
            finish = !request.Executing;
            if (finish) request.Settling = true;
        }
        if (finish) _ = FinishPendingCancellationAsync(request);
    }

    private async Task FinishPendingCancellationAsync(Request request)
    {
        try { await request.CancellationDrain.ConfigureAwait(false); }
        catch { /* Cancellation errors are observed; preserve the original reason. */ }
        lock (gate) if (ReferenceEquals(current, request)) current = null;
        Finish(request, request.CancellationReason);
    }

    private static void Finish(Request request, Exception? exception)
    {
        if (Interlocked.Exchange(ref request.Finished, 1) != 0) return;
        request.Deadline?.Dispose();
        request.DisposeCallerCancellation();
        request.Cancellation.Dispose();
        if (exception is null) request.Completion.TrySetResult();
        else
        {
            try { request.Failure?.Invoke(exception); } catch { /* Completion must not be lost to observer errors. */ }
            request.Completion.TrySetException(exception);
        }
    }

    private sealed record Registration(ChatstronomyCommandTrigger Trigger, ISequenceContainer Parent,
        ISequenceRootContainer Root, long Version);

    private sealed class Request(SequenceCommandKind kind, ISequenceRootContainer root, ISequenceContainer scope,
        Registration registration, NativeTargetCommands.TargetSnapshot? target, Func<bool> validate,
        Func<ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> execute,
        Action<Exception>? failure, TimeSpan estimate, string? key)
    {
        private readonly object cancellationGate = new();
        private CancellationTokenRegistration callerCancellation;
        internal SequenceCommandKind Kind { get; } = kind;
        internal ISequenceRootContainer Root { get; } = root;
        internal ISequenceContainer Scope { get; } = scope;
        internal Registration Registration { get; } = registration;
        internal NativeTargetCommands.TargetSnapshot? Target { get; } = target;
        internal Func<bool> Validate { get; } = validate;
        internal Func<ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> Execute { get; } = execute;
        internal Action<Exception>? Failure { get; } = failure;
        internal TimeSpan EstimatedDuration { get; } = estimate;
        internal string? Key { get; } = key;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Timer? Deadline;
        internal bool Executing;
        internal bool Settling;
        internal Task CancellationDrain = Task.CompletedTask;
        internal int Finished;
        internal ISequenceItem? Next;
        internal Exception? CancellationReason;

        internal void SetCallerCancellation(CancellationTokenRegistration value)
        {
            lock (cancellationGate)
            {
                if (Volatile.Read(ref Finished) == 0) { callerCancellation = value; return; }
            }
            value.Unregister();
        }

        internal void DisposeCallerCancellation()
        {
            CancellationTokenRegistration value;
            lock (cancellationGate) value = callerCancellation;
            // Nonblocking, including when finishing from that callback itself.
            value.Unregister();
        }
    }
}
