using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json;
using Chatstronomy.NINA.Sequencing;
using NINA.Core.Enum;
using NINA.Sequencer.Container;

namespace Chatstronomy.NINA.Direct;

internal sealed partial class NinaDirectDataProvider
{
    private readonly DirectObservationState observationState = new();
    private readonly object observationSubscriptionsGate = new();
    private readonly List<(ISequenceContainer Container, INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> observationSubscriptions = [];
    private readonly List<(INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)> observationStructureSubscriptions = [];
    private readonly object schedulerObservationOwner = new();
    private long observationGeneration;
    private DateTimeOffset lastSchedulerObservation;
    private ISequenceRootContainer? observationRoot;
    private DateTimeOffset observationNotBefore;
    private string? lastSchedulerObservationKind;

    private void ResetObservationState()
    {
        lock (observationSubscriptionsGate)
        {
            Interlocked.Increment(ref observationGeneration);
            foreach (var (_, source, handler) in observationSubscriptions) source.PropertyChanged -= handler;
            observationSubscriptions.Clear();
            foreach (var (source, handler) in observationStructureSubscriptions) source.CollectionChanged -= handler;
            observationStructureSubscriptions.Clear();
            observationRoot = null;
            lastSchedulerObservation = default;
            lastSchedulerObservationKind = null;
            observationNotBefore = utcNow();
            observationState.Reset();
        }
    }

    /// Subscribe before native execution starts. No polling-based counter inference.
    private void BindObservationTargets(ISequenceRootContainer? root, long? expectedGeneration = null)
    {
        try { BindObservationTargetsCore(root, expectedGeneration); }
        catch (Exception)
        {
            ResetObservationState();
            // A third-party container cannot make optional chat telemetry fail
            // the sequence or prevent N.I.N.A. from saving an exposure.
            LogMotionCaptureWarning("observation");
        }
    }

    private void BindObservationTargetsCore(ISequenceRootContainer? root, long? expectedGeneration)
    {
        if (root is null || !eventDelivery.Current.ObservationTelemetry || !eventDelivery.Current.TargetScheduler) return;
        var generation = expectedGeneration ?? Volatile.Read(ref observationGeneration);
        if (generation != Volatile.Read(ref observationGeneration)) return;
        var visited = new HashSet<ISequenceContainer>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ISequenceContainer>();
        var targets = new List<ISequenceContainer>();
        var collections = new List<INotifyCollectionChanged>();
        pending.Push(root);
        // Do not hold our gate while taking N.I.N.A.'s item-snapshot locks:
        // its property-change callbacks may be waiting for our gate.
        while (pending.TryPop(out var candidate) && visited.Count < 4096)
        {
            if (!visited.Add(candidate) || TargetSchedulerCommandContext.IsSchedulerScope(candidate)) continue;
            if (candidate.Items is INotifyCollectionChanged collection) collections.Add(collection);
            if (candidate is IDeepSkyObjectContainer) targets.Add(candidate);
            foreach (var item in candidate.GetItemsSnapshot().OfType<ISequenceContainer>()) pending.Push(item);
        }
        lock (observationSubscriptionsGate)
        {
            if (generation != observationGeneration || observationRoot is not null
                || !eventDelivery.Current.ObservationTelemetry || !eventDelivery.Current.TargetScheduler) return;
            observationRoot = root;
            // A partial graph cannot establish an authoritative target tally.
            if (pending.Count > 0 || targets.Any(item => item is not INotifyPropertyChanged)) return;
            foreach (var collection in collections)
            {
                NotifyCollectionChangedEventHandler handler = (_, _) =>
                {
                    lock (observationSubscriptionsGate)
                        if (generation == observationGeneration) ResetObservationState();
                };
                collection.CollectionChanged += handler;
                observationStructureSubscriptions.Add((collection, handler));
            }
            foreach (var container in targets)
            {
                if (container is IDeepSkyObjectContainer targetContainer && container is INotifyPropertyChanged source)
                {
                    var previous = container.Status;
                    PropertyChangedEventHandler handler = (_, args) =>
                    {
                        if (args.PropertyName is not (null or "" or "Status")) return;
                        lock (observationSubscriptionsGate)
                        {
                            if (generation != observationGeneration || !started || !eventDelivery.Current.ObservationTelemetry
                                || !eventDelivery.Current.TargetScheduler) return;
                            var status = container.Status;
                            if (status.ToString() == "PAUSED") return;
                            if (status == previous) return;
                            previous = status;
                            ReconcileNativeObservation(container, status == SequenceEntityStatus.RUNNING);
                        }
                    };
                    source.PropertyChanged += handler;
                    observationSubscriptions.Add((container, source, handler));
                }
            }
            ReconcileNativeObservation(null, false);
        }
    }

    private void ReconcileNativeObservation(ISequenceContainer? changed, bool entered)
    {
        var active = observationSubscriptions.Select(item => item.Container)
            .Where(item => item.Status == SequenceEntityStatus.RUNNING || item.Status.ToString() == "PAUSED").Take(2).ToArray();
        // Parallel/ambiguous targets are unknown, never whichever callback won.
        if (active.Length == 0)
        {
            foreach (var item in observationSubscriptions) observationState.EndTarget(item.Container);
            return;
        }
        if (active.Length != 1) { observationState.EndTarget(); return; }
        var selected = active[0];
        var complete = entered && ReferenceEquals(changed, selected);
        observationState.BeginTarget(selected, ((IDeepSkyObjectContainer)selected).Target?.TargetName,
            utcNow(), complete, force: complete);
    }

    private JsonElement GetObservationState()
    {
        var delivery = eventDelivery.Current;
        if (delivery.ObservationTelemetry && delivery.TargetScheduler && observationRoot is null)
        {
            // Reuse the provider's already discovered root. Never dispatch to
            // the UI, query hardware, or block a sequencer instruction for chat.
            BindObservationTargets(Volatile.Read(ref sequenceFailureRoot));
        }
        lock (observationSubscriptionsGate)
        {
            delivery = eventDelivery.Current;
            if (observationSubscriptions.Count > 0) ReconcileNativeObservation(null, false);
            return observationState.Snapshot(utcNow(), delivery.ObservationTelemetry,
                delivery.TargetScheduler, delivery.Images && delivery.Sequence,
                delivery.FilterFocuserRotator, delivery.WeatherChanges);
        }
    }

    private void ObserveSchedulerTarget(string eventName, string name, DateTimeOffset sentAt, long historyGeneration)
    {
        var delivery = eventDelivery.Current;
        if (!started || !delivery.ObservationTelemetry || !delivery.TargetScheduler) return;
        lock (historyGenerationGate)
        lock (observationSubscriptionsGate)
        {
            if (historyWritesSuspended || historyGeneration != this.historyGeneration) return;
            var now = utcNow();
            if (sentAt < observationNotBefore || sentAt < now.AddSeconds(-30) || sentAt > now.AddSeconds(5)
                || sentAt < lastSchedulerObservation
                || (sentAt == lastSchedulerObservation && eventName == lastSchedulerObservationKind)) return;
            lastSchedulerObservation = sentAt;
            lastSchedulerObservationKind = eventName;
            observationState.BeginTarget(schedulerObservationOwner, name, sentAt,
                complete: eventName == "TS-NEWTARGETSTART",
                force: eventName == "TS-NEWTARGETSTART");
        }
    }

    private void ObserveSequenceBoundary(ISequenceRootContainer? root, long historyGeneration, bool beginning)
    {
        long generation;
        lock (historyGenerationGate)
        {
            if (historyWritesSuspended || historyGeneration != this.historyGeneration) return;
            ResetObservationState();
            generation = Volatile.Read(ref observationGeneration);
        }
        if (beginning) BindObservationTargets(root, generation);
    }
}
