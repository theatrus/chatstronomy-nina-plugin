using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using NINA.Core.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Native implementation of the Direct read surface used by
/// Chatstronomy. It reads live device state from N.I.N.A. mediators and keeps
/// only bounded callback history; it does not host an HTTP server.
/// </summary>
internal sealed class NinaDirectDataProvider :
    INinaDirectDataProvider,
    IFocuserConsumer,
    ISafetyMonitorConsumer,
    ISubscriber
{
    private const int EventHistoryCapacity = 10_000;
    private const int ImageHistoryCapacity = 500;
    private const int GuideHistoryCapacity = 10_000;
    /// Forwarded log lines live in their own ring. Sharing the equipment ring
    /// let an evening of INFO chatter evict every real event, leaving the
    /// consumer's state reconstruction with nothing to read.
    private const int LogHistoryCapacity = 500;
    private static readonly Regex LocalPathPattern = new(
        "(?:[A-Za-z]:[\\\\/]|\\\\\\\\|/(?:Users|home|var|tmp|opt)/)[^\"'\\r\\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] TargetSchedulerTopics =
    [
        "TargetScheduler-WaitStart",
        "TargetScheduler-NewTargetStart",
        "TargetScheduler-TargetStart",
    ];

    private readonly IProfileService profileService;
    private readonly ITelescopeMediator telescope;
    private readonly ICameraMediator camera;
    private readonly IFilterWheelMediator filterWheel;
    private readonly IGuiderMediator guider;
    private readonly IRotatorMediator rotator;
    private readonly IFocuserMediator focuser;
    private readonly ISequenceMediator sequence;
    private readonly ISafetyMonitorMediator safetyMonitor;
    private readonly IImageSaveMediator imageSave;
    private readonly IApplicationStatusMediator applicationStatus;
    private readonly IAutoFocusVMFactory autoFocusFactory;
    private readonly IImageHistoryVM imageHistory;
    private readonly IWindowServiceFactory windowFactory;
    private readonly IMessageBroker messageBroker;
    private readonly DirectEventDeliveryPolicy eventDelivery;
    private readonly DirectAccessPolicy accessPolicy;
    private readonly NinaLogWatcher logWatcher;
    private readonly NinaNotificationWatcher notificationWatcher;
    private readonly BoundedHistory<Dictionary<string, object?>> events =
        new(EventHistoryCapacity);
    private readonly BoundedHistory<Dictionary<string, object?>> logEvents =
        new(LogHistoryCapacity);
    private readonly BoundedHistory<DirectSavedImage> images =
        new(ImageHistoryCapacity);
    private readonly BoundedHistory<DirectGuideStep> guideSteps =
        new(GuideHistoryCapacity);
    private readonly object sequenceGate = new();
    private CancellationTokenSource? sequenceSubscriptionStop;
    private bool sequenceSubscribed;
    private readonly object commandGate = new();
    private readonly object commandGenerationGate = new();
    private readonly object historyGenerationGate = new();
    private readonly object autofocusReportGate = new();
    private readonly object safetyStateGate = new();
    private readonly object directSessionGate = new();
    private readonly string autofocusReportDirectory;
    private CancellationTokenSource? guideCommandStop;
    private CancellationTokenSource? cameraCommandStop;
    private CancellationTokenSource? autofocusCommandStop;
    private CancellationTokenSource? eventCaptureStop;
    private DirectHistorySession directSession = new();
    private CancellationTokenSource profileSession = new();
    private JsonElement? lastAutofocusReport;
    private DirectAutofocusCompletion? pendingAutofocusCompletion;
    private long pendingAutofocusGeneration;
    private long pendingAutofocusHistoryGeneration;
    private long autofocusCaptureGeneration;
    private long imageCaptureGeneration;
    private long guideCaptureGeneration;
    private long historyGeneration;
    private bool historyWritesSuspended;
    private DirectSafetyState safetyState;
    private long guideStepId;
    private long commandGeneration;
    private volatile bool started;

    internal NinaDirectDataProvider(
        IProfileService profileService,
        ITelescopeMediator telescope,
        ICameraMediator camera,
        IFilterWheelMediator filterWheel,
        IGuiderMediator guider,
        IRotatorMediator rotator,
        IFocuserMediator focuser,
        ISequenceMediator sequence,
        ISafetyMonitorMediator safetyMonitor,
        IImageSaveMediator imageSave,
        IApplicationStatusMediator applicationStatus,
        IAutoFocusVMFactory autoFocusFactory,
        IImageHistoryVM imageHistory,
        IWindowServiceFactory windowFactory,
        IMessageBroker messageBroker,
        DirectEventDeliveryPolicy eventDelivery,
        DirectAccessPolicy accessPolicy,
        string? autofocusReportDirectory = null)
    {
        this.profileService = profileService;
        this.telescope = telescope;
        this.camera = camera;
        this.filterWheel = filterWheel;
        this.guider = guider;
        this.rotator = rotator;
        this.focuser = focuser;
        this.sequence = sequence;
        this.safetyMonitor = safetyMonitor;
        this.imageSave = imageSave;
        this.applicationStatus = applicationStatus;
        this.autoFocusFactory = autoFocusFactory;
        this.imageHistory = imageHistory;
        this.windowFactory = windowFactory;
        this.messageBroker = messageBroker;
        this.eventDelivery = eventDelivery;
        this.accessPolicy = accessPolicy;
        this.autofocusReportDirectory = autofocusReportDirectory
            ?? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
        logWatcher = new NinaLogWatcher(RecordLog);
        notificationWatcher = new NinaNotificationWatcher(RecordNotification);
    }

    public DirectCapabilities Capabilities => new(
        EventHistory: true,
        ImageHistory: true,
        Thumbnails: true,
        Sequence: true,
        EquipmentSnapshots: true,
        AutofocusDetails: true,
        GuiderGraph: true,
        Commands: accessPolicy.Current.CommandsEnabled);

    public CancellationToken ProfileSessionToken =>
        Volatile.Read(ref profileSession).Token;

    public CancellationToken DirectSessionToken =>
        Volatile.Read(ref directSession).Cancellation.Token;

    public Guid DirectSessionId => Volatile.Read(ref directSession).Id;

    public void Start()
    {
        if (started)
        {
            return;
        }

        // Mark started before wiring anything up. Stop() early-returns when
        // this is false, so setting it last meant a throw part-way through
        // left the handlers, the log tail, and the toast hook attached with no
        // way to detach them — a zombie uploader after the user disables the
        // plugin.
        started = true;
        eventCaptureStop = new CancellationTokenSource();

        telescope.Connected += TelescopeConnected;
        telescope.Disconnected += TelescopeDisconnected;
        telescope.BeforeMeridianFlip += TelescopeBeforeMeridianFlip;
        telescope.AfterMeridianFlip += TelescopeAfterMeridianFlip;
        telescope.Homed += TelescopeHomed;
        telescope.Parked += TelescopeParked;
        telescope.Unparked += TelescopeUnparked;

        camera.Connected += CameraConnected;
        camera.Disconnected += CameraDisconnected;
        camera.DownloadTimeout += CameraDownloadTimeout;

        filterWheel.Connected += FilterWheelConnected;
        filterWheel.Disconnected += FilterWheelDisconnected;
        filterWheel.FilterChanged += FilterWheelChanged;

        guider.Connected += GuiderConnected;
        guider.Disconnected += GuiderDisconnected;
        guider.AfterDither += GuiderDithered;
        guider.GuidingStarted += GuiderStarted;
        guider.GuidingStopped += GuiderStopped;
        guider.GuideEvent += GuiderGuideEvent;

        rotator.Connected += RotatorConnected;
        rotator.Disconnected += RotatorDisconnected;
        rotator.Moved += RotatorMoved;
        rotator.MovedMechanical += RotatorMovedMechanical;
        rotator.Synced += RotatorSynced;

        focuser.Connected += FocuserConnected;
        focuser.Disconnected += FocuserDisconnected;
        focuser.RegisterConsumer(this);

        safetyMonitor.Connected += SafetyConnected;
        safetyMonitor.Disconnected += SafetyDisconnected;
        safetyMonitor.IsSafeChanged += SafetyChanged;
        safetyMonitor.RegisterConsumer(this);

        imageSave.ImageSaved += ImageSaved;
        foreach (var topic in TargetSchedulerTopics)
        {
            messageBroker.Subscribe(topic, this);
        }
        ApplyLogDeliveryOptions();
        _ = notificationWatcher.Start(CaptureHistoryGeneration());
        sequenceSubscriptionStop = new CancellationTokenSource();
        _ = SubscribeToSequenceWhenReadyAsync(sequenceSubscriptionStop.Token);
    }

    /// Start or stop the log tail to match the current selection.
    ///
    /// Nothing needs to read the N.I.N.A. log until at least one level is
    /// ticked, and all of them default to off — so for most users this keeps
    /// the tail from opening, decoding, and parsing the log all night only to
    /// discard every line. Called again whenever the options change.
    public void ApplyLogDeliveryOptions()
    {
        if (!started)
        {
            logWatcher.Stop();
            return;
        }
        if (eventDelivery.Current.AnyLogLevelEnabled)
        {
            logWatcher.Start(CaptureHistoryGeneration());
        }
        else
        {
            logWatcher.Stop();
        }
    }

    public void RevokeRemoteControl()
    {
        // A profile can grant the same command as its predecessor. Advancing
        // the generation first still invalidates every callback captured by
        // that predecessor, including callbacks not yet given a device token.
        lock (commandGenerationGate)
        {
            Interlocked.Increment(ref commandGeneration);
        }
        CancelOutstandingCommands();
    }

    public void RotateDirectSession()
    {
        // Publish the successor first so the reconnect requested after a
        // privacy-policy update cannot attach itself to the session being
        // cancelled. Cancellation callbacks only abort a socket or close a
        // named pipe; they never wait on the plugin lifecycle lock.
        DirectHistorySession previous;
        lock (directSessionGate)
        {
            previous = directSession;
            directSession = previous.CreateSuccessor();
        }
        previous.Cancellation.Cancel();
    }

    public void BeginDirectTransport(CancellationToken directSessionToken)
    {
        lock (directSessionGate)
        {
            var session = RequireCurrentDirectSession(directSessionToken);
            session.RewindForPhysicalTransport();
        }
    }

    public void SuspendEventCapture()
    {
        lock (historyGenerationGate)
        {
            historyGeneration++;
            historyWritesSuspended = true;
        }
    }

    public void ResumeEventCapture()
    {
        lock (historyGenerationGate)
        {
            // Work that began during the suspended policy-publication window
            // must remain stale even if it completes after capture resumes.
            historyGeneration++;
            historyWritesSuspended = false;
        }
    }

    public void EventDeliveryPolicyChanging(
        DirectEventDeliveryOptions previous,
        DirectEventDeliveryOptions current)
    {
        lock (historyGenerationGate)
        {
            if (previous.Images != current.Images)
            {
                imageCaptureGeneration++;
            }
            if (previous.Guiding != current.Guiding)
            {
                guideCaptureGeneration++;
            }
        }
        if (previous.Autofocus != current.Autofocus)
        {
            // Completion provenance belongs to the consent state observed at
            // capture. Toggling autofocus sharing revokes every pending/cached
            // report so an old-enabled run cannot surface after re-enabling.
            InvalidateAutofocusCapture();
        }
    }

    public void EventDeliveryPolicyChanged(
        DirectEventDeliveryOptions previous,
        DirectEventDeliveryOptions current)
    {
    }

    public void RevokeProfileAccess()
    {
        // Stop background producers before advancing/clearing history. The
        // log tail can parse a predecessor line before Reset and invoke its
        // callback afterwards; Stop is a completion barrier for that task.
        // Notification hooks are synchronous but are detached for the same
        // profile boundary.
        logWatcher.Stop();
        notificationWatcher.Stop();
        // Transport invalidation is independent from the stronger profile
        // trust revocation below. Keeping it separate lets event-policy
        // changes reconnect without cancelling accepted hardware operations
        // or an exact-run autofocus report capture.
        RotateDirectSession();
        var previous = Interlocked.Exchange(
            ref profileSession,
            new CancellationTokenSource());
        lock (commandGenerationGate)
        {
            Interlocked.Increment(ref commandGeneration);
        }
        InvalidateAutofocusCapture();
        SuspendEventCapture();
        previous.Cancel();
        CancelOutstandingCommands();
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

        // Make every callback already in flight stale before detaching the
        // mediators. A focuser callback can otherwise create new pending work
        // in the interval between cancellation and RemoveConsumer().
        started = false;
        InvalidateAutofocusCapture();
        var captureStop = eventCaptureStop;
        eventCaptureStop = null;
        captureStop?.Cancel();

        telescope.Connected -= TelescopeConnected;
        telescope.Disconnected -= TelescopeDisconnected;
        telescope.BeforeMeridianFlip -= TelescopeBeforeMeridianFlip;
        telescope.AfterMeridianFlip -= TelescopeAfterMeridianFlip;
        telescope.Homed -= TelescopeHomed;
        telescope.Parked -= TelescopeParked;
        telescope.Unparked -= TelescopeUnparked;

        camera.Connected -= CameraConnected;
        camera.Disconnected -= CameraDisconnected;
        camera.DownloadTimeout -= CameraDownloadTimeout;

        filterWheel.Connected -= FilterWheelConnected;
        filterWheel.Disconnected -= FilterWheelDisconnected;
        filterWheel.FilterChanged -= FilterWheelChanged;

        guider.Connected -= GuiderConnected;
        guider.Disconnected -= GuiderDisconnected;
        guider.AfterDither -= GuiderDithered;
        guider.GuidingStarted -= GuiderStarted;
        guider.GuidingStopped -= GuiderStopped;
        guider.GuideEvent -= GuiderGuideEvent;

        rotator.Connected -= RotatorConnected;
        rotator.Disconnected -= RotatorDisconnected;
        rotator.Moved -= RotatorMoved;
        rotator.MovedMechanical -= RotatorMovedMechanical;
        rotator.Synced -= RotatorSynced;

        focuser.Connected -= FocuserConnected;
        focuser.Disconnected -= FocuserDisconnected;
        focuser.RemoveConsumer(this);

        safetyMonitor.Connected -= SafetyConnected;
        safetyMonitor.Disconnected -= SafetyDisconnected;
        safetyMonitor.IsSafeChanged -= SafetyChanged;
        safetyMonitor.RemoveConsumer(this);
        lock (safetyStateGate)
        {
            safetyState = DirectSafetyState.Unknown;
        }

        foreach (var topic in TargetSchedulerTopics)
        {
            messageBroker.Unsubscribe(topic, this);
        }
        logWatcher.Stop();
        notificationWatcher.Stop();

        var subscriptionStop = sequenceSubscriptionStop;
        sequenceSubscriptionStop = null;
        subscriptionStop?.Cancel();
        lock (sequenceGate)
        {
            if (sequenceSubscribed)
            {
                sequence.SequenceStarting -= SequenceStarting;
                sequence.SequenceFinished -= SequenceFinished;
                sequenceSubscribed = false;
            }
        }
        subscriptionStop?.Dispose();
        captureStop?.Dispose();
        imageSave.ImageSaved -= ImageSaved;
        CancelOutstandingCommands();
    }

    public void Reset()
    {
        InvalidateAutofocusCapture();
        ResetHistoryBuffers();
        lock (safetyStateGate)
        {
            safetyState = DirectSafetyState.Unknown;
        }
        Interlocked.Exchange(ref guideStepId, 0);
        ApplyLogDeliveryOptions();
        _ = notificationWatcher.Start(CaptureHistoryGeneration());
    }

    private long CaptureHistoryGeneration() => Volatile.Read(ref historyGeneration);

    private bool IsHistoryGenerationCurrent(long generation)
    {
        lock (historyGenerationGate)
        {
            return !historyWritesSuspended && generation == historyGeneration;
        }
    }

    private bool AddHistoryIfCurrent<T>(
        BoundedHistory<T> history,
        T item,
        long generation)
    {
        lock (historyGenerationGate)
        {
            if (historyWritesSuspended || generation != historyGeneration)
            {
                return false;
            }
            history.Add(item);
            return true;
        }
    }

    private bool AddImageHistoryIfCurrent(
        DirectSavedImage image,
        long historyGeneration,
        long captureGeneration)
    {
        lock (historyGenerationGate)
        {
            if (historyWritesSuspended
                || historyGeneration != this.historyGeneration
                || captureGeneration != imageCaptureGeneration)
            {
                return false;
            }
            images.Add(image);
            return true;
        }
    }

    private bool AddGuideHistoryIfCurrent(
        DirectGuideStep step,
        long historyGeneration,
        long captureGeneration)
    {
        lock (historyGenerationGate)
        {
            if (historyWritesSuspended
                || historyGeneration != this.historyGeneration
                || captureGeneration != guideCaptureGeneration)
            {
                return false;
            }
            guideSteps.Add(step);
            return true;
        }
    }

    private void ResetHistoryBuffers()
    {
        lock (historyGenerationGate)
        {
            historyGeneration++;
            events.Clear();
            logEvents.Clear();
            images.Clear();
            guideSteps.Clear();
            historyWritesSuspended = false;
        }
    }

    public async Task<object?> ExecuteAsync(
        DirectQuery query,
        CancellationToken cancellationToken,
        CancellationToken? directSessionToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directSessionToken?.ThrowIfCancellationRequested();
        object? result = query.Kind switch
        {
            DirectQueryKind.EventHistory =>
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(
                    SnapshotEventHistoryForQuery(query, directSessionToken)),
            DirectQueryKind.ImageHistory =>
                DirectApiEnvelope<IReadOnlyList<DirectImageMetadata>>.Ok(
                    SnapshotImagesForQuery(query, directSessionToken)),
            DirectQueryKind.Sequence =>
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(
                    RunOnUiThread(() => NinaDirectSequenceSnapshot.Build(
                        sequence,
                        eventDelivery.Current,
                        accessPolicy.Current,
                        GetSafetyMonitorIsSafe()))),
            DirectQueryKind.Thumbnail => GetThumbnail(query.Index),
            DirectQueryKind.LastAutofocus =>
                DirectApiEnvelope<JsonElement>.Ok(
                    await GetLastAutofocusAsync(cancellationToken).ConfigureAwait(false)),
            DirectQueryKind.MountInfo =>
                DirectApiEnvelope<IReadOnlyDictionary<string, object?>>.Ok(GetMountInfo()),
            DirectQueryKind.CameraInfo =>
                DirectApiEnvelope<DirectCameraInfo>.Ok(GetCameraInfo()),
            DirectQueryKind.FilterwheelInfo =>
                DirectApiEnvelope<DirectFilterWheelInfo>.Ok(GetFilterWheelInfo()),
            DirectQueryKind.GuiderInfo =>
                DirectApiEnvelope<DirectGuiderInfo>.Ok(GetGuiderInfo()),
            DirectQueryKind.GuiderGraph =>
                DirectApiEnvelope<DirectGuiderGraph>.Ok(GetGuiderGraph()),
            DirectQueryKind.RotatorInfo =>
                DirectApiEnvelope<DirectRotatorInfo>.Ok(GetRotatorInfo()),
            DirectQueryKind.FocuserInfo =>
                DirectApiEnvelope<DirectFocuserInfo>.Ok(GetFocuserInfo()),
            DirectQueryKind.Command =>
                await ExecuteCommandAsync(query, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Direct query '{query.Kind}' is not implemented by this plugin version."),
        };
        return result;
    }

    public void ConfirmDirectQueryResponse(
        DirectQuery query,
        CancellationToken directSessionToken)
    {
        if (query.Kind is not DirectQueryKind.EventHistory
            and not DirectQueryKind.ImageHistory)
        {
            return;
        }

        lock (directSessionGate)
        {
            var session = directSession;
            if (session.Cancellation.Token != directSessionToken)
            {
                return;
            }

            if (query.Kind == DirectQueryKind.EventHistory
                && session.PendingEventQuery is { } events
                && events.QueryId == query.Id)
            {
                var hadQueriedEventHistory = session.EventHistoryQueried;
                session.PendingEventQuery = null;
                session.EventHistoryQueried = true;
                if (events.WasReplayBaseline)
                {
                    session.EventReplayPending = false;
                }
                else
                {
                    if (!hadQueriedEventHistory)
                    {
                        session.PriorEquipmentEvent = events.EquipmentTail;
                        session.PriorLogEvent = events.LogTail;
                    }
                    else
                    {
                        session.PriorEquipmentEvent = session.LastEquipmentEvent;
                        session.PriorLogEvent = session.LastLogEvent;
                    }
                    session.LastEquipmentEvent = events.EquipmentTail;
                    session.LastLogEvent = events.LogTail;
                    if (events.NewAutofocusEvents.Count != 0)
                    {
                        // Direct v1 has no acknowledgement for "the chart was
                        // rendered and the chat message was accepted". Keep
                        // the latest completion at-least-once across every
                        // forced transport rotation instead of treating a
                        // LastAutofocus read (which slash commands can also
                        // issue) as delivery completion.
                        session.PendingAutofocusEvents.Clear();
                        session.PendingAutofocusEvents.UnionWith(
                            events.NewAutofocusEvents);
                    }
                }
            }
            else if (query.Kind == DirectQueryKind.ImageHistory
                && session.PendingImageQuery is { } images
                && images.QueryId == query.Id)
            {
                var hadQueriedImageHistory = session.ImageHistoryQueried;
                session.PendingImageQuery = null;
                session.ImageHistoryQueried = true;
                if (images.WasReplayBaseline)
                {
                    session.ImageReplayPending = false;
                }
                else
                {
                    session.PriorImage = hadQueriedImageHistory
                        ? session.LastImage
                        : images.ImageTail;
                    session.LastImage = images.ImageTail;
                }
            }
        }
    }

    private DirectEventHistoryBarrier? GetEventReplayBarrier(
        CancellationToken? directSessionToken)
    {
        if (!directSessionToken.HasValue)
        {
            return null;
        }

        lock (directSessionGate)
        {
            var session = RequireCurrentDirectSession(directSessionToken.Value);
            return session.EventReplayPending
                ? new DirectEventHistoryBarrier(
                    session.LastEquipmentEvent,
                    session.LastLogEvent,
                    session.PendingAutofocusEvents.ToHashSet())
                : null;
        }
    }

    private long? GetImageReplayBarrier(CancellationToken? directSessionToken)
    {
        if (!directSessionToken.HasValue)
        {
            return null;
        }

        lock (directSessionGate)
        {
            var session = RequireCurrentDirectSession(directSessionToken.Value);
            return session.ImageReplayPending ? session.LastImage : null;
        }
    }

    private void RegisterEventHistoryQuery(
        Guid queryId,
        CancellationToken? directSessionToken,
        bool wasReplayBaseline,
        IReadOnlyList<BoundedHistoryEntry<Dictionary<string, object?>>> equipmentSnapshot,
        long equipmentTail,
        long logTail)
    {
        if (!directSessionToken.HasValue)
        {
            return;
        }

        lock (directSessionGate)
        {
            var session = RequireCurrentDirectSession(directSessionToken.Value);
            // Recheck the phase after taking the history snapshots. A second
            // query is not expected on the serial transports, but treating a
            // phase change as stale prevents an accidental concurrent caller
            // from advancing a replacement cursor with the wrong response.
            if (session.EventReplayPending != wasReplayBaseline)
            {
                throw new OperationCanceledException(directSessionToken.Value);
            }
            var newlyObservedAutofocus = !wasReplayBaseline
                && session.EventHistoryQueried
                    ? equipmentSnapshot
                        .Where(entry => entry.Sequence > session.LastEquipmentEvent
                            && entry.Item.TryGetValue("Event", out var eventName)
                            && eventName is "AUTOFOCUS-FINISHED"
                            && entry.Item.TryGetValue("ChatEnabled", out var chatEnabled)
                            && chatEnabled is true
                            && eventDelivery.Current.Autofocus)
                        .Select(entry => entry.Sequence)
                        .TakeLast(1)
                        .ToArray()
                    : Array.Empty<long>();
            session.PendingEventQuery = new DirectEventHistoryObservation(
                queryId,
                wasReplayBaseline,
                equipmentTail,
                logTail,
                newlyObservedAutofocus);
        }
    }

    private void RegisterImageHistoryQuery(
        Guid queryId,
        CancellationToken? directSessionToken,
        bool wasReplayBaseline,
        long imageTail)
    {
        if (!directSessionToken.HasValue)
        {
            return;
        }

        lock (directSessionGate)
        {
            var session = RequireCurrentDirectSession(directSessionToken.Value);
            if (session.ImageReplayPending != wasReplayBaseline)
            {
                throw new OperationCanceledException(directSessionToken.Value);
            }
            session.PendingImageQuery = new DirectImageHistoryObservation(
                queryId,
                wasReplayBaseline,
                imageTail);
        }
    }

    private DirectHistorySession RequireCurrentDirectSession(
        CancellationToken directSessionToken)
    {
        var session = directSession;
        if (session.Cancellation.Token != directSessionToken)
        {
            throw new OperationCanceledException(directSessionToken);
        }
        return session;
    }

    public void Dispose() => Stop();

    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
    }

    public void UpdateDeviceInfo(SafetyMonitorInfo deviceInfo) =>
        RecordSafetyState(
            CaptureHistoryGeneration(),
            deviceInfo.Connected,
            deviceInfo.IsSafe);

    public void UpdateEndAutoFocusRun(AutoFocusInfo info)
    {
        var historyGeneration = CaptureHistoryGeneration();
        if (!started)
        {
            return;
        }

        // Capture the session token before the generation. If a profile
        // switch races this callback, either the old token is cancelled or
        // the new generation makes this completion stale.
        var profileSessionToken = ProfileSessionToken;
        var generation = Volatile.Read(ref autofocusCaptureGeneration);
        var chatEnabled = eventDelivery.Current.Autofocus;
        var completion = new DirectAutofocusCompletion(
            info.Filter ?? string.Empty,
            info.Position,
            info.Temperature,
            info.Timestamp,
            TryGetActiveProfileId(),
            chatEnabled);
        if (!IsHistoryGenerationCurrent(historyGeneration))
        {
            return;
        }
        lock (autofocusReportGate)
        {
            if (!started || generation != Volatile.Read(ref autofocusCaptureGeneration))
            {
                return;
            }
            if (!IsHistoryGenerationCurrent(historyGeneration))
            {
                return;
            }

            // Never answer a completion-triggered query with the preceding
            // run. The report file is written by N.I.N.A. just before this
            // callback, but third-party autofocus engines can expose it a
            // little later.
            lastAutofocusReport = null;
            pendingAutofocusCompletion = completion;
            pendingAutofocusGeneration = generation;
            pendingAutofocusHistoryGeneration = historyGeneration;
        }

        var captureStop = eventCaptureStop;
        if (!started
            || generation != Volatile.Read(ref autofocusCaptureGeneration)
            || captureStop is null)
        {
            return;
        }
        if (!chatEnabled)
        {
            AddAutofocusFinishedEvent(completion, generation);
            return;
        }

        _ = CaptureCompletedAutofocusAsync(
            completion,
            generation,
            captureStop.Token,
            profileSessionToken);
    }

    public void UpdateUserFocused(FocuserInfo info) =>
        AddEvent(CaptureHistoryGeneration(), "FOCUSER-USER-FOCUSED");

    public void AutoFocusRunStarting() =>
        AddEvent(CaptureHistoryGeneration(), "AUTOFOCUS-STARTING");

    public void NewAutoFocusPoint(DataPoint dataPoint)
    {
        var historyGeneration = CaptureHistoryGeneration();
        AddEvent(
            historyGeneration,
            "AUTOFOCUS-POINT-ADDED",
            ("Position", FiniteInt(dataPoint.X)),
            ("HFR", FiniteOrZero(dataPoint.Y)));
    }

    private async Task<JsonElement> GetLastAutofocusAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref autofocusCaptureGeneration);
        RequireAutofocusSharing(generation);

        DirectAutofocusCompletion? pending;
        long pendingGeneration;
        JsonElement? cached;
        lock (autofocusReportGate)
        {
            cached = lastAutofocusReport?.Clone();
            pending = pendingAutofocusCompletion;
            pendingGeneration = pendingAutofocusGeneration;
        }

        if (cached is not null)
        {
            var redacted = DirectPrivacyProjection.Redact(cached.Value, accessPolicy.Current);
            RequireAutofocusSharing(generation);
            return redacted;
        }

        if (pending is not null)
        {
            if (!pending.ChatEnabled)
            {
                throw new InvalidOperationException(
                    "The latest autofocus result was captured while sharing was disabled.");
            }
            if (pendingGeneration != Volatile.Read(ref autofocusCaptureGeneration))
            {
                throw new InvalidOperationException(
                    "No completed autofocus report is available for this profile session.");
            }

            var matched = await ReadCompletedAutofocusReportAsync(
                    autofocusReportDirectory,
                    pending,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CacheAutofocusReport(matched, pending, pendingGeneration))
            {
                throw new InvalidOperationException(
                    "The autofocus profile session changed while reading the report.");
            }
            var redacted = DirectPrivacyProjection.Redact(matched, accessPolicy.Current);
            RequireAutofocusSharing(generation);
            return redacted;
        }

        throw new InvalidOperationException(
            "No completed autofocus report is available for this profile session.");
    }

    private void RequireAutofocusSharing(long generation)
    {
        if (!eventDelivery.Current.Autofocus)
        {
            throw new InvalidOperationException("Autofocus result sharing is disabled.");
        }
        if (generation != Volatile.Read(ref autofocusCaptureGeneration))
        {
            throw new InvalidOperationException(
                "No completed autofocus report is available for this profile session.");
        }
    }

    private async Task CaptureCompletedAutofocusAsync(
        DirectAutofocusCompletion completion,
        long generation,
        CancellationToken providerCancellationToken,
        CancellationToken profileCancellationToken)
    {
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(
            providerCancellationToken,
            profileCancellationToken);
        var cancellationToken = captureStop.Token;
        try
        {
            var report = await ReadCompletedAutofocusReportAsync(
                    autofocusReportDirectory,
                    completion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CacheAutofocusReport(report, completion, generation))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Keep the completion event: the Hub retries the detail query and
            // may find a report exposed later by a third-party autofocus
            // engine. Do not include the local report path in N.I.N.A.'s log.
            Logger.Warning("Chatstronomy could not cache the completed autofocus report yet.");
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            AddAutofocusFinishedEvent(completion, generation);
        }
    }

    private bool CacheAutofocusReport(
        JsonElement report,
        DirectAutofocusCompletion completion,
        long generation)
    {
        lock (autofocusReportGate)
        {
            if (generation == Volatile.Read(ref autofocusCaptureGeneration)
                && pendingAutofocusGeneration == generation
                && pendingAutofocusCompletion == completion
                && completion.ChatEnabled)
            {
                lastAutofocusReport = report.Clone();
                return true;
            }
        }
        return false;
    }

    internal bool TryCacheObservedAutofocusReport(
        JsonElement report,
        long generation,
        bool chatEnabledAtCompletion)
    {
        lock (autofocusReportGate)
        {
            if (!chatEnabledAtCompletion
                || generation != Volatile.Read(ref autofocusCaptureGeneration))
            {
                return false;
            }

            if (pendingAutofocusCompletion is not null
                && (pendingAutofocusGeneration != generation
                    || !pendingAutofocusCompletion.ChatEnabled
                    || !MatchesAutofocusCompletion(report, pendingAutofocusCompletion)))
            {
                return false;
            }

            // Keep the completion provenance. In particular, a completion
            // captured with sharing off must remain unavailable after the
            // switch is turned on again.
            lastAutofocusReport = report.Clone();
            pendingAutofocusGeneration = generation;
            return true;
        }
    }

    private void InvalidateAutofocusCapture()
    {
        var generation = Interlocked.Increment(ref autofocusCaptureGeneration);
        lock (autofocusReportGate)
        {
            lastAutofocusReport = null;
            pendingAutofocusCompletion = null;
            pendingAutofocusGeneration = generation;
            pendingAutofocusHistoryGeneration = CaptureHistoryGeneration();
        }
    }

    private void AddAutofocusFinishedEvent(
        DirectAutofocusCompletion completion,
        long generation)
    {
        lock (autofocusReportGate)
        {
            if (!started
                || generation != Volatile.Read(ref autofocusCaptureGeneration)
                || pendingAutofocusGeneration != generation
                || pendingAutofocusCompletion != completion)
            {
                return;
            }

            AddEventCore(
                pendingAutofocusHistoryGeneration,
                DateTime.Now,
                "AUTOFOCUS-FINISHED",
                completion.ChatEnabled,
                ("Filter", completion.Filter),
                ("Position", FiniteOrZero(completion.Position)),
                ("Temperature", double.IsFinite(completion.Temperature)
                    ? completion.Temperature
                    : null),
                ("ReportTimestamp", completion.Timestamp));
        }
    }

    private Guid? TryGetActiveProfileId()
    {
        try
        {
            return profileService.ActiveProfile.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static async Task<JsonElement> ReadCompletedAutofocusReportAsync(
        string directory,
        DirectAutofocusCompletion completion,
        CancellationToken cancellationToken)
    {
        if (completion.ProfileId is not Guid profileId)
        {
            throw new InvalidOperationException(
                "The active N.I.N.A. profile could not be identified for the autofocus report.");
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (var candidate in EnumerateAutofocusReportCandidates(
                        directory,
                        completion.Timestamp,
                        profileId,
                        cancellationToken))
                    {
                        try
                        {
                            var report = await ReadAutofocusReportAsync(candidate, cancellationToken)
                                .ConfigureAwait(false);
                            if (MatchesAutofocusCompletion(report, completion))
                            {
                                return report;
                            }
                        }
                        catch (Exception exception) when (
                            exception is IOException
                                or UnauthorizedAccessException
                                or JsonException)
                        {
                            lastError = exception;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
            }

            if (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "The autofocus report matching the completed run is not available yet.",
            lastError);
    }

    private static IEnumerable<string> EnumerateAutofocusReportCandidates(
        string directory,
        DateTime completionTimestamp,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var profileSuffix = $"--{profileId:D}.json";

        // N.I.N.A. names reports with its local completion timestamp. Probe
        // the same bounded tolerance used for payload correlation first, so
        // large historical report folders do not need to be scanned.
        for (var offset = -5; offset <= 5; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"{completionTimestamp.AddSeconds(offset):yyyy-MM-dd--HH-mm-ss}{profileSuffix}";
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static async Task<JsonElement> ReadAutofocusReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static bool MatchesAutofocusCompletion(
        JsonElement report,
        DirectAutofocusCompletion completion)
    {
        if (!report.TryGetProperty("Timestamp", out var timestampValue)
            || !timestampValue.TryGetDateTime(out var timestamp)
            || (timestamp - completion.Timestamp).Duration() > TimeSpan.FromSeconds(5))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(completion.Filter)
            && report.TryGetProperty("Filter", out var filterValue)
            && filterValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(filterValue.GetString())
            && !completion.Filter.Equals(filterValue.GetString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (double.IsFinite(completion.Position)
            && report.TryGetProperty("CalculatedFocusPoint", out var calculated)
            && calculated.TryGetProperty("Position", out var positionValue)
            && positionValue.TryGetDouble(out var position)
            && double.IsFinite(position)
            && Math.Abs(position - completion.Position) > 1)
        {
            return false;
        }

        return true;
    }

    private Task<object?> ExecuteCommandAsync(
        DirectQuery query,
        CancellationToken cancellationToken)
    {
        var command = query.Command ?? throw new InvalidOperationException(
            "The Direct command payload is missing.");
        var generation = Volatile.Read(ref commandGeneration);
        // The negotiated capability is informational, not a trust boundary:
        // an old connection or compromised peer can still send a command.
        // Fail before inspecting any mediator or touching observatory gear.
        RequireCurrentCommandConsent(query, cancellationToken, generation);
        Action authorize = () => RequireCurrentCommandConsent(
            query,
            cancellationToken,
            generation);
        var response = command.Kind switch
        {
            DirectRigCommandKind.UnparkMount => UnparkMount(authorize, generation),
            DirectRigCommandKind.HomeMount => HomeMount(authorize, generation),
            DirectRigCommandKind.ChangeFilter => ChangeFilter(command.FilterId, authorize, generation),
            DirectRigCommandKind.StartGuiding => StartGuiding(command.Calibrate, authorize, generation),
            DirectRigCommandKind.StopGuiding => StopGuiding(authorize, generation),
            DirectRigCommandKind.CoolCamera =>
                CoolCamera(command.Temperature, command.Minutes, authorize, generation),
            DirectRigCommandKind.WarmCamera => WarmCamera(command.Minutes, authorize, generation),
            DirectRigCommandKind.StartAutofocus =>
                StartAutofocus(query, cancellationToken, generation, authorize),
            DirectRigCommandKind.CancelAutofocus => CancelAutofocus(authorize),
            DirectRigCommandKind.ParkMount => ParkMount(authorize, generation),
            DirectRigCommandKind.AbortExposure => AbortExposure(authorize),
            DirectRigCommandKind.StopSequence =>
                StopSequence(query, cancellationToken, generation),
            DirectRigCommandKind.StartSequence =>
                StartSequence(command.SkipValidation, query, cancellationToken, generation),
            _ => throw new NotSupportedException(
                $"Direct command '{command.Kind}' is not implemented."),
        };
        return Task.FromResult<object?>(response);
    }

    private void RequireCurrentCommandConsent(
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref commandGeneration) != generation)
        {
            throw new InvalidOperationException(
                "Remote hardware command was revoked before execution.");
        }
        if (query.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            throw new InvalidOperationException("query expired before execution");
        }

        accessPolicy.RequireRemoteControl(query.Command ?? throw new InvalidOperationException(
            "The Direct command payload is missing."));
    }

    /// Capture immutable command context before joining the WPF dispatcher
    /// queue, then recheck its live deadline and profile consent when the
    /// callback finally begins, immediately before touching N.I.N.A. hardware.
    internal Func<T> GuardCommandAction<T>(
        DirectQuery query,
        CancellationToken cancellationToken,
        Func<T> action) => GuardCommandAction(
            query,
            cancellationToken,
            Volatile.Read(ref commandGeneration),
            action);

    private Func<T> GuardCommandAction<T>(
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation,
        Func<T> action) => () =>
    {
        RequireCurrentCommandConsent(query, cancellationToken, generation);
        return action();
    };

    private T RunAuthorizedCommandOnUiThread<T>(
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation,
        Func<T> action) => RunOnUiThread(GuardCommandAction(
            query,
            cancellationToken,
            generation,
            action));

    private DirectApiEnvelope<string> UnparkMount(Action authorize, long generation)
    {
        var info = telescope.GetInfo();
        if (!info.Connected)
        {
            throw new InvalidOperationException("Mount is not connected.");
        }
        if (!info.AtPark)
        {
            return DirectApiEnvelope<string>.Ok("Mount is not parked");
        }
        authorize();
        ObserveCommand(
            telescope.UnparkTelescope(CreateProgress(), CancellationToken.None),
            "Unpark mount",
            generation);
        return DirectApiEnvelope<string>.Accepted("Mount unparking requested");
    }

    private DirectApiEnvelope<string> HomeMount(Action authorize, long generation)
    {
        var info = telescope.GetInfo();
        if (!info.Connected)
        {
            throw new InvalidOperationException("Mount is not connected.");
        }
        if (info.AtPark)
        {
            throw new InvalidOperationException("Mount is parked.");
        }
        if (info.AtHome)
        {
            return DirectApiEnvelope<string>.Ok("Mount is already homed");
        }
        if (!info.CanFindHome)
        {
            throw new InvalidOperationException("The mount does not support homing.");
        }
        if (info.Slewing)
        {
            authorize();
            telescope.StopSlew();
        }
        authorize();
        ObserveCommand(
            telescope.FindHome(CreateProgress(), CancellationToken.None),
            "Home mount",
            generation);
        return DirectApiEnvelope<string>.Accepted("Mount homing requested");
    }

    private DirectApiEnvelope<string> ParkMount(Action authorize, long generation)
    {
        var info = telescope.GetInfo();
        if (!info.Connected)
        {
            throw new InvalidOperationException("Mount is not connected.");
        }
        if (info.AtPark)
        {
            return DirectApiEnvelope<string>.Ok("Mount is already parked");
        }
        if (!info.CanPark)
        {
            throw new InvalidOperationException("The mount does not support parking.");
        }
        if (info.Slewing)
        {
            authorize();
            telescope.StopSlew();
        }
        authorize();
        ObserveCommand(
            telescope.ParkTelescope(CreateProgress(), CancellationToken.None),
            "Park mount",
            generation);
        return DirectApiEnvelope<string>.Accepted("Mount parking requested");
    }

    private DirectApiEnvelope<string> ChangeFilter(
        int? filterId,
        Action authorize,
        long generation)
    {
        if (!filterWheel.GetInfo().Connected)
        {
            throw new InvalidOperationException("Filter wheel is not connected.");
        }
        if (!filterId.HasValue)
        {
            throw new InvalidOperationException("A filter ID is required.");
        }

        var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
        var selected = filters.FirstOrDefault(filter => filter.Position == filterId.Value);
        if (selected is null && filterId.Value >= 0 && filterId.Value < filters.Count)
        {
            selected = filters[filterId.Value];
        }
        if (selected is null)
        {
            throw new InvalidOperationException($"Filter ID {filterId.Value} does not exist.");
        }

        authorize();
        ObserveCommand(
            filterWheel.ChangeFilter(selected, CancellationToken.None, CreateProgress()),
            $"Change filter to {selected.Name}",
            generation);
        return DirectApiEnvelope<string>.Accepted($"Filter change to {selected.Name} requested");
    }

    private DirectApiEnvelope<string> StartGuiding(
        bool? calibrate,
        Action authorize,
        long generation)
    {
        if (!guider.GetInfo().Connected)
        {
            throw new InvalidOperationException("Guider is not connected.");
        }
        authorize();
        var stop = ReplaceCommandToken(ref guideCommandStop);
        authorize();
        ObserveCommand(
            guider.StartGuiding(calibrate ?? false, CreateProgress(), stop.Token),
            "Start guiding",
            generation);
        return DirectApiEnvelope<string>.Accepted("Guiding start requested");
    }

    private DirectApiEnvelope<string> StopGuiding(Action authorize, long generation)
    {
        if (!guider.GetInfo().Connected)
        {
            throw new InvalidOperationException("Guider is not connected.");
        }
        authorize();
        CancelCommand(ref guideCommandStop);
        authorize();
        ObserveCommand(
            guider.StopGuiding(CancellationToken.None),
            "Stop guiding",
            generation);
        return DirectApiEnvelope<string>.Accepted("Guiding stop requested");
    }

    private DirectApiEnvelope<string> CoolCamera(
        double? temperature,
        double? minutes,
        Action authorize,
        long generation)
    {
        if (!camera.GetInfo().Connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
        if (!camera.GetInfo().CanSetTemperature)
        {
            throw new InvalidOperationException("Camera has no temperature control.");
        }
        var target = RequiredFinite(temperature, "Camera temperature");
        var duration = ResolveDuration(
            minutes,
            profileService.ActiveProfile.CameraSettings.CoolingDuration,
            "Cooling duration");
        authorize();
        var stop = ReplaceCommandToken(ref cameraCommandStop);
        authorize();
        ObserveCommand(
            camera.CoolCamera(target, duration, CreateProgress(), stop.Token),
            "Cool camera",
            generation);
        return DirectApiEnvelope<string>.Accepted(
            $"Camera cooling to {target:0.##} C over {duration.TotalMinutes:0.##} minutes requested");
    }

    private DirectApiEnvelope<string> WarmCamera(
        double? minutes,
        Action authorize,
        long generation)
    {
        if (!camera.GetInfo().Connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
        if (!camera.GetInfo().CanSetTemperature)
        {
            throw new InvalidOperationException("Camera has no temperature control.");
        }
        var duration = ResolveDuration(
            minutes,
            profileService.ActiveProfile.CameraSettings.WarmingDuration,
            "Warming duration");
        authorize();
        var stop = ReplaceCommandToken(ref cameraCommandStop);
        authorize();
        ObserveCommand(
            camera.WarmCamera(duration, CreateProgress(), stop.Token),
            "Warm camera",
            generation);
        return DirectApiEnvelope<string>.Accepted(
            $"Camera warming over {duration.TotalMinutes:0.##} minutes requested");
    }

    private DirectApiEnvelope<string> StartAutofocus(
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation,
        Action authorize)
    {
        if (!focuser.GetInfo().Connected)
        {
            throw new InvalidOperationException("Focuser is not connected.");
        }

        authorize();
        var stop = ReplaceCommandToken(ref autofocusCommandStop);
        IWindowService? window = null;
        Task<AutoFocusReport>? autofocus = null;
        long autofocusGeneration = -1;
        RunAuthorizedCommandOnUiThread(query, cancellationToken, generation, () =>
        {
            window = windowFactory.Create();
            var viewModel = autoFocusFactory.Create();
            window.Show(
                viewModel,
                "Autofocus",
                ResizeMode.CanResize,
                WindowStyle.ToolWindow);
            var selectedFilter = filterWheel.GetInfo().SelectedFilter;
            autofocusGeneration = Volatile.Read(ref autofocusCaptureGeneration);
            authorize();
            autofocus = viewModel.StartAutoFocus(
                selectedFilter,
                stop.Token,
                CreateProgress());
            return true;
        });

        ObserveAutofocus(
            autofocus ?? throw new InvalidOperationException("Autofocus did not start."),
            window ?? throw new InvalidOperationException("Autofocus window did not open."),
            autofocusGeneration,
            generation);
        return DirectApiEnvelope<string>.Accepted("Autofocus requested");
    }

    private DirectApiEnvelope<string> CancelAutofocus(Action authorize)
    {
        authorize();
        CancelCommand(ref autofocusCommandStop);
        return DirectApiEnvelope<string>.Accepted("Autofocus cancellation requested");
    }

    private DirectApiEnvelope<string> AbortExposure(Action authorize)
    {
        if (!camera.GetInfo().Connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
        if (!camera.GetInfo().IsExposing)
        {
            return DirectApiEnvelope<string>.Ok("Camera is not exposing");
        }
        authorize();
        camera.AbortExposure();
        return DirectApiEnvelope<string>.Accepted("Exposure abort requested");
    }

    private DirectApiEnvelope<string> StopSequence(
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation)
    {
        EnsureSequenceReady();
        RunAuthorizedCommandOnUiThread(query, cancellationToken, generation, () =>
        {
            sequence.CancelAdvancedSequence();
            return true;
        });
        return DirectApiEnvelope<string>.Accepted("Sequence stop requested");
    }

    private DirectApiEnvelope<string> StartSequence(
        bool? skipValidation,
        DirectQuery query,
        CancellationToken cancellationToken,
        long generation)
    {
        EnsureSequenceReady();
        if (sequence.IsAdvancedSequenceRunning())
        {
            throw new InvalidOperationException("Sequence is already running.");
        }
        var task = RunAuthorizedCommandOnUiThread(
            query,
            cancellationToken,
            generation,
            () => sequence.StartAdvancedSequence(skipValidation ?? false));
        ObserveCommand(task, "Start sequence", generation);
        return DirectApiEnvelope<string>.Accepted("Sequence start requested");
    }

    private void EnsureSequenceReady()
    {
        if (!sequence.Initialized)
        {
            throw new InvalidOperationException("Sequence is not initialized.");
        }
    }

    private void ObserveAutofocus(
        Task<AutoFocusReport> task,
        IWindowService window,
        long generation,
        long acceptedCommandGeneration)
    {
        if (task.IsFaulted || task.IsCanceled)
        {
            _ = window.Close();
            task.GetAwaiter().GetResult();
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    var report = completed.Result;
                    if (report is null)
                    {
                        AddCommandFailureIfCurrent(
                            "Autofocus",
                            "No autofocus report was returned",
                            acceptedCommandGeneration);
                        _ = window.Close();
                        return;
                    }

                    if (generation != Volatile.Read(ref autofocusCaptureGeneration))
                    {
                        _ = window.Close();
                        return;
                    }

                    var chatEnabledAtCompletion = eventDelivery.Current.Autofocus;
                    var serialized = JsonSerializer.SerializeToElement(
                        report,
                        DirectProtocol.JsonOptions);
                    TryCacheObservedAutofocusReport(
                        serialized,
                        generation,
                        chatEnabledAtCompletion);
                    imageHistory.AppendAutoFocusPoint(report);
                    window.DelayedClose(TimeSpan.FromSeconds(10));
                    return;
                }

                if (completed.IsFaulted)
                {
                    AddCommandFailureIfCurrent(
                        "Autofocus",
                        completed.Exception?.GetBaseException().Message ?? "Unknown error",
                        acceptedCommandGeneration);
                }
                else if (completed.IsCanceled)
                {
                    AddCommandFailureIfCurrent(
                        "Autofocus",
                        "Command canceled before completion",
                        acceptedCommandGeneration);
                }
                _ = window.Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task ObserveCommand(
        Task task,
        string commandName,
        long acceptedCommandGeneration)
    {
        if (task.IsFaulted || task.IsCanceled)
        {
            task.GetAwaiter().GetResult();
        }

        return task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    AddCommandFailureIfCurrent(
                        commandName,
                        completed.Exception?.GetBaseException().Message ?? "Unknown error",
                        acceptedCommandGeneration);
                }
                else if (completed.IsCanceled)
                {
                    AddCommandFailureIfCurrent(
                        commandName,
                        "Command canceled before completion",
                        acceptedCommandGeneration);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void AddCommandFailureIfCurrent(
        string commandName,
        string error,
        long acceptedGeneration)
    {
        lock (commandGenerationGate)
        {
            if (acceptedGeneration != Volatile.Read(ref commandGeneration))
            {
                return;
            }
            AddCommandFailure(commandName, error);
        }
    }

    private void AddCommandFailure(string commandName, string error) =>
        AddEventCore(
            DateTime.Now,
            "CHATSTRONOMY-COMMAND-FAILED",
            chatEnabled: eventDelivery.Current.ShouldSendEvent(
                "CHATSTRONOMY-COMMAND-FAILED"),
            ("Command", commandName),
            ("Error", RedactCommandError(error)));

    internal static string RedactCommandError(string error) =>
        LocalPathPattern.Replace(error, "[local path redacted]");

    private IProgress<ApplicationStatus> CreateProgress() =>
        new Progress<ApplicationStatus>(applicationStatus.StatusUpdate);

    private CancellationTokenSource ReplaceCommandToken(ref CancellationTokenSource? field)
    {
        lock (commandGate)
        {
            CancelCommandCore(ref field);
            field = new CancellationTokenSource();
            return field;
        }
    }

    private void CancelCommand(ref CancellationTokenSource? field)
    {
        lock (commandGate)
        {
            CancelCommandCore(ref field);
        }
    }

    private static void CancelCommandCore(ref CancellationTokenSource? field)
    {
        var stop = field;
        field = null;
        if (stop is null)
        {
            return;
        }
        // N.I.N.A. command implementations can continue registering callbacks
        // with the token while their asynchronous cancellation unwinds.  A
        // cancelled source is collectible; disposing it here can race those
        // registrations and turn a requested cancellation into an
        // ObjectDisposedException inside N.I.N.A.
        stop.Cancel();
    }

    private void CancelOutstandingCommands()
    {
        lock (commandGate)
        {
            CancelCommandCore(ref guideCommandStop);
            CancelCommandCore(ref cameraCommandStop);
            CancelCommandCore(ref autofocusCommandStop);
        }
    }

    private static TimeSpan ResolveDuration(
        double? requestedMinutes,
        double profileMinutes,
        string name)
    {
        var minutes = RequiredFinite(requestedMinutes, name);
        if (minutes == -1)
        {
            minutes = profileMinutes;
        }
        if (minutes < 0)
        {
            throw new InvalidOperationException($"{name} must be zero or greater, or -1 for the profile default.");
        }
        return TimeSpan.FromMinutes(minutes);
    }

    private static double RequiredFinite(double? value, string name)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            throw new InvalidOperationException($"{name} must be a finite number.");
        }
        return value.Value;
    }

    private static T RunOnUiThread<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? action()
            : dispatcher.Invoke(action);
    }

    private IReadOnlyDictionary<string, object?> GetMountInfo()
    {
        var info = telescope.GetInfo();
        var access = accessPolicy.Current;
        var coordinates = info.Coordinates;
        var coordinateRa = FiniteOrZero(coordinates?.RA ?? info.RightAscension);
        var coordinateDec = FiniteOrZero(coordinates?.Dec ?? info.Declination);
        var coordinateRaDegrees = FiniteOrZero(coordinates?.RADegrees ?? coordinateRa * 15);
        var coordinateEpoch = coordinates?.Epoch.ToString() ?? info.EquatorialSystem.ToString();
        var utcNow = coordinates?.DateTime.UtcNow ?? DateTime.UtcNow;
        var now = access.ShareObservatoryLocation
            ? coordinates?.DateTime.Now ?? DateTime.Now
            : utcNow;
        var emptyObject = new Dictionary<string, object?>();

        var snapshot = new Dictionary<string, object?>
        {
            ["SiderealTime"] = FiniteOrZero(info.SiderealTime),
            ["RightAscension"] = FiniteOrZero(info.RightAscension),
            ["Declination"] = FiniteOrZero(info.Declination),
            ["SiteLatitude"] = FiniteOrZero(info.SiteLatitude),
            ["SiteLongitude"] = FiniteOrZero(info.SiteLongitude),
            ["SiteElevation"] = FiniteInt(info.SiteElevation),
            ["RightAscensionString"] = info.RightAscensionString ?? string.Empty,
            ["DeclinationString"] = info.DeclinationString ?? string.Empty,
            ["Coordinates"] = new Dictionary<string, object?>
            {
                ["RA"] = coordinateRa,
                ["RAString"] = coordinates?.RAString ?? info.RightAscensionString ?? string.Empty,
                ["RADegrees"] = coordinateRaDegrees,
                ["Dec"] = coordinateDec,
                ["DecString"] = coordinates?.DecString ?? info.DeclinationString ?? string.Empty,
                ["Epoch"] = coordinateEpoch,
                ["DateTime"] = new Dictionary<string, object?>
                {
                    ["Now"] = now,
                    ["UtcNow"] = utcNow,
                },
            },
            ["TimeToMeridianFlip"] = FiniteOrZero(info.TimeToMeridianFlip),
            ["SideOfPier"] = info.SideOfPier.ToString(),
            ["Altitude"] = FiniteOrZero(info.Altitude),
            ["AltitudeString"] = info.AltitudeString ?? string.Empty,
            ["Azimuth"] = FiniteOrZero(info.Azimuth),
            ["AzimuthString"] = info.AzimuthString ?? string.Empty,
            ["SiderealTimeString"] = info.SiderealTimeString ?? string.Empty,
            ["HoursToMeridianString"] = info.HoursToMeridianString ?? string.Empty,
            ["AtPark"] = info.AtPark,
            ["TrackingRate"] = emptyObject,
            ["TrackingEnabled"] = info.TrackingEnabled,
            ["TrackingModes"] = info.TrackingModes?.Select(mode => mode.ToString()).ToArray()
                ?? Array.Empty<string>(),
            ["AtHome"] = info.AtHome,
            ["CanFindHome"] = info.CanFindHome,
            ["CanPark"] = info.CanPark,
            ["CanSetPark"] = info.CanSetPark,
            ["CanSetTrackingEnabled"] = info.CanSetTrackingEnabled,
            ["CanSetDeclinationRate"] = info.CanSetDeclinationRate,
            ["CanSetRightAscensionRate"] = info.CanSetRightAscensionRate,
            ["EquatorialSystem"] = info.EquatorialSystem.ToString(),
            ["HasUnknownEpoch"] = info.HasUnknownEpoch,
            ["TimeToMeridianFlipString"] = info.TimeToMeridianFlipString ?? string.Empty,
            ["Slewing"] = info.Slewing,
            ["GuideRateRightAscensionArcsecPerSec"] =
                FiniteOrZero(info.GuideRateRightAscensionArcsecPerSec),
            ["GuideRateDeclinationArcsecPerSec"] =
                FiniteOrZero(info.GuideRateDeclinationArcsecPerSec),
            ["CanMovePrimaryAxis"] = info.CanMovePrimaryAxis,
            ["CanMoveSecondaryAxis"] = info.CanMoveSecondaryAxis,
            ["PrimaryAxisRates"] = info.PrimaryAxisRates?.Select(_ => emptyObject).ToArray()
                ?? Array.Empty<Dictionary<string, object?>>(),
            ["SecondaryAxisRates"] = info.SecondaryAxisRates?.Select(_ => emptyObject).ToArray()
                ?? Array.Empty<Dictionary<string, object?>>(),
            ["SupportedActions"] = info.SupportedActions?.ToArray() ?? Array.Empty<string>(),
            ["AlignmentMode"] = info.AlignmentMode.ToString(),
            ["CanPulseGuide"] = info.CanPulseGuide,
            ["IsPulseGuiding"] = info.IsPulseGuiding,
            ["CanSetPierSide"] = info.CanSetPierSide,
            ["CanSlew"] = info.CanSlew,
            ["UTCDate"] = info.UTCDate,
            ["Connected"] = info.Connected,
            ["Name"] = info.Name ?? string.Empty,
            ["DisplayName"] = info.DisplayName ?? string.Empty,
            ["DeviceId"] = info.DeviceId ?? string.Empty,
        };
        DirectPrivacyProjection.RedactMount(snapshot, access);
        return snapshot;
    }

    private DirectRotatorInfo GetRotatorInfo()
    {
        var info = rotator.GetInfo();
        return new DirectRotatorInfo(
            info.Connected,
            info.CanReverse,
            info.Reverse,
            info.Position,
            info.MechanicalPosition,
            info.StepSize,
            info.IsMoving,
            info.Synced);
    }

    private DirectFocuserInfo GetFocuserInfo()
    {
        var info = focuser.GetInfo();
        return new DirectFocuserInfo(
            info.Connected,
            info.Position,
            info.StepSize,
            info.Temperature,
            info.IsMoving,
            info.IsSettling,
            info.TempComp,
            info.TempCompAvailable);
    }

    private DirectFilterWheelInfo GetFilterWheelInfo()
    {
        var info = filterWheel.GetInfo();
        var available = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters
            .Select(filter => new DirectFilterInfo(filter.Name ?? string.Empty, filter.Position))
            .ToArray();
        var selected = info.Connected && info.SelectedFilter is { } filter
            ? new DirectFilterInfo(filter.Name ?? string.Empty, filter.Position)
            : null;
        return new DirectFilterWheelInfo(
            info.Connected,
            info.Name ?? string.Empty,
            info.DisplayName ?? string.Empty,
            info.IsMoving,
            selected,
            available);
    }

    private DirectCameraInfo GetCameraInfo()
    {
        var info = camera.GetInfo();
        return new DirectCameraInfo(
            info.Connected,
            info.CanSetTemperature,
            info.CoolerOn,
            info.CoolerPower,
            info.Temperature,
            info.TemperatureSetPoint,
            info.Connected && info.CanSetTemperature && camera.AtTargetTemp,
            info.Name ?? string.Empty,
            info.DisplayName ?? string.Empty);
    }

    private DirectGuiderInfo GetGuiderInfo()
    {
        var info = guider.GetInfo();
        var state = (guider.GetDevice() as IGuider)?.State ?? string.Empty;
        return new DirectGuiderInfo(
            info.Connected,
            info.Name ?? string.Empty,
            info.DisplayName ?? string.Empty,
            state,
            FiniteOrZero(info.PixelScale),
            info.RMSError);
    }

    private DirectGuiderGraph GetGuiderGraph()
    {
        if (!eventDelivery.Current.Guiding)
        {
            throw new InvalidOperationException("Guiding graph sharing is disabled.");
        }

        var info = guider.GetInfo();
        var pixelScale = FiniteOrZero(info.PixelScale);
        var settings = profileService.ActiveProfile.GuiderSettings;
        var historySize = Math.Clamp(settings.PHD2HistorySize, 1, GuideHistoryCapacity);
        var scale = settings.PHD2GuiderScale;
        var displayScale = scale == GuiderScaleEnum.ARCSECONDS ? pixelScale : 1;
        var steps = guideSteps.Snapshot()
            .TakeLast(historySize)
            .Select(step => step with
            {
                RADistanceRawDisplay = step.RADistanceRaw * displayScale,
                DECDistanceRawDisplay = step.DECDistanceRaw * displayScale,
            })
            .ToArray();
        var measuredSteps = steps.Where(step => step.Dither == "NO").ToArray();
        var rms = measuredSteps.Length == 0
            ? null
            : DirectGuideRms.FromSteps(measuredSteps, pixelScale);
        var maxDistance = double.IsFinite(settings.MaxY) && settings.MaxY > 0
            ? settings.MaxY
            : 4;
        var maxDuration = measuredSteps.Length == 0
            ? 1
            : Math.Max(1, measuredSteps.Max(
                step => Math.Max(Math.Abs(step.RADuration), Math.Abs(step.DECDuration))));
        return new DirectGuiderGraph(
            rms,
            Interval: maxDistance / 4,
            MaxY: maxDistance,
            MinY: -maxDistance,
            MaxDurationY: maxDuration,
            MinDurationY: -maxDuration,
            GuideSteps: steps,
            HistorySize: historySize,
            PixelScale: pixelScale,
            Scale: (int)scale);
    }

    private DirectThumbnail GetThumbnail(uint? index)
    {
        if (!eventDelivery.Current.Images)
        {
            throw new InvalidOperationException(
                "Image sharing is disabled in this N.I.N.A. profile.");
        }
        var sharedImages = SnapshotSharedImages();
        if (!index.HasValue || index.Value >= sharedImages.Count)
        {
            throw new InvalidOperationException("The requested image is no longer in history.");
        }

        var savedImage = sharedImages[(int)index.Value];
        var data = savedImage.ThumbnailData;
        if (data is null)
        {
            throw new InvalidOperationException("The image thumbnail is still being prepared.");
        }
        return new DirectThumbnail(data, "image/jpeg", 200);
    }

    private IReadOnlyList<DirectImageMetadata> SnapshotImagesForQuery(
        DirectQuery query,
        CancellationToken? directSessionToken)
    {
        var replayBarrier = GetImageReplayBarrier(directSessionToken);
        var snapshot = images.SnapshotEntries();
        RegisterImageHistoryQuery(
            query.Id,
            directSessionToken,
            replayBarrier is not null,
            snapshot.Count == 0 ? 0 : snapshot[^1].Sequence);
        return FilterSharedImages(snapshot, replayBarrier)
            .Select(image => image.Metadata)
            .ToArray();
    }

    private IReadOnlyList<DirectSavedImage> SnapshotSharedImages() =>
        FilterSharedImages(images.SnapshotEntries(), maximumSequence: null);

    private IReadOnlyList<DirectSavedImage> FilterSharedImages(
        IReadOnlyList<BoundedHistoryEntry<DirectSavedImage>> snapshot,
        long? maximumSequence)
    {
        if (!eventDelivery.Current.Images)
        {
            return Array.Empty<DirectSavedImage>();
        }

        // Consent at capture time and at query time are both required. An
        // image taken while forwarding was disabled must not reappear after
        // the user enables a later imaging session.
        return snapshot
            .Where(entry => !maximumSequence.HasValue
                || entry.Sequence <= maximumSequence.Value)
            .Select(entry => entry.Item)
            .Where(image => image.Metadata.ChatEnabled)
            .ToArray();
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
        var historyGeneration = CaptureHistoryGeneration();
        var captureGeneration = Volatile.Read(ref imageCaptureGeneration);
        var metadata = args.MetaData;
        var statistics = args.Statistics;
        var starAnalysis = args.StarDetectionAnalysis;
        var image = new DirectImageMetadata(
            ExposureTime: FiniteOrZero(args.Duration),
            ImageType: metadata?.Image?.ImageType ?? string.Empty,
            Filter: args.Filter ?? string.Empty,
            RmsText: metadata?.Image?.RecordedRMS?.TotalText ?? string.Empty,
            Temperature: FiniteOrZero(metadata?.Camera?.Temperature ?? 0),
            CameraName: metadata?.Camera?.Name ?? string.Empty,
            Gain: metadata?.Camera?.Gain ?? -1,
            Offset: metadata?.Camera?.Offset ?? -1,
            Date: DateTime.Now,
            TelescopeName: metadata?.Telescope?.Name ?? string.Empty,
            FocalLength: FiniteInt(metadata?.Telescope?.FocalLength ?? 0),
            StDev: FiniteOrZero(statistics?.StDev ?? 0),
            Mean: FiniteOrZero(statistics?.Mean ?? 0),
            Median: FiniteOrZero(statistics?.Median ?? 0),
            Stars: starAnalysis?.DetectedStars ?? 0,
            HFR: FiniteOrZero(starAnalysis?.HFR ?? 0),
            IsBayered: args.IsBayered,
            ChatEnabled: eventDelivery.Current.Images);
        var savedImage = new DirectSavedImage(image);
        if (!AddImageHistoryIfCurrent(
            savedImage,
            historyGeneration,
            captureGeneration))
        {
            return;
        }
        AddEvent(historyGeneration, "IMAGE-SAVE");
        if (image.ChatEnabled)
        {
            // Images captured without consent can never leave N.I.N.A. Avoid
            // cloning/freezing their large WPF source on its UI thread or
            // retaining a private JPEG in a needless background encoding job.
            QueueThumbnail(args.Image, savedImage);
        }
    }

    private static void QueueThumbnail(BitmapSource? source, DirectSavedImage savedImage)
    {
        if (source is null)
        {
            return;
        }

        BitmapSource frozen = source;
        if (!source.IsFrozen)
        {
            frozen = source.Clone();
            frozen.Freeze();
        }

        _ = Task.Run(() =>
        {
            try
            {
                savedImage.ThumbnailData = DirectThumbnailEncoder.Encode(frozen);
            }
            catch
            {
                // The metadata remains useful and the runtime degrades to a
                // notification without an attachment.
            }
        });
    }

    private void GuiderGuideEvent(object? sender, IGuideStep step)
    {
        var historyGeneration = CaptureHistoryGeneration();
        var captureGeneration = Volatile.Read(ref guideCaptureGeneration);
        if (!eventDelivery.Current.Guiding)
        {
            return;
        }

        var id = Interlocked.Increment(ref guideStepId);
        AddGuideHistoryIfCurrent(new DirectGuideStep(
            Id: id,
            IdOffsetLeft: id - 0.15,
            IdOffsetRight: id + 0.15,
            RADistanceRaw: FiniteOrZero(step.RADistanceRaw),
            RADistanceRawDisplay: FiniteOrZero(step.RADistanceRaw),
            RADuration: FiniteOrZero(step.RADuration),
            DECDistanceRaw: FiniteOrZero(step.DECDistanceRaw),
            DECDistanceRawDisplay: FiniteOrZero(step.DECDistanceRaw),
            DECDuration: FiniteOrZero(step.DECDuration),
            Dither: "NO"), historyGeneration, captureGeneration);
    }

    public Task OnMessageReceived(IMessage message)
    {
        var historyGeneration = CaptureHistoryGeneration();
        var eventName = message.Topic switch
        {
            "TargetScheduler-WaitStart" => "TS-WAITSTART",
            "TargetScheduler-NewTargetStart" => "TS-NEWTARGETSTART",
            "TargetScheduler-TargetStart" => "TS-TARGETSTART",
            _ => null,
        };
        if (eventName is null)
        {
            return Task.CompletedTask;
        }

        if (eventName == "TS-WAITSTART")
        {
            AddEventAt(
                historyGeneration,
                message.SentAt,
                eventName,
                ("WaitEndTime", message.Content));
            return Task.CompletedTask;
        }

        message.CustomHeaders.TryGetValue("ProjectName", out var projectName);
        message.CustomHeaders.TryGetValue("Coordinates", out var coordinates);
        message.CustomHeaders.TryGetValue("Rotation", out var rotation);
        AddEventAt(
            historyGeneration,
            message.SentAt,
            eventName,
            ("TargetName", Convert.ToString(message.Content) ?? string.Empty),
            ("ProjectName", Convert.ToString(projectName) ?? string.Empty),
            ("Coordinates", NinaDirectSequenceSnapshot.ProjectCoordinates(
                coordinates,
                accessPolicy.Current)),
            ("Rotation", rotation),
            ("TargetEndTime", message.Expiration));
        return Task.CompletedTask;
    }

    private void RecordLog(NinaLogRecord record, long historyGeneration)
    {
        if (!eventDelivery.Current.ShouldSendLogLevel(record.Level))
        {
            return;
        }
        AddHistoryIfCurrent(logEvents, BuildEvent(
            record.Time,
            "NINA-LOG",
            chatEnabled: true,
            ("Level", record.Level),
            ("Source", record.Source),
            ("Member", record.Member),
            ("Line", record.Line),
            ("Message", record.Message)), historyGeneration);
    }

    /// Equipment events and forwarded log lines, merged in time order.
    ///
    /// They are buffered separately so log volume cannot evict equipment
    /// history, but the consumer still expects one chronological list.
    private IReadOnlyList<Dictionary<string, object?>> SnapshotEventHistoryForQuery(
        DirectQuery query,
        CancellationToken? directSessionToken)
    {
        var replayBarrier = GetEventReplayBarrier(directSessionToken);
        var equipmentSnapshot = events.SnapshotEntries();
        var logSnapshot = logEvents.SnapshotEntries();
        RegisterEventHistoryQuery(
            query.Id,
            directSessionToken,
            replayBarrier is not null,
            equipmentSnapshot,
            equipmentSnapshot.Count == 0 ? 0 : equipmentSnapshot[^1].Sequence,
            logSnapshot.Count == 0 ? 0 : logSnapshot[^1].Sequence);
        return SnapshotEventHistory(
            equipmentSnapshot,
            logSnapshot,
            replayBarrier?.Equipment,
            replayBarrier?.Logs,
            replayBarrier?.ExcludedEquipment);
    }

    private IReadOnlyList<Dictionary<string, object?>> SnapshotEventHistory() =>
        SnapshotEventHistory(
            events.SnapshotEntries(),
            logEvents.SnapshotEntries(),
            maximumEquipmentSequence: null,
            maximumLogSequence: null,
            excludedEquipmentSequences: null);

    private IReadOnlyList<Dictionary<string, object?>> SnapshotEventHistory(
        IReadOnlyList<BoundedHistoryEntry<Dictionary<string, object?>>> equipmentSnapshot,
        IReadOnlyList<BoundedHistoryEntry<Dictionary<string, object?>>> logSnapshot,
        long? maximumEquipmentSequence,
        long? maximumLogSequence,
        IReadOnlySet<long>? excludedEquipmentSequences)
    {
        var access = accessPolicy.Current;
        var delivery = eventDelivery.Current;
        // Apply privacy choices on every poll, not just when an event first
        // entered its ring. Clone recursively so turning location or a log
        // level off takes effect immediately without destroying history the
        // user may explicitly choose to share again later.
        var equipment = equipmentSnapshot
            .Where(entry => !maximumEquipmentSequence.HasValue
                || entry.Sequence <= maximumEquipmentSequence.Value)
            .Where(entry => excludedEquipmentSequences is null
                || !excludedEquipmentSequences.Contains(entry.Sequence))
            .Select(entry => entry.Item)
            .Where(item => item.TryGetValue("Event", out var eventName)
                && eventName is string name
                && delivery.ShouldSendEvent(name)
                && item.TryGetValue("ChatEnabled", out var chatEnabled)
                && chatEnabled is true)
            .Select(item => DirectPrivacyProjection.RedactedCopy(item, access))
            .ToArray();
        var logs = logSnapshot
            .Where(entry => !maximumLogSequence.HasValue
                || entry.Sequence <= maximumLogSequence.Value)
            .Select(entry => entry.Item)
            .Where(item => item.TryGetValue("Level", out var level)
                && level is string name
                && delivery.ShouldSendLogLevel(name))
            .Select(item => DirectPrivacyProjection.RedactedCopy(item, access))
            .ToArray();
        if (logs.Length == 0)
        {
            return equipment;
        }

        return equipment
            .Concat(logs)
            .OrderBy(item => item.TryGetValue("Time", out var time) ? ToSortableTime(time) : default)
            .ToArray();
    }

    private static DateTimeOffset ToSortableTime(object? time) => time switch
    {
        DateTimeOffset offset => offset,
        DateTime value => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
        _ => default,
    };

    private void RecordNotification(
        NinaNotificationRecord notification,
        long historyGeneration)
    {
        if (!eventDelivery.Current.NinaNotifications)
        {
            return;
        }
        AddEventCore(
            historyGeneration,
            notification.Time,
            "NINA-NOTIFICATION",
            chatEnabled: true,
            ("Level", notification.Level),
            ("Header", notification.Header),
            ("Message", notification.Message));
    }

    private void AddEvent(string eventName, params (string Name, object? Value)[] details) =>
        AddEvent(CaptureHistoryGeneration(), eventName, details);

    private void AddEvent(
        long generation,
        string eventName,
        params (string Name, object? Value)[] details) =>
        AddEventCore(
            generation,
            DateTime.Now,
            eventName,
            eventDelivery.Current.ShouldSendEvent(eventName),
            details);

    private void AddEventAt(
        DateTimeOffset time,
        string eventName,
        params (string Name, object? Value)[] details) =>
        AddEventAt(CaptureHistoryGeneration(), time, eventName, details);

    private void AddEventAt(
        long generation,
        DateTimeOffset time,
        string eventName,
        params (string Name, object? Value)[] details) =>
        AddEventCore(
            generation,
            time,
            eventName,
            eventDelivery.Current.ShouldSendEvent(eventName),
            details);

    private void AddEventCore(
        object time,
        string eventName,
        bool chatEnabled,
        params (string Name, object? Value)[] details) =>
        AddEventCore(
            CaptureHistoryGeneration(),
            time,
            eventName,
            chatEnabled,
            details);

    private void AddEventCore(
        long generation,
        object time,
        string eventName,
        bool chatEnabled,
        params (string Name, object? Value)[] details) =>
        AddHistoryIfCurrent(
            events,
            BuildEvent(time, eventName, chatEnabled, details),
            generation);

    private static Dictionary<string, object?> BuildEvent(
        object time,
        string eventName,
        bool chatEnabled,
        params (string Name, object? Value)[] details)
    {
        var item = new Dictionary<string, object?>
        {
            ["Time"] = time,
            ["Event"] = eventName,
            ["ChatEnabled"] = chatEnabled,
        };
        foreach (var (name, value) in details)
        {
            // Omit absent details rather than sending an explicit null. The
            // consumer picks an event shape by which fields are present, and a
            // null in a typed slot fails that match — which used to drop the
            // whole event silently, so a target change was never reported.
            if (value is null)
            {
                continue;
            }
            item[name] = value;
        }
        return item;
    }

    private async Task SubscribeToSequenceWhenReadyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // N.I.N.A.'s SequenceMediator.Initialized getter itself cannot
                // be called before sequence navigation has registered.
                if (sequence.Initialized)
                {
                    lock (sequenceGate)
                    {
                        if (!started || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        sequence.SequenceStarting += SequenceStarting;
                        try
                        {
                            sequence.SequenceFinished += SequenceFinished;
                        }
                        catch
                        {
                            sequence.SequenceStarting -= SequenceStarting;
                            throw;
                        }
                        sequenceSubscribed = true;
                        return;
                    }
                }
            }
            catch (NullReferenceException)
            {
                // Sequence navigation has not registered yet.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private Task TelescopeConnected(object sender, EventArgs args) => AddSimpleEvent("MOUNT-CONNECTED");
    private Task TelescopeDisconnected(object sender, EventArgs args) => AddSimpleEvent("MOUNT-DISCONNECTED");
    private Task TelescopeBeforeMeridianFlip(object sender, BeforeMeridianFlipEventArgs args) => AddSimpleEvent("MOUNT-BEFORE-FLIP");
    private Task TelescopeAfterMeridianFlip(object sender, AfterMeridianFlipEventArgs args) => AddSimpleEvent("MOUNT-AFTER-FLIP");
    private Task TelescopeHomed(object sender, EventArgs args) => AddSimpleEvent("MOUNT-HOMED");
    private Task TelescopeParked(object sender, EventArgs args) => AddSimpleEvent("MOUNT-PARKED");
    private Task TelescopeUnparked(object sender, EventArgs args) => AddSimpleEvent("MOUNT-UNPARKED");
    private Task CameraConnected(object sender, EventArgs args) => AddSimpleEvent("CAMERA-CONNECTED");
    private Task CameraDisconnected(object sender, EventArgs args) => AddSimpleEvent("CAMERA-DISCONNECTED");
    private Task CameraDownloadTimeout(object sender, EventArgs args) => AddSimpleEvent("CAMERA-DOWNLOAD-TIMEOUT");
    private Task FilterWheelConnected(object sender, EventArgs args) => AddSimpleEvent("FILTERWHEEL-CONNECTED");
    private Task FilterWheelDisconnected(object sender, EventArgs args) => AddSimpleEvent("FILTERWHEEL-DISCONNECTED");
    private Task GuiderConnected(object sender, EventArgs args) => AddSimpleEvent("GUIDER-CONNECTED");
    private Task GuiderDisconnected(object sender, EventArgs args) => AddSimpleEvent("GUIDER-DISCONNECTED");
    private Task GuiderDithered(object sender, EventArgs args)
    {
        var historyGeneration = CaptureHistoryGeneration();
        var captureGeneration = Volatile.Read(ref guideCaptureGeneration);
        if (!eventDelivery.Current.Guiding)
        {
            return Task.CompletedTask;
        }

        var id = Interlocked.Increment(ref guideStepId);
        if (!AddGuideHistoryIfCurrent(new DirectGuideStep(
            Id: id,
            IdOffsetLeft: id - 0.15,
            IdOffsetRight: id + 0.15,
            RADistanceRaw: 0,
            RADistanceRawDisplay: 0,
            RADuration: 0,
            DECDistanceRaw: 0,
            DECDistanceRawDisplay: 0,
            DECDuration: 0,
            Dither: "0.01"), historyGeneration, captureGeneration))
        {
            return Task.CompletedTask;
        }
        AddEvent(historyGeneration, "GUIDER-DITHER");
        return Task.CompletedTask;
    }
    private Task GuiderStarted(object sender, EventArgs args) => AddSimpleEvent("GUIDER-START");
    private Task GuiderStopped(object sender, EventArgs args) => AddSimpleEvent("GUIDER-STOP");
    private Task RotatorConnected(object sender, EventArgs args) => AddSimpleEvent("ROTATOR-CONNECTED");
    private Task RotatorDisconnected(object sender, EventArgs args) => AddSimpleEvent("ROTATOR-DISCONNECTED");
    private Task FocuserConnected(object sender, EventArgs args) => AddSimpleEvent("FOCUSER-CONNECTED");
    private Task FocuserDisconnected(object sender, EventArgs args) => AddSimpleEvent("FOCUSER-DISCONNECTED");
    private Task SafetyConnected(object sender, EventArgs args)
    {
        RecordCurrentSafetyState(
            CaptureHistoryGeneration(),
            connectedFallback: true,
            safeFallback: false);
        return Task.CompletedTask;
    }

    private Task SafetyDisconnected(object sender, EventArgs args)
    {
        RecordSafetyState(
            CaptureHistoryGeneration(),
            connected: false,
            isSafe: false);
        return Task.CompletedTask;
    }

    private void SafetyChanged(object? sender, IsSafeEventArgs args) =>
        RecordCurrentSafetyState(
            CaptureHistoryGeneration(),
            connectedFallback: true,
            safeFallback: args.IsSafe);

    private void RecordCurrentSafetyState(
        long historyGeneration,
        bool connectedFallback,
        bool safeFallback)
    {
        try
        {
            var info = safetyMonitor.GetInfo();
            RecordSafetyState(
                historyGeneration,
                info?.Connected ?? connectedFallback,
                info?.IsSafe ?? safeFallback);
        }
        catch (Exception)
        {
            RecordSafetyState(historyGeneration, connectedFallback, safeFallback);
        }
    }

    private bool? GetSafetyMonitorIsSafe()
    {
        try
        {
            var info = safetyMonitor.GetInfo();
            return info.Connected ? info.IsSafe : null;
        }
        catch (Exception)
        {
            lock (safetyStateGate)
            {
                return safetyState switch
                {
                    DirectSafetyState.Safe => true,
                    DirectSafetyState.Unsafe => false,
                    _ => null,
                };
            }
        }
    }

    private void RecordSafetyState(long historyGeneration, bool connected, bool isSafe)
    {
        var next = !connected
            ? DirectSafetyState.Disconnected
            : isSafe
                ? DirectSafetyState.Safe
                : DirectSafetyState.Unsafe;
        DirectSafetyState previous;
        lock (safetyStateGate)
        {
            if (historyGeneration != CaptureHistoryGeneration())
            {
                return;
            }
            previous = safetyState;
            if (previous == next)
            {
                return;
            }
            safetyState = next;
        }

        // An unused safety monitor reports disconnected when the consumer is
        // first registered. Keep that baseline quiet; real transitions are
        // announced once a monitor connects.
        if (previous == DirectSafetyState.Unknown && next == DirectSafetyState.Disconnected)
        {
            return;
        }
        if (next == DirectSafetyState.Disconnected)
        {
            AddEvent(historyGeneration, "SAFETY-DISCONNECTED");
            return;
        }
        if (previous is DirectSafetyState.Unknown or DirectSafetyState.Disconnected)
        {
            AddEvent(historyGeneration, "SAFETY-CONNECTED");
        }
        AddEvent(
            historyGeneration,
            "SAFETY-CHANGED",
            ("IsSafe", next == DirectSafetyState.Safe));
    }

    private Task SequenceStarting(object sender, EventArgs args) => AddSimpleEvent("SEQUENCE-STARTING");
    private Task SequenceFinished(object sender, EventArgs args) => AddSimpleEvent("SEQUENCE-FINISHED");

    private Task FilterWheelChanged(object sender, FilterChangedEventArgs args)
    {
        var historyGeneration = CaptureHistoryGeneration();
        AddEvent(
            historyGeneration,
            "FILTERWHEEL-CHANGED",
            ("Previous", new DirectFilterInfo(args.From?.Name ?? string.Empty, args.From?.Position ?? -1)),
            ("New", new DirectFilterInfo(args.To?.Name ?? string.Empty, args.To?.Position ?? -1)));
        return Task.CompletedTask;
    }

    private Task RotatorMoved(object sender, RotatorEventArgs args)
    {
        var historyGeneration = CaptureHistoryGeneration();
        AddEvent(historyGeneration, "ROTATOR-MOVED", ("From", args.From), ("To", args.To));
        return Task.CompletedTask;
    }

    private Task RotatorMovedMechanical(object sender, RotatorEventArgs args)
    {
        var historyGeneration = CaptureHistoryGeneration();
        AddEvent(
            historyGeneration,
            "ROTATOR-MOVED-MECHANICAL",
            ("From", args.From),
            ("To", args.To));
        return Task.CompletedTask;
    }

    private void RotatorSynced(object? sender, RotatorEventArgs args) =>
        AddEvent(CaptureHistoryGeneration(), "ROTATOR-SYNCED");

    private Task AddSimpleEvent(string eventName)
    {
        AddEvent(CaptureHistoryGeneration(), eventName);
        return Task.CompletedTask;
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static int FiniteInt(double value) =>
        double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)
            : 0;
}

/// <summary>
/// Private cursor state for one authenticated Direct transport generation.
/// A successor rewinds one written response because transport completion does
/// not prove the peer finished processing it. Its first history response is a
/// replay-safe baseline; the next poll exposes the last delta plus anything
/// captured during the reconnect window.
/// </summary>
internal sealed class DirectHistorySession
{
    internal Guid Id { get; } = Guid.NewGuid();

    internal CancellationTokenSource Cancellation { get; } = new();

    internal bool EventHistoryQueried { get; set; }

    internal bool ImageHistoryQueried { get; set; }

    internal bool EventReplayPending { get; set; }

    internal bool ImageReplayPending { get; set; }

    internal long LastEquipmentEvent { get; set; }

    internal long LastLogEvent { get; set; }

    internal long LastImage { get; set; }

    internal long PriorEquipmentEvent { get; set; }

    internal long PriorLogEvent { get; set; }

    internal long PriorImage { get; set; }

    internal HashSet<long> PendingAutofocusEvents { get; } = [];

    internal DirectEventHistoryObservation? PendingEventQuery { get; set; }

    internal DirectImageHistoryObservation? PendingImageQuery { get; set; }

    internal DirectHistorySession CreateSuccessor()
    {
        var successor = new DirectHistorySession
        {
            EventHistoryQueried = EventHistoryQueried,
            ImageHistoryQueried = ImageHistoryQueried,
            EventReplayPending = EventHistoryQueried,
            ImageReplayPending = ImageHistoryQueried,
            // Rewind one confirmed poll. A transport write is not an
            // application-level acknowledgement: the peer can be cancelled
            // while parsing or posting that response. Replaying the last
            // delta makes forced reconnects at-least-once.
            LastEquipmentEvent = PriorEquipmentEvent,
            LastLogEvent = PriorLogEvent,
            LastImage = PriorImage,
            PriorEquipmentEvent = PriorEquipmentEvent,
            PriorLogEvent = PriorLogEvent,
            PriorImage = PriorImage,
        };
        successor.PendingAutofocusEvents.UnionWith(PendingAutofocusEvents);
        return successor;
    }

    internal void RewindForPhysicalTransport()
    {
        PendingEventQuery = null;
        PendingImageQuery = null;
        if (EventHistoryQueried)
        {
            EventReplayPending = true;
            LastEquipmentEvent = PriorEquipmentEvent;
            LastLogEvent = PriorLogEvent;
        }
        if (ImageHistoryQueried)
        {
            ImageReplayPending = true;
            LastImage = PriorImage;
        }
    }
}

internal sealed record DirectEventHistoryObservation(
    Guid QueryId,
    bool WasReplayBaseline,
    long EquipmentTail,
    long LogTail,
    IReadOnlyList<long> NewAutofocusEvents);

internal sealed record DirectImageHistoryObservation(
    Guid QueryId,
    bool WasReplayBaseline,
    long ImageTail);

internal sealed record DirectEventHistoryBarrier(
    long Equipment,
    long Logs,
    IReadOnlySet<long> ExcludedEquipment);

internal enum DirectSafetyState
{
    Unknown,
    Disconnected,
    Safe,
    Unsafe,
}

internal sealed record DirectAutofocusCompletion(
    string Filter,
    double Position,
    double Temperature,
    DateTime Timestamp,
    Guid? ProfileId,
    bool ChatEnabled);

internal sealed record DirectFilterInfo(string Name, int Id);

internal sealed record DirectCameraInfo(
    bool Connected,
    bool CanSetTemperature,
    bool CoolerOn,
    double CoolerPower,
    double Temperature,
    double TemperatureSetPoint,
    bool AtTargetTemp,
    string Name,
    string DisplayName);

internal sealed record DirectRotatorInfo(
    bool Connected,
    bool CanReverse,
    bool Reverse,
    double Position,
    double MechanicalPosition,
    double StepSize,
    bool IsMoving,
    bool Synced);

internal sealed record DirectFocuserInfo(
    bool Connected,
    int Position,
    double StepSize,
    double Temperature,
    bool IsMoving,
    bool IsSettling,
    bool TempComp,
    bool TempCompAvailable);

internal sealed record DirectFilterWheelInfo(
    bool Connected,
    string Name,
    string DisplayName,
    bool IsMoving,
    DirectFilterInfo? SelectedFilter,
    IReadOnlyList<DirectFilterInfo> AvailableFilters);

internal sealed record DirectGuiderInfo(
    bool Connected,
    string Name,
    string DisplayName,
    string State,
    double PixelScale,
    object? RMSError);

internal sealed record DirectImageMetadata(
    double ExposureTime,
    string ImageType,
    string Filter,
    string RmsText,
    double Temperature,
    string CameraName,
    int Gain,
    int Offset,
    DateTime Date,
    string TelescopeName,
    int FocalLength,
    double StDev,
    double Mean,
    double Median,
    int Stars,
    double HFR,
    bool IsBayered,
    bool ChatEnabled);

internal sealed class DirectSavedImage(DirectImageMetadata metadata)
{
    internal DirectImageMetadata Metadata { get; } = metadata;

    internal byte[]? ThumbnailData { get; set; }
}

internal sealed record DirectThumbnail(
    [property: JsonPropertyName("data")] byte[] Data,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("status_code")] ushort StatusCode);

internal sealed record DirectGuiderGraph(
    [property: JsonPropertyName("RMS")] DirectGuideRms? RMS,
    double Interval,
    double MaxY,
    double MinY,
    double MaxDurationY,
    double MinDurationY,
    IReadOnlyList<DirectGuideStep> GuideSteps,
    int HistorySize,
    double PixelScale,
    int Scale);

internal sealed record DirectGuideRms(
    [property: JsonPropertyName("RA")] double RA,
    double Dec,
    double Total,
    [property: JsonPropertyName("RAText")] string RAText,
    string DecText,
    string TotalText,
    [property: JsonPropertyName("PeakRAText")] string PeakRAText,
    string PeakDecText,
    double Scale,
    [property: JsonPropertyName("PeakRA")] double PeakRA,
    double PeakDec,
    int DataPoints)
{
    internal static DirectGuideRms FromSteps(
        IReadOnlyList<DirectGuideStep> steps,
        double pixelScale)
    {
        var meanRa = steps.Average(step => step.RADistanceRaw);
        var meanDec = steps.Average(step => step.DECDistanceRaw);
        var ra = Math.Sqrt(steps.Average(step => Math.Pow(step.RADistanceRaw - meanRa, 2)));
        var dec = Math.Sqrt(steps.Average(step => Math.Pow(step.DECDistanceRaw - meanDec, 2)));
        var total = Math.Sqrt(ra * ra + dec * dec);
        var peakRa = steps.Max(step => Math.Abs(step.RADistanceRaw));
        var peakDec = steps.Max(step => Math.Abs(step.DECDistanceRaw));
        return new DirectGuideRms(
            ra,
            dec,
            total,
            $"RA: {ra:0.00} ({ra * pixelScale:0.00}\")",
            $"Dec: {dec:0.00} ({dec * pixelScale:0.00}\")",
            $"Tot: {total:0.00} ({total * pixelScale:0.00}\")",
            $"RA Peak: {peakRa:0.00} ({peakRa * pixelScale:0.00}\")",
            $"Dec Peak: {peakDec:0.00} ({peakDec * pixelScale:0.00}\")",
            pixelScale,
            peakRa,
            peakDec,
            steps.Count);
    }
}

internal sealed record DirectGuideStep(
    long Id,
    double IdOffsetLeft,
    double IdOffsetRight,
    [property: JsonPropertyName("RADistanceRaw")] double RADistanceRaw,
    [property: JsonPropertyName("RADistanceRawDisplay")] double RADistanceRawDisplay,
    [property: JsonPropertyName("RADuration")] double RADuration,
    [property: JsonPropertyName("DECDistanceRaw")] double DECDistanceRaw,
    [property: JsonPropertyName("DECDistanceRawDisplay")] double DECDistanceRawDisplay,
    [property: JsonPropertyName("DECDuration")] double DECDuration,
    string Dither);
