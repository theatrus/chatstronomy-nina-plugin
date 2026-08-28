using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Utility;
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
    IWeatherDataConsumer,
    ISubscriber
{
    private const int EventHistoryCapacity = 10_000;
    private const int ImageHistoryCapacity = 500;
    private const int GuideHistoryCapacity = 10_000;
    private const string SequenceFailureDeliveryScopesField =
        "ChatstronomyRequiredDeliveryScopes";
    /// Forwarded log lines live in their own ring. Sharing the equipment ring
    /// let an evening of INFO chatter evict every real event, leaving the
    /// consumer's state reconstruction with nothing to read.
    private const int LogHistoryCapacity = 500;
    private static readonly TimeSpan WeatherChangeMinimumInterval = TimeSpan.FromMinutes(5);
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
    private readonly IDomeMediator dome;
    private readonly IFlatDeviceMediator flatDevice;
    private readonly IWeatherDataMediator weatherData;
    private readonly ISwitchMediator switchMediator;
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
    private readonly NinaImageSaveFailureWatcher imageSaveFailureWatcher;
    private readonly Func<BitmapSource, byte[]> thumbnailEncoder;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly BoundedHistory<Dictionary<string, object?>> events =
        new(EventHistoryCapacity);
    private readonly BoundedHistory<Dictionary<string, object?>> logEvents =
        new(LogHistoryCapacity);
    private readonly BoundedHistory<DirectSavedImage> images =
        new(ImageHistoryCapacity);
    private readonly BoundedHistory<DirectGuideStep> guideSteps =
        new(GuideHistoryCapacity);
    private readonly object sequenceGate = new();
    private readonly object sequenceFailureGate = new();
    private CancellationTokenSource? sequenceSubscriptionStop;
    private bool sequenceSubscribed;
    private Func<object, EventArgs, Task>? sequenceStartingHandler;
    private Func<object, EventArgs, Task>? sequenceFinishedHandler;
    private long sequenceLifecycleVersion;
    private bool sequenceSubscriptionsPaused;
    private bool sequenceRunning;
    private ISequenceRootContainer? sequenceFailureRoot;
    private Func<object, SequenceEntityFailureEventArgs, Task>? sequenceFailureHandler;
    private long sequenceFailureGeneration = -1;
    private long sequenceFailureSubscriptionVersion;
    private string? lastSequenceFailureKey;
    private DateTimeOffset lastSequenceFailureAt;
    private bool sequenceHadFailure;
    private bool sequenceOutcomeProvenanceComplete;
    private bool sequenceFailureCoverageBlocked;
    private readonly object commandGate = new();
    private readonly object commandGenerationGate = new();
    private readonly object historyGenerationGate = new();
    private readonly object autofocusReportGate = new();
    private readonly object safetyStateGate = new();
    private readonly object weatherStateGate = new();
    private readonly object directSessionGate = new();
    private readonly object thumbnailGate = new();
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
    private bool autofocusSharingBlocked;
    private bool autofocusRunActive;
    private bool autofocusRunContinuouslyShareable;
    private long autofocusRunGeneration = -1;
    private long autofocusCaptureGeneration;
    private long imageCaptureGeneration;
    private long guideCaptureGeneration;
    private long historyGeneration;
    private bool historyWritesSuspended;
    private DirectSafetyState safetyState;
    private long safetyStateRevision;
    private long safetyPublicationEpoch;
    private bool safetySharingBlocked;
    private DirectWeatherSnapshot? weatherBaseline;
    private DateTimeOffset lastWeatherChangePublishedAt;
    private DirectHighWindState highWindState;
    private bool highWindRequiredSpeed;
    private bool highWindRequiredGust;
    private bool highWindThresholdReconciliationPending;
    private bool highWindConnectionReconciliationPending;
    private bool? weatherConnectionState;
    private long weatherChangesPublicationEpoch;
    private long highWindPublicationEpoch;
    private bool weatherChangesSharingBlocked;
    private bool highWindSharingBlocked;
    private long guideStepId;
    private long commandGeneration;
    private ThumbnailWork? pendingThumbnail;
    private bool thumbnailWorkerActive;
    private long thumbnailWorkEpoch;
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
        IDomeMediator dome,
        IFlatDeviceMediator flatDevice,
        IWeatherDataMediator weatherData,
        ISwitchMediator switchMediator,
        IImageSaveMediator imageSave,
        IApplicationStatusMediator applicationStatus,
        IAutoFocusVMFactory autoFocusFactory,
        IImageHistoryVM imageHistory,
        IWindowServiceFactory windowFactory,
        IMessageBroker messageBroker,
        DirectEventDeliveryPolicy eventDelivery,
        DirectAccessPolicy accessPolicy,
        string? autofocusReportDirectory = null,
        Func<BitmapSource, byte[]>? thumbnailEncoder = null,
        Func<DateTimeOffset>? utcNow = null)
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
        this.dome = dome;
        this.flatDevice = flatDevice;
        this.weatherData = weatherData;
        this.switchMediator = switchMediator;
        this.imageSave = imageSave;
        this.applicationStatus = applicationStatus;
        this.autoFocusFactory = autoFocusFactory;
        this.imageHistory = imageHistory;
        this.windowFactory = windowFactory;
        this.messageBroker = messageBroker;
        this.eventDelivery = eventDelivery;
        this.accessPolicy = accessPolicy;
        this.thumbnailEncoder = thumbnailEncoder ?? DirectThumbnailEncoder.Encode;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        autofocusSharingBlocked = !eventDelivery.Current.Autofocus;
        safetySharingBlocked = !eventDelivery.Current.Safety;
        weatherChangesSharingBlocked = !eventDelivery.Current.WeatherChanges;
        highWindSharingBlocked = !eventDelivery.Current.HighWindAlerts;
        sequenceFailureCoverageBlocked = !NinaDirectSequenceSnapshot
            .HasCompleteSequenceFailureCoverage(eventDelivery.Current);
        this.autofocusReportDirectory = autofocusReportDirectory
            ?? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
        logWatcher = new NinaLogWatcher(RecordLog);
        notificationWatcher = new NinaNotificationWatcher(RecordNotification);
        imageSaveFailureWatcher = new NinaImageSaveFailureWatcher(RecordImageSaveFailure);
    }

    private sealed record ThumbnailWork(
        long Epoch,
        BitmapSource Source,
        bool SourceIsFrozen,
        System.Windows.Threading.Dispatcher? SourceDispatcher,
        DirectSavedImage SavedImage,
        long HistoryGeneration,
        long ImageCaptureGeneration,
        CancellationToken ProviderCancellation,
        CancellationToken ProfileCancellation);

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
        sequenceSubscriptionsPaused = false;
        eventCaptureStop = new CancellationTokenSource();

        telescope.Connected += TelescopeConnected;
        telescope.Disconnected += TelescopeDisconnected;
        telescope.BeforeMeridianFlip += TelescopeBeforeMeridianFlip;
        telescope.AfterMeridianFlip += TelescopeAfterMeridianFlip;
        telescope.Homed += TelescopeHomed;
        telescope.Parked += TelescopeParked;
        telescope.Unparked += TelescopeUnparked;
        telescope.Slewed += TelescopeSlewed;

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

        dome.Connected += DomeConnected;
        dome.Disconnected += DomeDisconnected;
        dome.Synced += DomeSynced;
        dome.Opened += DomeOpened;
        dome.Closed += DomeClosed;
        dome.Parked += DomeParked;
        dome.Homed += DomeHomed;
        dome.Slewed += DomeSlewed;

        flatDevice.Connected += FlatConnected;
        flatDevice.Disconnected += FlatDisconnected;
        flatDevice.Opened += FlatOpened;
        flatDevice.Closed += FlatClosed;
        flatDevice.BrightnessChanged += FlatBrightnessChanged;
        flatDevice.LightToggled += FlatLightToggled;

        var weatherConnectedBeforeRegistration = false;
        try
        {
            // Read only the lifecycle bit. RegisterConsumer immediately sends
            // another current snapshot, and any edge which races this read is
            // detected by comparing that snapshot under weatherStateGate.
            weatherConnectedBeforeRegistration = weatherData.GetInfo()?.Connected == true;
        }
        catch (Exception)
        {
            // No registered weather handler is equivalent to disconnected for
            // startup edge detection. The mediator broadcast will correct it.
        }
        lock (weatherStateGate)
        {
            weatherConnectionState = weatherConnectedBeforeRegistration;
        }
        weatherData.Connected += WeatherConnected;
        weatherData.Disconnected += WeatherDisconnected;
        weatherData.RegisterConsumer(this);
        switchMediator.Connected += SwitchConnected;
        switchMediator.Disconnected += SwitchDisconnected;

        imageSave.ImageSaved += ImageSaved;
        imageSaveFailureWatcher.Start(imageSave);
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
        var imagePolicyChanged = previous.Images != current.Images;
        lock (historyGenerationGate)
        {
            if (imagePolicyChanged)
            {
                imageCaptureGeneration++;
            }
            if (previous.Guiding != current.Guiding)
            {
                guideCaptureGeneration++;
            }
        }
        if (imagePolicyChanged)
        {
            CancelPendingThumbnailWork();
        }
        if (previous.Safety != current.Safety)
        {
            lock (safetyStateGate)
            {
                // Invalidate every baseline read that began in the previous
                // consent epoch. A disable closes publication before the
                // policy object itself is replaced.
                safetyPublicationEpoch++;
                if (!current.Safety)
                {
                    safetySharingBlocked = true;
                }
            }
        }
        if (previous.WeatherChanges != current.WeatherChanges)
        {
            lock (weatherStateGate)
            {
                weatherChangesPublicationEpoch++;
                weatherChangesSharingBlocked = true;
                ClearWeatherChangeBaselineLocked();
                RemoveWeatherEventsFromHistory("WEATHER-CHANGED");
            }
        }
        var highWindToggleChanged = previous.HighWindAlerts != current.HighWindAlerts;
        var highWindThresholdChanged = previous.HighWindThresholdMetersPerSecond
            != current.HighWindThresholdMetersPerSecond;
        if (highWindToggleChanged || highWindThresholdChanged)
        {
            lock (weatherStateGate)
            {
                highWindPublicationEpoch++;
                highWindSharingBlocked = true;
                if (highWindToggleChanged)
                {
                    ResetHighWindStateLocked();
                    RemoveWeatherEventsFromHistory("WEATHER-HIGH-WIND");
                }
                else
                {
                    // Keep an active alert until the current reading can be
                    // reconciled against the new threshold. Silently erasing
                    // it would leave chat showing a stale high-wind state.
                    highWindThresholdReconciliationPending = true;
                }
            }
        }
        if (previous.Autofocus != current.Autofocus)
        {
            if (!current.Autofocus)
            {
                lock (autofocusReportGate)
                {
                    // Like sequence failure coverage, autofocus capture must
                    // close before the policy object publishes a disable.
                    autofocusSharingBlocked = true;
                }
            }
            // Completion provenance belongs to the consent state observed at
            // capture. Toggling autofocus sharing revokes every pending/cached
            // report so an old-enabled run cannot surface after re-enabling.
            InvalidateAutofocusCapture();
        }
        if (!NinaDirectSequenceSnapshot.HasCompleteSequenceFailureCoverage(current))
        {
            lock (sequenceGate)
            {
                // This runs before DirectEventDeliveryPolicy publishes the
                // new value. Block first so a SequenceStarting callback in
                // that window cannot observe the old all-enabled policy and
                // restore complete provenance.
                sequenceFailureCoverageBlocked = true;
                if (sequenceRunning)
                {
                    lock (sequenceFailureGate)
                    {
                        // A terminal success is provable only while every
                        // entity-failure category is visible. Invalidate at
                        // the policy boundary rather than when a hidden
                        // failure happens, so the neutral outcome itself does
                        // not reveal whether such a failure occurred.
                        sequenceOutcomeProvenanceComplete = false;
                        sequenceHadFailure = false;
                        lastSequenceFailureKey = null;
                        lastSequenceFailureAt = default;
                    }
                }
            }
        }
    }

    public void EventDeliveryPolicyChanged(
        DirectEventDeliveryOptions previous,
        DirectEventDeliveryOptions current)
    {
        if (previous.Autofocus != current.Autofocus)
        {
            lock (autofocusReportGate)
            {
                // Enabling opens only after publication. An active run that
                // crossed the transition remains unshareable through its
                // separate continuous-run provenance bit.
                autofocusSharingBlocked = !current.Autofocus;
            }
        }
        lock (sequenceGate)
        {
            // Enabling is published only after the policy value itself. A run
            // that started during the transition already marked its own
            // provenance incomplete and is intentionally not upgraded here.
            sequenceFailureCoverageBlocked = !NinaDirectSequenceSnapshot
                .HasCompleteSequenceFailureCoverage(current);
        }
        if (previous.Safety != current.Safety)
        {
            lock (safetyStateGate)
            {
                // Enabling opens only after the new policy is visible.
                // Incrementing again also protects callers that invoke only
                // the post-publication hook in a compatibility integration.
                safetyPublicationEpoch++;
                safetySharingBlocked = !current.Safety;
            }
        }
        var weatherChangesChanged = previous.WeatherChanges != current.WeatherChanges;
        var highWindPolicyChanged = previous.HighWindAlerts != current.HighWindAlerts
            || previous.HighWindThresholdMetersPerSecond
                != current.HighWindThresholdMetersPerSecond;
        if (weatherChangesChanged || highWindPolicyChanged)
        {
            lock (weatherStateGate)
            {
                if (weatherChangesChanged)
                {
                    weatherChangesPublicationEpoch++;
                    weatherChangesSharingBlocked = !current.WeatherChanges;
                }
                if (highWindPolicyChanged)
                {
                    highWindPublicationEpoch++;
                    highWindSharingBlocked = !current.HighWindAlerts;
                }
            }
        }
        if (!previous.Safety && current.Safety)
        {
            PublishCurrentSafetyBaseline(CaptureHistoryGeneration());
        }
        if ((!previous.WeatherChanges && current.WeatherChanges)
            || current.HighWindAlerts && highWindPolicyChanged)
        {
            PublishCurrentWeatherBaseline();
        }
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
        imageSaveFailureWatcher.Stop();
        CancelPendingThumbnailWork();
        lock (safetyStateGate)
        {
            safetyPublicationEpoch++;
            safetySharingBlocked = true;
        }
        ResetWeatherState(blockSharing: true);
        PauseSequenceSubscriptions();
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
        CancelPendingThumbnailWork();
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
        telescope.Slewed -= TelescopeSlewed;

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

        dome.Connected -= DomeConnected;
        dome.Disconnected -= DomeDisconnected;
        dome.Synced -= DomeSynced;
        dome.Opened -= DomeOpened;
        dome.Closed -= DomeClosed;
        dome.Parked -= DomeParked;
        dome.Homed -= DomeHomed;
        dome.Slewed -= DomeSlewed;

        flatDevice.Connected -= FlatConnected;
        flatDevice.Disconnected -= FlatDisconnected;
        flatDevice.Opened -= FlatOpened;
        flatDevice.Closed -= FlatClosed;
        flatDevice.BrightnessChanged -= FlatBrightnessChanged;
        flatDevice.LightToggled -= FlatLightToggled;

        weatherData.Connected -= WeatherConnected;
        weatherData.Disconnected -= WeatherDisconnected;
        weatherData.RemoveConsumer(this);
        switchMediator.Connected -= SwitchConnected;
        switchMediator.Disconnected -= SwitchDisconnected;
        lock (safetyStateGate)
        {
            if (safetyState != DirectSafetyState.Unknown)
            {
                safetyStateRevision++;
            }
            safetyState = DirectSafetyState.Unknown;
            safetyPublicationEpoch++;
            safetySharingBlocked = true;
        }
        ResetWeatherState(blockSharing: true);

        foreach (var topic in TargetSchedulerTopics)
        {
            messageBroker.Unsubscribe(topic, this);
        }
        logWatcher.Stop();
        notificationWatcher.Stop();

        var subscriptionStop = sequenceSubscriptionStop;
        sequenceSubscriptionStop = null;
        subscriptionStop?.Cancel();
        PauseSequenceSubscriptions();
        subscriptionStop?.Dispose();
        captureStop?.Dispose();
        imageSave.ImageSaved -= ImageSaved;
        imageSaveFailureWatcher.Stop();
        CancelOutstandingCommands();
    }

    public void Reset()
    {
        CancelPendingThumbnailWork();
        PauseSequenceSubscriptions();
        lock (sequenceGate)
        {
            // Profile synchronization publishes its policy directly between
            // RevokeProfileAccess and Reset rather than through the two-phase
            // per-setting callbacks. Rebuild the transition barrier from the
            // newly active profile before lifecycle handlers are rebound.
            sequenceFailureCoverageBlocked = !NinaDirectSequenceSnapshot
                .HasCompleteSequenceFailureCoverage(eventDelivery.Current);
        }
        lock (autofocusReportGate)
        {
            autofocusSharingBlocked = !eventDelivery.Current.Autofocus;
        }
        InvalidateAutofocusCapture();
        ResetHistoryBuffers();
        lock (sequenceFailureGate)
        {
            sequenceHadFailure = false;
            sequenceOutcomeProvenanceComplete = false;
            lastSequenceFailureKey = null;
            lastSequenceFailureAt = default;
        }
        lock (safetyStateGate)
        {
            if (safetyState != DirectSafetyState.Unknown)
            {
                safetyStateRevision++;
            }
            safetyState = DirectSafetyState.Unknown;
            safetyPublicationEpoch++;
            safetySharingBlocked = !eventDelivery.Current.Safety;
        }
        lock (weatherStateGate)
        {
            weatherChangesPublicationEpoch++;
            highWindPublicationEpoch++;
            weatherChangesSharingBlocked = !eventDelivery.Current.WeatherChanges;
            highWindSharingBlocked = !eventDelivery.Current.HighWindAlerts;
            weatherBaseline = null;
            lastWeatherChangePublishedAt = default;
            ResetHighWindStateLocked();
        }
        Interlocked.Exchange(ref guideStepId, 0);
        ApplyLogDeliveryOptions();
        _ = notificationWatcher.Start(CaptureHistoryGeneration());
        imageSaveFailureWatcher.Start(imageSave);
        if (eventDelivery.Current.Safety)
        {
            PublishCurrentSafetyBaseline(CaptureHistoryGeneration());
        }
        if (eventDelivery.Current.WeatherChanges || eventDelivery.Current.HighWindAlerts)
        {
            PublishCurrentWeatherBaseline();
        }
        ResumeSequenceSubscriptions();
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

    public void UpdateDeviceInfo(WeatherDataInfo deviceInfo)
    {
        if (!started || deviceInfo is null)
        {
            return;
        }

        long weatherChangesEpoch;
        long windEpoch;
        bool captureWeatherChanges;
        bool captureWind;
        lock (weatherStateGate)
        {
            if (!started)
            {
                return;
            }
            // N.I.N.A. broadcasts each connection-state snapshot before its
            // Connected/Disconnected callback. Record that edge first so a
            // simultaneous high-wind reading can never overtake it. Only the
            // boolean is observed when both weather permissions are off.
            RecordWeatherConnectionStateLocked(
                deviceInfo.Connected,
                emitWhenPreviouslyUnknown: false);
            if (!deviceInfo.Connected)
            {
                return;
            }
            weatherChangesEpoch = weatherChangesPublicationEpoch;
            windEpoch = highWindPublicationEpoch;
            captureWeatherChanges = !weatherChangesSharingBlocked
                && eventDelivery.Current.WeatherChanges;
            captureWind = !highWindSharingBlocked
                && eventDelivery.Current.HighWindAlerts;
        }
        if (!captureWeatherChanges && !captureWind)
        {
            return;
        }

        // WeatherDataVM mutates and rebroadcasts one WeatherDataInfo instance
        // from its polling worker. Copy only primitive, unit-defined readings
        // before taking our state lock; never retain the mutable N.I.N.A.
        // object or perform I/O on that worker.
        var snapshot = DirectWeatherSnapshot.FromNina(deviceInfo);
        var historyGeneration = CaptureHistoryGeneration();
        var observedAt = utcNow();
        lock (weatherStateGate)
        {
            if (!started || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return;
            }
            if (weatherConnectionState != true)
            {
                // A disconnect invalidated this mutable N.I.N.A. snapshot
                // while its primitive values were being copied.
                return;
            }
            if (!snapshot.Connected)
            {
                RecordWeatherConnectionStateLocked(
                    connected: false,
                    emitWhenPreviouslyUnknown: false);
                return;
            }
            var windEdgePublished = false;
            if (captureWind)
            {
                windEdgePublished = ProcessHighWindLocked(
                    snapshot,
                    historyGeneration,
                    windEpoch,
                    observedAt);
            }
            if (captureWeatherChanges)
            {
                if (windEdgePublished)
                {
                    // One sample should create one chat update. Keep the
                    // wind portion of the general baseline current so the
                    // edge is not repeated as a generic change on the next
                    // poll. Non-wind changes remain pending for that poll.
                    if (CanPublishWeatherChangesLocked(
                        historyGeneration,
                        weatherChangesEpoch))
                    {
                        weatherBaseline = weatherBaseline is null
                            ? snapshot
                            : weatherBaseline.MergeWindFrom(snapshot);
                    }
                }
                else
                {
                    ProcessWeatherChangesLocked(
                        snapshot,
                        historyGeneration,
                        weatherChangesEpoch,
                        observedAt);
                }
            }
        }
    }

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
        if (!IsHistoryGenerationCurrent(historyGeneration))
        {
            return;
        }
        DirectAutofocusCompletion completion;
        bool chatEnabled;
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

            // A saved report contains every point from the run, not only the
            // final measurement. Re-enabling autofocus sharing just before
            // completion must not release points captured during an off gap.
            chatEnabled = !autofocusSharingBlocked
                && eventDelivery.Current.Autofocus
                && autofocusRunActive
                && autofocusRunGeneration == generation
                && autofocusRunContinuouslyShareable;
            autofocusRunActive = false;
            autofocusRunContinuouslyShareable = false;
            autofocusRunGeneration = -1;
            completion = new DirectAutofocusCompletion(
                info.Filter ?? string.Empty,
                info.Position,
                info.Temperature,
                info.Timestamp,
                TryGetActiveProfileId(),
                chatEnabled);

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

    public void AutoFocusRunStarting()
    {
        var historyGeneration = CaptureHistoryGeneration();
        var generation = Volatile.Read(ref autofocusCaptureGeneration);
        bool chatEnabled;
        lock (autofocusReportGate)
        {
            if (!started || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return;
            }
            autofocusRunActive = true;
            autofocusRunGeneration = generation;
            autofocusRunContinuouslyShareable = !autofocusSharingBlocked
                && eventDelivery.Current.Autofocus
                && generation == Volatile.Read(ref autofocusCaptureGeneration);
            chatEnabled = autofocusRunContinuouslyShareable;
        }
        AddEventCore(
            historyGeneration,
            DateTime.Now,
            "AUTOFOCUS-STARTING",
            chatEnabled);
    }

    public void NewAutoFocusPoint(DataPoint dataPoint)
    {
        var historyGeneration = CaptureHistoryGeneration();
        var generation = Volatile.Read(ref autofocusCaptureGeneration);
        bool chatEnabled;
        lock (autofocusReportGate)
        {
            chatEnabled = !autofocusSharingBlocked
                && eventDelivery.Current.Autofocus
                && autofocusRunActive
                && autofocusRunGeneration == generation
                && autofocusRunContinuouslyShareable;
        }
        AddEventCore(
            historyGeneration,
            DateTime.Now,
            "AUTOFOCUS-POINT-ADDED",
            chatEnabled,
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
            if (!CacheAutofocusReport(
                matched,
                pending,
                pendingGeneration,
                out var cachedReport))
            {
                throw new InvalidOperationException(
                    "The autofocus profile session changed while reading the report.");
            }
            var redacted = DirectPrivacyProjection.Redact(cachedReport, accessPolicy.Current);
            RequireAutofocusSharing(generation);
            return redacted;
        }

        throw new InvalidOperationException(
            "No completed autofocus report is available for this profile session.");
    }

    private void RequireAutofocusSharing(long generation)
    {
        lock (autofocusReportGate)
        {
            if (autofocusSharingBlocked || !eventDelivery.Current.Autofocus)
            {
                throw new InvalidOperationException("Autofocus result sharing is disabled.");
            }
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
            if (!CacheAutofocusReport(report, completion, generation, out _))
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
        long generation,
        out JsonElement cachedReport)
    {
        lock (autofocusReportGate)
        {
            if (generation == Volatile.Read(ref autofocusCaptureGeneration)
                && pendingAutofocusGeneration == generation
                && pendingAutofocusCompletion == completion
                && !autofocusSharingBlocked
                && eventDelivery.Current.Autofocus
                && completion.ChatEnabled)
            {
                var merged = lastAutofocusReport is JsonElement current
                    ? MergeProjectedAutofocusReports(
                        current,
                        report,
                        preferIncomingConflicts: true)
                    : report.Clone();
                lastAutofocusReport = merged;
                cachedReport = merged.Clone();
                return true;
            }
        }
        cachedReport = default;
        return false;
    }

    internal bool TryCacheObservedAutofocusReport(
        JsonElement report,
        long generation,
        bool chatEnabledAtCompletion)
    {
        lock (autofocusReportGate)
        {
            if (autofocusSharingBlocked
                || !eventDelivery.Current.Autofocus
                || !chatEnabledAtCompletion
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

            JsonElement projected;
            try
            {
                projected = DirectAutofocusReportProjection.Project(
                    report,
                    pendingAutofocusCompletion);
            }
            catch (JsonException)
            {
                return false;
            }

            // Keep the completion provenance. In particular, a completion
            // captured with sharing off must remain unavailable after the
            // switch is turned on again.
            lastAutofocusReport = lastAutofocusReport is JsonElement current
                && pendingAutofocusCompletion is not null
                ? MergeProjectedAutofocusReports(
                    current,
                    projected,
                    preferIncomingConflicts: false)
                : projected;
            pendingAutofocusGeneration = generation;
            return true;
        }
    }

    private static JsonElement MergeProjectedAutofocusReports(
        JsonElement current,
        JsonElement incoming,
        bool preferIncomingConflicts)
    {
        // The caller knows which side came from Hocus/N.I.N.A.'s persisted
        // report. Keep that disk snapshot authoritative for conflicting
        // values regardless of whether it arrived before or after the
        // reflected command result. Missing enrichment is still backfilled.
        var primary = preferIncomingConflicts ? incoming : current;
        var secondary = preferIncomingConflicts ? current : incoming;
        var merged = JsonNode.Parse(primary.GetRawText())?.AsObject()
            ?? throw new JsonException("The projected autofocus report was not an object.");

        foreach (var name in new[]
        {
            "FinalHFR",
            "FinalHFRSource",
            "InitialHFRMeasured",
            "HyperbolicMinimumStdError",
            "HyperbolicReducedChiSquared",
            "HyperbolicLeaveOneOutStdError",
            "AcceptedStarCountMin",
            "AcceptedStarCountMax",
            "HyperbolicFitModelChosen",
            "Region",
            "HocusFocusAlgorithm",
        })
        {
            if (!merged.ContainsKey(name)
                && secondary.TryGetProperty(name, out var value)
                && value.ValueKind != JsonValueKind.Null)
            {
                merged[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        // Top-level presence is not enough to rank structured enrichment.
        // Merge these reviewed objects recursively so complementary disk and
        // reflection projections cannot erase one another based on callback
        // ordering. The disk-backed object supplies conflicting values while
        // every missing leaf is backfilled from the reflected result.
        foreach (var name in new[] { "Region", "HocusFocusAlgorithm" })
        {
            if (current.TryGetProperty(name, out var currentObject)
                && currentObject.ValueKind == JsonValueKind.Object
                && incoming.TryGetProperty(name, out var incomingObject)
                && incomingObject.ValueKind == JsonValueKind.Object)
            {
                merged[name] = MergeProjectedAutofocusObject(
                    currentObject,
                    incomingObject,
                    preferIncomingConflicts);
            }
        }

        return JsonSerializer.SerializeToElement(merged, DirectProtocol.JsonOptions);
    }

    private static JsonObject MergeProjectedAutofocusObject(
        JsonElement current,
        JsonElement incoming,
        bool preferIncomingConflicts)
    {
        var primary = preferIncomingConflicts ? incoming : current;
        var secondary = preferIncomingConflicts ? current : incoming;
        var merged = JsonNode.Parse(primary.GetRawText())?.AsObject()
            ?? throw new JsonException("The projected autofocus enrichment was not an object.");
        var fallback = JsonNode.Parse(secondary.GetRawText())?.AsObject()
            ?? throw new JsonException("The projected autofocus enrichment was not an object.");
        BackfillMissingJsonProperties(merged, fallback);
        return merged;
    }

    private static void BackfillMissingJsonProperties(
        JsonObject primary,
        JsonObject fallback)
    {
        foreach (var property in fallback)
        {
            if (!primary.TryGetPropertyValue(property.Key, out var current)
                || current is null)
            {
                primary[property.Key] = property.Value?.DeepClone();
            }
            else if (current is JsonObject currentObject
                && property.Value is JsonObject fallbackObject)
            {
                BackfillMissingJsonProperties(currentObject, fallbackObject);
            }
        }
    }

    internal static JsonElement SerializeObservedAutofocusReport(AutoFocusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Serialize only N.I.N.A.'s common report surface. A runtime-type
        // serialization would also traverse Hocus Focus settings containing
        // local paths and device details before the privacy projection runs.
        var serialized = JsonSerializer.SerializeToElement<AutoFocusReport>(
            report,
            DirectProtocol.JsonOptions);
        var root = JsonNode.Parse(serialized.GetRawText())?.AsObject()
            ?? throw new JsonException("The observed autofocus report was not an object.");

        // Hocus Focus returns AutoFocusReport polymorphically. Copy its small,
        // reviewed result surface one property at a time without traversing
        // the settings objects that also live on the derived report.
        foreach (var name in new[]
        {
            "FinalHFR",
            "HyperbolicMinimumStdError",
            "HyperbolicReducedChiSquared",
            "HyperbolicLeaveOneOutStdError",
        })
        {
            CopyObservedDoubleProperty(report, root, name);
        }
        foreach (var name in new[] { "AcceptedStarCountMin", "AcceptedStarCountMax" })
        {
            CopyObservedIntegerProperty(report, root, name);
        }
        CopyObservedFitModel(report, root);
        CopyObservedRegion(report, root);
        CopyObservedHocusAlgorithm(report, root);

        return JsonSerializer.SerializeToElement(root, DirectProtocol.JsonOptions);
    }

    private static object? ReadObservedProperty(object source, string name)
    {
        try
        {
            var property = source.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public);
            return property?.GetIndexParameters().Length == 0
                ? property.GetValue(source)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CopyObservedDoubleProperty(
        AutoFocusReport report,
        JsonObject root,
        string name)
    {
        if (ReadObservedProperty(report, name) is double value)
        {
            root[name] = value;
        }
    }

    private static void CopyObservedIntegerProperty(
        AutoFocusReport report,
        JsonObject root,
        string name)
    {
        if (ReadObservedProperty(report, name) is int value)
        {
            root[name] = value;
        }
    }

    private static void CopyObservedFitModel(AutoFocusReport report, JsonObject root)
    {
        var value = ReadObservedProperty(report, "HyperbolicFitModelChosen");
        if (value is not null && value.GetType().IsEnum)
        {
            root["HyperbolicFitModelChosen"] = NormalizeObservedEnumName(
                value.ToString() ?? string.Empty);
        }
        else if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            root["HyperbolicFitModelChosen"] = NormalizeObservedEnumName(text);
        }
    }

    private static void CopyObservedRegion(AutoFocusReport report, JsonObject root)
    {
        var region = ReadObservedProperty(report, "Region");
        var outer = region is null ? null : ReadObservedProperty(region, "OuterBoundary");
        if (region is null || outer is null || ProjectObservedRatioRect(outer) is not JsonObject outerJson)
        {
            return;
        }

        var index = ReadObservedProperty(region, "Index") is int regionIndex
            ? regionIndex
            : 0;
        var inner = ReadObservedProperty(region, "InnerCropBoundary");
        root["Region"] = new JsonObject
        {
            ["Index"] = index,
            ["OuterBoundary"] = outerJson,
            ["InnerCropBoundary"] = inner is null
                ? null
                : ProjectObservedRatioRect(inner),
        };
    }

    private static JsonObject? ProjectObservedRatioRect(object rectangle)
    {
        if (ReadObservedProperty(rectangle, "StartX") is not double startX
            || ReadObservedProperty(rectangle, "StartY") is not double startY
            || ReadObservedProperty(rectangle, "Width") is not double width
            || ReadObservedProperty(rectangle, "Height") is not double height
            || !double.IsFinite(startX)
            || !double.IsFinite(startY)
            || !double.IsFinite(width)
            || !double.IsFinite(height))
        {
            return null;
        }

        return new JsonObject
        {
            ["StartX"] = startX,
            ["StartY"] = startY,
            ["Width"] = width,
            ["Height"] = height,
        };
    }

    private static void CopyObservedHocusAlgorithm(
        AutoFocusReport report,
        JsonObject root)
    {
        var autofocus = ReadObservedProperty(report, "HocusFocusAutoFocusOptions");
        var focuser = ReadObservedProperty(report, "FocuserOptions");
        var detection = ReadObservedProperty(report, "HocusFocusStarDetectionOptions");
        if (autofocus is null && focuser is null && detection is null)
        {
            return;
        }

        var algorithm = new JsonObject();
        if (autofocus is not null)
        {
            foreach (var name in new[]
            {
                "ValidateHfrImprovement",
                "WeightedHyperbolicFitEnabled",
            })
            {
                CopyObservedBooleanProperty(autofocus, algorithm, name);
            }
            foreach (var name in new[]
            {
                "HFRImprovementThreshold",
                "ReducedChiSquaredRejectionThreshold",
                "OutlierRejectionConfidence",
            })
            {
                CopyObservedNestedDoubleProperty(autofocus, algorithm, name);
            }
            CopyObservedNestedIntegerProperty(
                autofocus,
                algorithm,
                "MaxOutlierRejections");
            CopyObservedEnumProperty(
                autofocus,
                algorithm,
                "HyperbolicFitModel",
                "ConfiguredHyperbolicModel");
            CopyObservedEnumProperty(
                autofocus,
                algorithm,
                "FitRejectionCriterion",
                "FitRejectionCriterion");

            if (ReadObservedProperty(autofocus, "ValidateHfrImprovement") is bool validate)
            {
                root["InitialHFRMeasured"] = validate;
                root["FinalHFRSource"] = validate
                    ? "measured_validation"
                    : "fitted_estimate";
            }
        }

        if (focuser is not null)
        {
            CopyObservedNestedDoubleProperty(
                focuser,
                algorithm,
                "RSquaredThreshold");
        }

        if (detection is not null)
        {
            foreach (var name in new[]
            {
                "ModelPSF",
                "UseOptimizedSettings",
                "HasOptimizedSettings",
            })
            {
                CopyObservedBooleanProperty(detection, algorithm, name);
            }
            CopyObservedEnumOrIntegerProperty(
                detection,
                algorithm,
                "DetectionBinning");
            CopyObservedEnumProperty(
                detection,
                algorithm,
                "MeasurementAverage",
                "MeasurementAverage");
            var optimized = ReadObservedProperty(detection, "UseOptimizedSettings")
                is true;
            var hasOptimized = ReadObservedProperty(detection, "HasOptimizedSettings")
                is true;
            var advanced = ReadObservedProperty(detection, "UseAdvanced") is true;
            algorithm["StarDetectionMode"] = advanced
                ? "Advanced"
                : optimized && hasOptimized ? "Optimized" : "Simple";
        }

        if (algorithm.Count > 0)
        {
            root["HocusFocusAlgorithm"] = algorithm;
        }
    }

    private static void CopyObservedBooleanProperty(
        object source,
        JsonObject target,
        string name)
    {
        if (ReadObservedProperty(source, name) is bool value)
        {
            target[name] = value;
        }
    }

    private static void CopyObservedNestedDoubleProperty(
        object source,
        JsonObject target,
        string name)
    {
        if (ReadObservedProperty(source, name) is double value && double.IsFinite(value))
        {
            target[name] = value;
        }
    }

    private static void CopyObservedNestedIntegerProperty(
        object source,
        JsonObject target,
        string name)
    {
        if (ReadObservedProperty(source, name) is int value)
        {
            target[name] = value;
        }
    }

    private static void CopyObservedEnumOrIntegerProperty(
        object source,
        JsonObject target,
        string name)
    {
        var value = ReadObservedProperty(source, name);
        if (value is int integer)
        {
            target[name] = integer;
        }
        else if (value is not null && value.GetType().IsEnum)
        {
            target[name] = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void CopyObservedEnumProperty(
        object source,
        JsonObject target,
        string sourceName,
        string targetName)
    {
        var value = ReadObservedProperty(source, sourceName);
        if (value is null || !value.GetType().IsEnum)
        {
            return;
        }

        target[targetName] = NormalizeObservedEnumName(value.ToString() ?? string.Empty);
    }

    private static string NormalizeObservedEnumName(string name) => name switch
        {
            "UnevenBlend" => "Uneven Blend",
            "TiltedHyperbola" => "Tilted Hyperbola",
            "SmoothBlend" => "Smooth Blend",
            "Hybrid" => "Hybrid (Best Fit)",
            "RSquared" => "R²",
            "ReducedChiSquared" => "Reduced χ²",
            "MeanOutliers" => "Mean + outlier detection",
            _ => name,
        };

    private void InvalidateAutofocusCapture()
    {
        var generation = Interlocked.Increment(ref autofocusCaptureGeneration);
        lock (autofocusReportGate)
        {
            if (autofocusRunActive)
            {
                autofocusRunContinuouslyShareable = false;
            }
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
                            var profileSuffix = $"--{profileId:D}.json";
                            var isProfileScoped = Path.GetFileName(candidate).EndsWith(
                                profileSuffix,
                                StringComparison.OrdinalIgnoreCase);
                            if (MatchesAutofocusCompletion(
                                report,
                                completion,
                                requireStrongCorrelation: !isProfileScoped))
                            {
                                return DirectAutofocusReportProjection.Project(report, completion);
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

        throw DirectQueryFailureException.ResourceNotReady(
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
        var timestamps = AutofocusFilenameTimestamps(completionTimestamp);

        // N.I.N.A. and Hocus Focus name reports from a separate wall-clock
        // read after building the report. Probe their bounded filename skew,
        // then require the payload timestamp to match the callback exactly.
        // Prefer every report explicitly scoped to the active profile before trying
        // the less authoritative profileless Hocus Focus format.
        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profilePath = Path.Combine(directory, $"{timestamp}{profileSuffix}");
            if (File.Exists(profilePath))
            {
                yield return profilePath;
            }
        }

        // Older Hocus Focus releases publish the same base N.I.N.A. report
        // without a profile suffix. Require the payload itself to strongly
        // match this observed completion.
        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profilelessPath = Path.Combine(directory, $"{timestamp}.json");
            if (File.Exists(profilelessPath))
            {
                yield return profilelessPath;
            }
        }
    }

    private static IReadOnlyList<string> AutofocusFilenameTimestamps(
        DateTime completionTimestamp)
    {
        var wallClocks = completionTimestamp.Kind == DateTimeKind.Utc
            ? new[] { completionTimestamp.ToLocalTime(), completionTimestamp }
            : new[] { completionTimestamp };
        var offsets = new[] { 0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5 };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var timestamps = new List<string>();
        foreach (var offset in offsets)
        {
            foreach (var wallClock in wallClocks)
            {
                var timestamp = $"{wallClock.AddSeconds(offset):yyyy-MM-dd--HH-mm-ss}";
                if (seen.Add(timestamp))
                {
                    timestamps.Add(timestamp);
                }
            }
        }
        return timestamps;
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
        DirectAutofocusCompletion completion,
        bool requireStrongCorrelation = false)
    {
        var payload = DirectAutofocusReportProjection.Unwrap(report);
        if (!payload.TryGetProperty("Timestamp", out var timestampValue)
            || !TryGetDateTimeOffset(timestampValue, out var timestamp)
            || AutofocusTimestampDifference(timestamp, completion.Timestamp) != TimeSpan.Zero)
        {
            return false;
        }

        var matchedIdentityField = false;
        if (!string.IsNullOrWhiteSpace(completion.Filter))
        {
            var hasFilter = payload.TryGetProperty("Filter", out var filterValue)
                && filterValue.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(filterValue.GetString());
            if (hasFilter)
            {
                if (!completion.Filter.Equals(
                    filterValue.GetString(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                matchedIdentityField = true;
            }
            else
            {
                return false;
            }
        }

        if (double.IsFinite(completion.Position))
        {
            var position = double.NaN;
            var hasPosition = payload.TryGetProperty(
                    "CalculatedFocusPoint",
                    out var calculated)
                && calculated.ValueKind == JsonValueKind.Object
                && calculated.TryGetProperty("Position", out var positionValue)
                && positionValue.TryGetDouble(out position)
                && double.IsFinite(position);
            if (hasPosition)
            {
                if (Math.Abs(position - completion.Position) > 1)
                {
                    return false;
                }
                matchedIdentityField = true;
            }
            else
            {
                return false;
            }
        }

        return !requireStrongCorrelation || matchedIdentityField;
    }

    private static TimeSpan AutofocusTimestampDifference(
        DateTimeOffset reportTimestamp,
        DateTime completionTimestamp)
    {
        // N.I.N.A. callbacks commonly expose a local wall-clock DateTime with
        // no Kind, while JSON reports include the local UTC offset. Compare
        // the wall clock in that case so host timezone settings cannot turn a
        // matching report into a false miss.
        return completionTimestamp.Kind == DateTimeKind.Unspecified
            ? (reportTimestamp.DateTime - completionTimestamp).Duration()
            : (reportTimestamp - new DateTimeOffset(completionTimestamp)).Duration();
    }

    private static bool TryGetDateTimeOffset(
        JsonElement value,
        out DateTimeOffset timestamp)
    {
        if (value.TryGetDateTimeOffset(out timestamp))
        {
            return true;
        }
        if (value.TryGetDateTime(out var dateTime))
        {
            timestamp = dateTime.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(
                    dateTime,
                    TimeZoneInfo.Local.GetUtcOffset(dateTime))
                : new DateTimeOffset(dateTime);
            return true;
        }
        timestamp = default;
        return false;
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
                    var serialized = SerializeObservedAutofocusReport(report);
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
            chatEnabled: true,
            ("Command", commandName),
            ("Error", RedactCommandError(error)));

    internal static string RedactCommandError(string error) =>
        LocalPathPattern.Replace(error, "[local path redacted]");

    internal static string SanitizeEventText(string? value, int maximumLength)
    {
        var redacted = RedactCommandError(value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return redacted.Length <= maximumLength
            ? redacted
            : $"{redacted[..maximumLength]}…";
    }

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
            QueueThumbnail(
                args.Image,
                savedImage,
                historyGeneration,
                captureGeneration);
        }
    }

    private void QueueThumbnail(
        BitmapSource? source,
        DirectSavedImage savedImage,
        long historyGeneration,
        long captureGeneration)
    {
        if (source is null)
        {
            return;
        }

        bool sourceIsFrozen;
        System.Windows.Threading.Dispatcher? sourceDispatcher;
        try
        {
            sourceIsFrozen = source.IsFrozen;
            sourceDispatcher = source.Dispatcher;
        }
        catch (Exception)
        {
            // A third-party saver can raise its callback on a thread that
            // does not own the WPF image. Metadata is still retained; never
            // let optional thumbnail work fail the image-save callback.
            return;
        }

        var providerCancellation = eventCaptureStop?.Token;
        if (!started
            || providerCancellation is null
            || providerCancellation.Value.IsCancellationRequested)
        {
            return;
        }

        lock (thumbnailGate)
        {
            var epoch = thumbnailWorkEpoch;
            pendingThumbnail = new ThumbnailWork(
                epoch,
                source,
                sourceIsFrozen,
                sourceDispatcher,
                savedImage,
                historyGeneration,
                captureGeneration,
                providerCancellation.Value,
                ProfileSessionToken);
            if (thumbnailWorkerActive)
            {
                return;
            }
            thumbnailWorkerActive = true;
        }
        _ = Task.Run(ProcessThumbnailQueueAsync);
    }

    private async Task ProcessThumbnailQueueAsync()
    {
        while (true)
        {
            ThumbnailWork? work;
            lock (thumbnailGate)
            {
                work = pendingThumbnail;
                pendingThumbnail = null;
                if (work is null)
                {
                    thumbnailWorkerActive = false;
                    return;
                }
            }

            try
            {
                if (!IsThumbnailWorkCurrent(work))
                {
                    continue;
                }
                var frozen = await FreezeThumbnailSourceAsync(work).ConfigureAwait(false);
                if (frozen is null || !IsThumbnailWorkCurrent(work))
                {
                    continue;
                }
                var encoded = thumbnailEncoder(frozen);
                if (IsThumbnailWorkCurrent(work))
                {
                    work.SavedImage.ThumbnailData = encoded;
                }
            }
            catch (Exception)
            {
                // The metadata remains useful and the runtime degrades to a
                // notification without an attachment. This observational
                // worker must never fail N.I.N.A.'s image-save callback.
            }
        }
    }

    private async Task<BitmapSource?> FreezeThumbnailSourceAsync(ThumbnailWork work)
    {
        if (work.SourceIsFrozen)
        {
            return work.Source;
        }

        var dispatcher = work.SourceDispatcher;
        if (dispatcher is null)
        {
            return null;
        }
        var operation = dispatcher.InvokeAsync(
            () =>
            {
                if (!IsThumbnailWorkCurrent(work))
                {
                    return null;
                }
                var frozen = work.Source.Clone();
                frozen.Freeze();
                return frozen;
            },
            System.Windows.Threading.DispatcherPriority.Background);
        return await operation.Task.ConfigureAwait(false);
    }

    private bool IsThumbnailWorkCurrent(ThumbnailWork work)
    {
        if (!started
            || work.ProviderCancellation.IsCancellationRequested
            || work.ProfileCancellation.IsCancellationRequested
            || work.Epoch != Volatile.Read(ref thumbnailWorkEpoch)
            || !eventDelivery.Current.Images)
        {
            return false;
        }
        lock (historyGenerationGate)
        {
            return !historyWritesSuspended
                && work.HistoryGeneration == historyGeneration
                && work.ImageCaptureGeneration == imageCaptureGeneration;
        }
    }

    private void CancelPendingThumbnailWork()
    {
        lock (thumbnailGate)
        {
            thumbnailWorkEpoch++;
            pendingThumbnail = null;
        }
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
                && chatEnabled is true
                && StoredEventScopesEnabled(item, delivery))
            .Select(item => ProjectEventForTransport(item, access))
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

    private static bool StoredEventScopesEnabled(
        IReadOnlyDictionary<string, object?> item,
        DirectEventDeliveryOptions delivery) =>
        !item.TryGetValue(SequenceFailureDeliveryScopesField, out var value)
        || value is int deliveryScopeMask
            && NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(
                deliveryScopeMask,
                delivery);

    private static Dictionary<string, object?> ProjectEventForTransport(
        IReadOnlyDictionary<string, object?> item,
        DirectAccessOptions access)
    {
        var projected = DirectPrivacyProjection.RedactedCopy(item, access);
        projected.Remove(SequenceFailureDeliveryScopesField);
        return projected;
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

    private void RecordImageSaveFailure(NinaImageSaveFailureRecord failure)
    {
        AddEventCore(
            CaptureHistoryGeneration(),
            DateTime.Now,
            "IMAGE-SAVE-FAILED",
            eventDelivery.Current.Images,
            ("Stage", SanitizeEventText(failure.Stage, 128)),
            ("DiskFull", failure.DiskFull),
            ("Error", SanitizeEventText(failure.Error, 1_024)));
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
        _ = TryAddEventCore(
            generation,
            time,
            eventName,
            chatEnabled,
            details);

    private bool TryAddEventCore(
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
                if (TrySubscribeToSequenceLifecycle(cancellationToken))
                {
                    RefreshSequenceFailureRoot();
                }
            }
            catch (NullReferenceException)
            {
                // Sequence navigation has not registered yet.
            }

            try
            {
                await Task.Delay(
                        sequenceSubscribed
                            ? TimeSpan.FromSeconds(1)
                            : TimeSpan.FromMilliseconds(250),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool TrySubscribeToSequenceLifecycle(CancellationToken cancellationToken)
    {
        if (!started || cancellationToken.IsCancellationRequested || !sequence.Initialized)
        {
            return false;
        }

        lock (sequenceGate)
        {
            if (!started
                || sequenceSubscriptionsPaused
                || cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (sequenceSubscribed)
            {
                return true;
            }

            var lifecycleVersion = Interlocked.Increment(ref sequenceLifecycleVersion);
            var historyGeneration = CaptureHistoryGeneration();
            Func<object, EventArgs, Task> starting = (sender, args) =>
                HandleSequenceStarting(sender, args, lifecycleVersion, historyGeneration);
            Func<object, EventArgs, Task> finished = (sender, args) =>
                HandleSequenceFinished(sender, args, lifecycleVersion, historyGeneration);
            sequenceStartingHandler = starting;
            sequenceFinishedHandler = finished;
            try
            {
                sequence.SequenceStarting += starting;
                sequence.SequenceFinished += finished;
                sequenceSubscribed = true;
                return true;
            }
            catch
            {
                // Invalidate an invocation list that captured the first
                // handler before the second subscription failed.
                Interlocked.Increment(ref sequenceLifecycleVersion);
                try
                {
                    sequence.SequenceStarting -= starting;
                }
                catch
                {
                    // The navigation object may be rotating. The version
                    // guard still makes an orphaned callback inert.
                }
                try
                {
                    sequence.SequenceFinished -= finished;
                }
                catch
                {
                }
                sequenceStartingHandler = null;
                sequenceFinishedHandler = null;
                sequenceSubscribed = false;
                throw;
            }
        }
    }

    private void PauseSequenceSubscriptions()
    {
        lock (sequenceGate)
        {
            sequenceSubscriptionsPaused = true;
            sequenceRunning = false;
            DetachSequenceLifecycleHandlers();
            BindSequenceFailureRoot(null);
        }
    }

    private void ResumeSequenceSubscriptions()
    {
        lock (sequenceGate)
        {
            if (!started)
            {
                return;
            }
            sequenceSubscriptionsPaused = false;
        }

        try
        {
            if (TrySubscribeToSequenceLifecycle(CancellationToken.None))
            {
                RefreshSequenceFailureRoot();
            }
        }
        catch (NullReferenceException)
        {
            // The background subscription loop will retry once sequence
            // navigation finishes rotating to the new profile.
        }
    }

    /// <summary>Caller must hold <see cref="sequenceGate"/>.</summary>
    private void DetachSequenceLifecycleHandlers()
    {
        Interlocked.Increment(ref sequenceLifecycleVersion);
        var starting = sequenceStartingHandler;
        var finished = sequenceFinishedHandler;
        sequenceStartingHandler = null;
        sequenceFinishedHandler = null;
        sequenceSubscribed = false;

        if (starting is not null)
        {
            try
            {
                sequence.SequenceStarting -= starting;
            }
            catch
            {
                // The old navigation object can disappear during a profile
                // switch. Its captured delegate is inert by version anyway.
            }
        }
        if (finished is not null)
        {
            try
            {
                sequence.SequenceFinished -= finished;
            }
            catch
            {
            }
        }
    }

    private void RefreshSequenceFailureRoot(
        Func<ISequenceRootContainer?>? readRoot = null)
    {
        long lifecycleVersion;
        long historyGeneration;
        lock (sequenceGate)
        {
            if (!started || sequenceSubscriptionsPaused || sequenceRunning)
            {
                return;
            }
            lifecycleVersion = Volatile.Read(ref sequenceLifecycleVersion);
            historyGeneration = CaptureHistoryGeneration();
        }

        ISequenceRootContainer? root = null;
        try
        {
            if (readRoot is not null)
            {
                root = readRoot();
            }
            else if (sequence.Initialized)
            {
                root = RunOnUiThread(() => NinaDirectSequenceSnapshot.TryGetSequenceRoot(sequence));
            }
        }
        catch (NullReferenceException)
        {
            // Sequence navigation has not registered yet.
        }

        lock (sequenceGate)
        {
            if (started
                && !sequenceSubscriptionsPaused
                && !sequenceRunning
                && lifecycleVersion == Volatile.Read(ref sequenceLifecycleVersion)
                && IsHistoryGenerationCurrent(historyGeneration))
            {
                BindSequenceFailureRoot(root);
            }
        }
    }

    /// <summary>Caller must hold <see cref="sequenceGate"/>.</summary>
    private void BindSequenceFailureRoot(ISequenceRootContainer? root)
    {
        var historyGeneration = CaptureHistoryGeneration();
        if (ReferenceEquals(sequenceFailureRoot, root)
            && (root is null || sequenceFailureGeneration == historyGeneration))
        {
            return;
        }

        var subscriptionVersion = Interlocked.Increment(
            ref sequenceFailureSubscriptionVersion);

        if (sequenceFailureRoot is not null && sequenceFailureHandler is not null)
        {
            try
            {
                sequenceFailureRoot.FailureEvent -= sequenceFailureHandler;
            }
            catch
            {
                // A detached root may already be tearing down. The version
                // guard prevents any captured invocation from writing.
            }
        }
        sequenceFailureRoot = null;
        sequenceFailureHandler = null;
        sequenceFailureGeneration = -1;

        if (root is null)
        {
            return;
        }

        Func<object, SequenceEntityFailureEventArgs, Task> handler =
            (_, args) => SequenceEntityFailed(
                args,
                historyGeneration,
                root,
                subscriptionVersion);
        root.FailureEvent += handler;
        sequenceFailureRoot = root;
        sequenceFailureHandler = handler;
        sequenceFailureGeneration = historyGeneration;
    }

    private Task TelescopeConnected(object sender, EventArgs args) => AddSimpleEvent("MOUNT-CONNECTED");
    private Task TelescopeDisconnected(object sender, EventArgs args) => AddSimpleEvent("MOUNT-DISCONNECTED");
    private Task TelescopeBeforeMeridianFlip(object sender, BeforeMeridianFlipEventArgs args) => AddSimpleEvent("MOUNT-BEFORE-FLIP");
    private Task TelescopeAfterMeridianFlip(object sender, AfterMeridianFlipEventArgs args) => AddSimpleEvent("MOUNT-AFTER-FLIP");
    private Task TelescopeHomed(object sender, EventArgs args) => AddSimpleEvent("MOUNT-HOMED");
    private Task TelescopeParked(object sender, EventArgs args) => AddSimpleEvent("MOUNT-PARKED");
    private Task TelescopeUnparked(object sender, EventArgs args) => AddSimpleEvent("MOUNT-UNPARKED");
    private Task TelescopeSlewed(object sender, MountSlewedEventArgs args)
    {
        var access = accessPolicy.Current;
        AddEvent(
            "MOUNT-SLEWED",
            ("From", NinaDirectSequenceSnapshot.ProjectCoordinates(args.From, access)),
            ("To", NinaDirectSequenceSnapshot.ProjectCoordinates(args.To, access)));
        return Task.CompletedTask;
    }
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
    private Task DomeConnected(object sender, EventArgs args) => AddSimpleEvent("DOME-CONNECTED");
    private Task DomeDisconnected(object sender, EventArgs args) => AddSimpleEvent("DOME-DISCONNECTED");
    private void DomeSynced(object? sender, EventArgs args) => AddEvent("DOME-SYNCED");
    private Task DomeOpened(object sender, EventArgs args) => AddSimpleEvent("DOME-SHUTTER-OPENED");
    private Task DomeClosed(object sender, EventArgs args) => AddSimpleEvent("DOME-SHUTTER-CLOSED");
    private Task DomeParked(object sender, EventArgs args) => AddSimpleEvent("DOME-PARKED");
    private Task DomeHomed(object sender, EventArgs args) => AddSimpleEvent("DOME-HOMED");
    private Task DomeSlewed(object sender, DomeEventArgs args)
    {
        AddEvent("DOME-SLEWED", ("FromAzimuth", args.From), ("ToAzimuth", args.To));
        return Task.CompletedTask;
    }
    private Task FlatConnected(object sender, EventArgs args) => AddSimpleEvent("FLAT-CONNECTED");
    private Task FlatDisconnected(object sender, EventArgs args) => AddSimpleEvent("FLAT-DISCONNECTED");
    private Task FlatOpened(object sender, EventArgs args) => AddSimpleEvent("FLAT-COVER-OPENED");
    private Task FlatClosed(object sender, EventArgs args) => AddSimpleEvent("FLAT-COVER-CLOSED");
    private Task FlatBrightnessChanged(object sender, FlatDeviceBrightnessChangedEventArgs args)
    {
        AddEvent("FLAT-BRIGHTNESS-CHANGED", ("Previous", args.From), ("New", args.To));
        return Task.CompletedTask;
    }
    private Task FlatLightToggled(object sender, EventArgs args)
    {
        bool? lightOn = null;
        try
        {
            lightOn = flatDevice.GetInfo().LightOn;
        }
        catch (Exception)
        {
            // The lifecycle transition remains useful even if a disconnect
            // races the state read; absent details are omitted on the wire.
        }
        AddEvent("FLAT-LIGHT-TOGGLED", ("On", lightOn));
        return Task.CompletedTask;
    }
    private Task WeatherConnected(object sender, EventArgs args)
    {
        bool publishCurrentSnapshot;
        lock (weatherStateGate)
        {
            // Normally the preceding mediator broadcast already recorded the
            // edge and the callback is a no-op. This remains a fallback for a
            // weather integration which raises Connected without broadcasting.
            publishCurrentSnapshot = RecordWeatherConnectionStateLocked(
                connected: true,
                emitWhenPreviouslyUnknown: true);
        }
        if (publishCurrentSnapshot)
        {
            PublishCurrentWeatherBaseline();
        }
        return Task.CompletedTask;
    }

    private Task WeatherDisconnected(object sender, EventArgs args)
    {
        lock (weatherStateGate)
        {
            // A missing station cannot prove that an active wind condition
            // recovered. Preserve the high-wind latch so the first complete
            // reading after reconnect can emit an authoritative recovery,
            // even when Equipment connection messages are disabled.
            RecordWeatherConnectionStateLocked(
                connected: false,
                emitWhenPreviouslyUnknown: true);
        }
        return Task.CompletedTask;
    }

    private bool RecordWeatherConnectionStateLocked(
        bool connected,
        bool emitWhenPreviouslyUnknown)
    {
        var previous = weatherConnectionState;
        weatherConnectionState = connected;
        if (previous == connected
            || !previous.HasValue && !emitWhenPreviouslyUnknown)
        {
            return false;
        }
        weatherChangesPublicationEpoch++;
        highWindPublicationEpoch++;
        ClearWeatherChangeBaselineLocked();
        highWindConnectionReconciliationPending =
            highWindState == DirectHighWindState.High;
        AddEvent(connected ? "WEATHER-CONNECTED" : "WEATHER-DISCONNECTED");
        return true;
    }

    private void PublishCurrentWeatherBaseline()
    {
        if (!started)
        {
            return;
        }
        try
        {
            UpdateDeviceInfo(weatherData.GetInfo());
        }
        catch (Exception)
        {
            // A disconnect can race the local snapshot read. The next normal
            // weather broadcast will establish the baseline if sharing is
            // still enabled; never turn an unavailable reading into a state
            // change or a false wind recovery.
        }
    }

    private void ProcessWeatherChangesLocked(
        DirectWeatherSnapshot snapshot,
        long historyGeneration,
        long publicationEpoch,
        DateTimeOffset observedAt)
    {
        if (!CanPublishWeatherChangesLocked(historyGeneration, publicationEpoch))
        {
            return;
        }
        if (!snapshot.HasAnyReading)
        {
            return;
        }
        if (weatherBaseline is null)
        {
            weatherBaseline = snapshot;
            return;
        }

        var baseline = weatherBaseline;
        var changedFields = MeaningfulWeatherChanges(baseline, snapshot);
        // A sensor that appears after the initial baseline is learned silently
        // and can participate in later comparisons. Missing sensor values do
        // not erase a known baseline or imply a condition change.
        weatherBaseline = baseline.FillMissingFrom(snapshot);
        if (changedFields.Count == 0)
        {
            return;
        }

        var rainStarted = baseline.RainRateMillimetersPerHour is <= 0
            && snapshot.RainRateMillimetersPerHour is > 0;
        if (!rainStarted
            && lastWeatherChangePublishedAt != default
            && observedAt >= lastWeatherChangePublishedAt
            && observedAt - lastWeatherChangePublishedAt < WeatherChangeMinimumInterval)
        {
            return;
        }

        var details = WeatherEventDetails(snapshot,
            ("ChangedFields", string.Join(", ", changedFields)));
        if (TryAddEventCore(
            historyGeneration,
            observedAt,
            "WEATHER-CHANGED",
            chatEnabled: true,
            details))
        {
            weatherBaseline = baseline.MergeAvailableFrom(snapshot);
            lastWeatherChangePublishedAt = observedAt;
        }
    }

    private bool CanPublishWeatherChangesLocked(
        long historyGeneration,
        long publicationEpoch) =>
        !weatherChangesSharingBlocked
        && publicationEpoch == weatherChangesPublicationEpoch
        && eventDelivery.Current.WeatherChanges
        && IsHistoryGenerationCurrent(historyGeneration);

    private bool ProcessHighWindLocked(
        DirectWeatherSnapshot snapshot,
        long historyGeneration,
        long publicationEpoch,
        DateTimeOffset observedAt)
    {
        if (highWindSharingBlocked
            || publicationEpoch != highWindPublicationEpoch
            || !eventDelivery.Current.HighWindAlerts
            || !IsHistoryGenerationCurrent(historyGeneration))
        {
            return false;
        }

        var speed = snapshot.WindSpeedMetersPerSecond;
        var gust = snapshot.WindGustMetersPerSecond;
        var effectiveWind = MaxAvailable(speed, gust);
        if (!effectiveWind.HasValue)
        {
            return false;
        }
        var threshold = ChatstronomySettings.NormalizeHighWindThreshold(
            eventDelivery.Current.HighWindThresholdMetersPerSecond);
        var hysteresis = Math.Max(1.0, threshold * 0.1);
        var recoveryThreshold = Math.Max(0.0, threshold - hysteresis);
        if ((highWindThresholdReconciliationPending
                || highWindConnectionReconciliationPending)
            && highWindState == DirectHighWindState.High)
        {
            var remainsHigh = effectiveWind.Value > recoveryThreshold;
            var nextRequiredSpeed = speed.HasValue
                ? speed.Value > recoveryThreshold
                : highWindRequiredSpeed;
            var nextRequiredGust = gust.HasValue
                ? gust.Value > recoveryThreshold
                : highWindRequiredGust;
            highWindRequiredSpeed = nextRequiredSpeed;
            highWindRequiredGust = nextRequiredGust;
            if (!remainsHigh
                && (nextRequiredSpeed && !speed.HasValue
                    || nextRequiredGust && !gust.HasValue))
            {
                return false;
            }
            var refreshedDetails = WindEventDetails(snapshot, threshold, remainsHigh);
            if (TryAddEventCore(
                historyGeneration,
                observedAt,
                "WEATHER-HIGH-WIND",
                chatEnabled: true,
                refreshedDetails))
            {
                if (remainsHigh)
                {
                    // Any current source above the recovery boundary proves
                    // the alert is still latched, even if the source which
                    // originally raised it is temporarily unavailable. Keep
                    // that missing source unresolved until it is observed
                    // safe; the alternate source confirms high but cannot
                    // prove the original source recovered.
                    highWindRequiredSpeed = nextRequiredSpeed;
                    highWindRequiredGust = nextRequiredGust;
                    ClearHighWindReconciliationLocked();
                }
                else
                {
                    ResetHighWindStateLocked(DirectHighWindState.BelowThreshold);
                }
                return true;
            }
            return false;
        }
        if (highWindState != DirectHighWindState.High)
        {
            if (effectiveWind.Value < threshold)
            {
                highWindState = DirectHighWindState.BelowThreshold;
                ClearHighWindReconciliationLocked();
                return false;
            }
            var details = WindEventDetails(snapshot, threshold, isHighWind: true);
            if (TryAddEventCore(
                historyGeneration,
                observedAt,
                "WEATHER-HIGH-WIND",
                chatEnabled: true,
                details))
            {
                highWindState = DirectHighWindState.High;
                highWindRequiredSpeed = speed is { } currentSpeed
                    && currentSpeed > recoveryThreshold;
                highWindRequiredGust = gust is { } currentGust
                    && currentGust > recoveryThreshold;
                ClearHighWindReconciliationLocked();
                return true;
            }
            return false;
        }

        // Once an alert is active, a missing sensor cannot prove recovery.
        // A present safe reading does discharge that source, so alternating
        // wind-speed/gust availability cannot latch the alert forever.
        if (speed.HasValue)
        {
            highWindRequiredSpeed = speed.Value > recoveryThreshold;
        }
        if (gust.HasValue)
        {
            highWindRequiredGust = gust.Value > recoveryThreshold;
        }
        if (highWindRequiredSpeed && !speed.HasValue
            || highWindRequiredGust && !gust.HasValue)
        {
            return false;
        }
        if (effectiveWind.Value > recoveryThreshold)
        {
            return false;
        }

        var recoveryDetails = WindEventDetails(snapshot, threshold, isHighWind: false);
        if (TryAddEventCore(
            historyGeneration,
            observedAt,
            "WEATHER-HIGH-WIND",
            chatEnabled: true,
            recoveryDetails))
        {
            ResetHighWindStateLocked(DirectHighWindState.BelowThreshold);
            return true;
        }
        return false;
    }

    private static IReadOnlyList<string> MeaningfulWeatherChanges(
        DirectWeatherSnapshot baseline,
        DirectWeatherSnapshot current)
    {
        var changed = new List<string>();
        AddThresholdChange(changed, "temperature", baseline.TemperatureCelsius,
            current.TemperatureCelsius, 1.0);
        AddThresholdChange(changed, "dew point", baseline.DewPointCelsius,
            current.DewPointCelsius, 1.0);
        AddThresholdChange(changed, "humidity", baseline.HumidityPercent,
            current.HumidityPercent, 5.0);
        AddThresholdChange(changed, "pressure", baseline.PressureHectopascals,
            current.PressureHectopascals, 2.0);
        AddThresholdChange(changed, "cloud cover", baseline.CloudCoverPercent,
            current.CloudCoverPercent, 10.0);
        AddThresholdChange(changed, "wind speed", baseline.WindSpeedMetersPerSecond,
            current.WindSpeedMetersPerSecond, 2.0);
        AddThresholdChange(changed, "wind gust", baseline.WindGustMetersPerSecond,
            current.WindGustMetersPerSecond, 3.0);
        AddThresholdChange(changed, "sky temperature", baseline.SkyTemperatureCelsius,
            current.SkyTemperatureCelsius, 2.0);
        AddThresholdChange(changed, "sky quality",
            baseline.SkyQualityMagnitudesPerSquareArcsecond,
            current.SkyQualityMagnitudesPerSquareArcsecond, 0.25);
        AddRelativeThresholdChange(changed, "sky brightness",
            baseline.SkyBrightnessLux,
            current.SkyBrightnessLux,
            minimumAbsoluteChange: 0.0001,
            relativeChange: 0.20);
        AddThresholdChange(changed, "star FWHM",
            baseline.StarFwhmArcseconds,
            current.StarFwhmArcseconds, 0.5);

        if (baseline.RainRateMillimetersPerHour is { } oldRain
            && current.RainRateMillimetersPerHour is { } newRain
            && ((oldRain <= 0) != (newRain <= 0) || Math.Abs(newRain - oldRain) >= 0.5))
        {
            changed.Add("rain rate");
        }

        var effectiveWind = MaxAvailable(
            current.WindSpeedMetersPerSecond,
            current.WindGustMetersPerSecond);
        if (effectiveWind is >= 2.0
            && baseline.WindDirectionDegrees is { } oldDirection
            && current.WindDirectionDegrees is { } newDirection)
        {
            var difference = Math.Abs(newDirection - oldDirection) % 360.0;
            if (Math.Min(difference, 360.0 - difference) >= 30.0)
            {
                changed.Add("wind direction");
            }
        }
        return changed;
    }

    private static void AddThresholdChange(
        ICollection<string> changed,
        string label,
        double? previous,
        double? current,
        double threshold)
    {
        if (previous.HasValue
            && current.HasValue
            && Math.Abs(current.Value - previous.Value) >= threshold)
        {
            changed.Add(label);
        }
    }

    private static void AddRelativeThresholdChange(
        ICollection<string> changed,
        string label,
        double? previous,
        double? current,
        double minimumAbsoluteChange,
        double relativeChange)
    {
        if (previous.HasValue
            && current.HasValue
            && Math.Abs(current.Value - previous.Value)
                >= Math.Max(minimumAbsoluteChange, Math.Abs(previous.Value) * relativeChange))
        {
            changed.Add(label);
        }
    }

    private static double? MaxAvailable(double? first, double? second) =>
        first.HasValue && second.HasValue
            ? Math.Max(first.Value, second.Value)
            : first ?? second;

    private static (string Name, object? Value)[] WeatherEventDetails(
        DirectWeatherSnapshot snapshot,
        params (string Name, object? Value)[] additional)
    {
        var details = new List<(string Name, object? Value)>
        {
            ("TemperatureCelsius", snapshot.TemperatureCelsius),
            ("DewPointCelsius", snapshot.DewPointCelsius),
            ("HumidityPercent", snapshot.HumidityPercent),
            ("PressureHectopascals", snapshot.PressureHectopascals),
            ("CloudCoverPercent", snapshot.CloudCoverPercent),
            ("RainRateMillimetersPerHour", snapshot.RainRateMillimetersPerHour),
            ("WindSpeedMetersPerSecond", snapshot.WindSpeedMetersPerSecond),
            ("WindGustMetersPerSecond", snapshot.WindGustMetersPerSecond),
            ("WindDirectionDegrees", snapshot.WindDirectionDegrees),
            ("SkyTemperatureCelsius", snapshot.SkyTemperatureCelsius),
            ("SkyBrightnessLux", snapshot.SkyBrightnessLux),
            ("SkyQualityMagnitudesPerSquareArcsecond",
                snapshot.SkyQualityMagnitudesPerSquareArcsecond),
            ("StarFwhmArcseconds", snapshot.StarFwhmArcseconds),
        };
        details.AddRange(additional);
        return details.ToArray();
    }

    private static (string Name, object? Value)[] WindEventDetails(
        DirectWeatherSnapshot snapshot,
        double threshold,
        bool isHighWind) =>
    [
        ("IsHighWind", isHighWind),
        ("ThresholdMetersPerSecond", threshold),
        ("WindSpeedMetersPerSecond", snapshot.WindSpeedMetersPerSecond),
        ("WindGustMetersPerSecond", snapshot.WindGustMetersPerSecond),
    ];

    private void ResetWeatherState(bool blockSharing)
    {
        lock (weatherStateGate)
        {
            weatherChangesPublicationEpoch++;
            highWindPublicationEpoch++;
            if (blockSharing)
            {
                weatherChangesSharingBlocked = true;
                highWindSharingBlocked = true;
            }
            ClearWeatherObservationsLocked();
        }
    }

    private void ClearWeatherObservationsLocked()
    {
        ClearWeatherChangeBaselineLocked();
        ResetHighWindStateLocked();
    }

    private void ClearWeatherChangeBaselineLocked()
    {
        weatherBaseline = null;
        lastWeatherChangePublishedAt = default;
    }

    private void RemoveWeatherEventsFromHistory(string eventName) =>
        events.RemoveWhere(item =>
            item.TryGetValue("Event", out var value)
            && value is string storedEvent
            && storedEvent.Equals(eventName, StringComparison.Ordinal));

    private void ResetHighWindStateLocked(
        DirectHighWindState state = DirectHighWindState.Unknown)
    {
        highWindState = state;
        highWindRequiredSpeed = false;
        highWindRequiredGust = false;
        ClearHighWindReconciliationLocked();
    }

    private void ClearHighWindReconciliationLocked()
    {
        highWindThresholdReconciliationPending = false;
        highWindConnectionReconciliationPending = false;
    }

    private Task SwitchConnected(object sender, EventArgs args) => AddSimpleEvent("SWITCH-CONNECTED");
    private Task SwitchDisconnected(object sender, EventArgs args) => AddSimpleEvent("SWITCH-DISCONNECTED");
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
        lock (safetyStateGate)
        {
            if (!IsHistoryGenerationCurrent(historyGeneration))
            {
                return;
            }
            var previous = safetyState;
            if (previous == next)
            {
                return;
            }
            safetyState = next;
            safetyStateRevision++;

            if (!CanPublishSafetyLocked(historyGeneration))
            {
                return;
            }

            // Keep the state commit and its history publication in one
            // serialization order. Otherwise a baseline could validate an
            // older read, then append after this newer transition.
            // An unused safety monitor reports disconnected when the
            // consumer is first registered; keep that baseline quiet.
            if (previous == DirectSafetyState.Unknown
                && next == DirectSafetyState.Disconnected)
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
    }

    private void PublishCurrentSafetyBaseline(long historyGeneration)
    {
        long observedRevision;
        long observedPublicationEpoch;
        DirectSafetyState fallback;
        lock (safetyStateGate)
        {
            if (!CanPublishSafetyLocked(historyGeneration))
            {
                return;
            }
            observedRevision = safetyStateRevision;
            observedPublicationEpoch = safetyPublicationEpoch;
            fallback = safetyState;
        }

        DirectSafetyState observed;
        try
        {
            var info = safetyMonitor.GetInfo();
            observed = !info.Connected
                ? DirectSafetyState.Disconnected
                : info.IsSafe
                    ? DirectSafetyState.Safe
                    : DirectSafetyState.Unsafe;
        }
        catch (Exception)
        {
            observed = fallback;
        }

        lock (safetyStateGate)
        {
            // A lifecycle, consent, or newer device transition invalidates
            // this baseline entirely. The transition already committed and
            // published its authoritative state under this same gate.
            if (safetyPublicationEpoch != observedPublicationEpoch
                || safetyStateRevision != observedRevision
                || !CanPublishSafetyLocked(historyGeneration))
            {
                return;
            }

            var current = observed;
            if (safetyState != current)
            {
                safetyState = current;
                safetyStateRevision++;
            }
            if (current == DirectSafetyState.Unknown)
            {
                return;
            }

            // Publication is ordered under the same gate as transition
            // publication, so whichever state wins the gate is also last in
            // the event stream.
            if (current == DirectSafetyState.Disconnected)
            {
                AddEvent(historyGeneration, "SAFETY-DISCONNECTED");
                return;
            }
            AddEvent(historyGeneration, "SAFETY-CONNECTED");
            AddEvent(
                historyGeneration,
                "SAFETY-CHANGED",
                ("IsSafe", current == DirectSafetyState.Safe));
        }
    }

    private bool CanPublishSafetyLocked(long historyGeneration) =>
        !safetySharingBlocked
        && eventDelivery.Current.Safety
        && IsHistoryGenerationCurrent(historyGeneration);

    private Task SequenceStarting(object sender, EventArgs args)
    {
        return HandleSequenceStarting(
            sender,
            args,
            Volatile.Read(ref sequenceLifecycleVersion),
            CaptureHistoryGeneration());
    }

    private Task HandleSequenceStarting(
        object sender,
        EventArgs args,
        long lifecycleVersion,
        long historyGeneration)
    {
        var root = NinaDirectSequenceSnapshot.TryGetSequenceRootFromOwner(sender);
        lock (sequenceGate)
        {
            if (!started
                || sequenceSubscriptionsPaused
                || lifecycleVersion != Volatile.Read(ref sequenceLifecycleVersion)
                || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return Task.CompletedTask;
            }
            sequenceRunning = true;
            if (root is not null)
            {
                BindSequenceFailureRoot(root);
            }
            lock (sequenceFailureGate)
            {
                sequenceHadFailure = false;
                sequenceOutcomeProvenanceComplete = !sequenceFailureCoverageBlocked
                    && NinaDirectSequenceSnapshot.HasCompleteSequenceFailureCoverage(
                        eventDelivery.Current);
                lastSequenceFailureKey = null;
                lastSequenceFailureAt = default;
            }
        }
        AddEvent(historyGeneration, "SEQUENCE-STARTING");
        return Task.CompletedTask;
    }

    private Task SequenceFinished(object sender, EventArgs args)
    {
        return HandleSequenceFinished(
            sender,
            args,
            Volatile.Read(ref sequenceLifecycleVersion),
            CaptureHistoryGeneration());
    }

    private Task HandleSequenceFinished(
        object sender,
        EventArgs args,
        long lifecycleVersion,
        long historyGeneration)
    {
        var root = NinaDirectSequenceSnapshot.TryGetSequenceRootFromOwner(sender);
        bool hadFailures;
        bool provenanceComplete;
        string observedStatus;
        lock (sequenceGate)
        {
            if (!started
                || sequenceSubscriptionsPaused
                || lifecycleVersion != Volatile.Read(ref sequenceLifecycleVersion)
                || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return Task.CompletedTask;
            }
            root ??= sequenceFailureRoot;
            observedStatus = root?.Status.ToString() ?? "UNKNOWN";
            sequenceRunning = false;
            lock (sequenceFailureGate)
            {
                hadFailures = sequenceHadFailure;
                provenanceComplete = sequenceOutcomeProvenanceComplete;
                sequenceHadFailure = false;
                sequenceOutcomeProvenanceComplete = false;
            }
        }
        var status = provenanceComplete ? observedStatus : "UNKNOWN";
        var outcome = !provenanceComplete
            ? "incomplete_provenance"
            : status switch
        {
            "FINISHED" when hadFailures => "completed_with_failures",
            "FINISHED" => "completed",
            "FAILED" => "failed",
            "SKIPPED" => "stopped",
            "CREATED" => "cancelled_or_not_started",
            _ => "ended",
        };
        AddEvent(
            historyGeneration,
            "SEQUENCE-FINISHED",
            ("Outcome", outcome),
            ("Status", status),
            ("HadFailures", provenanceComplete && hadFailures));
        return Task.CompletedTask;
    }

    private Task SequenceEntityFailed(
        SequenceEntityFailureEventArgs args,
        long historyGeneration,
        ISequenceRootContainer subscribedRoot,
        long subscriptionVersion)
    {
        lock (sequenceGate)
        {
            if (!started
                || sequenceSubscriptionsPaused
                || subscriptionVersion != Volatile.Read(
                    ref sequenceFailureSubscriptionVersion)
                || !ReferenceEquals(sequenceFailureRoot, subscribedRoot)
                || sequenceFailureGeneration != historyGeneration
                || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return Task.CompletedTask;
            }
        }

        var delivery = eventDelivery.Current;
        var deliveryScopeMask = NinaDirectSequenceSnapshot
            .GetSequenceFailureDeliveryScopeMask(args.Entity);
        if (!NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(
            deliveryScopeMask,
            delivery))
        {
            // Category switches are transmission boundaries. Do not retain
            // names, errors, or deduplication fingerprints for a failure the
            // active policy does not permit.
            return Task.CompletedTask;
        }

        var entityType = args.Entity?.GetType().Name ?? "Unknown";
        var entity = SanitizeEventText(args.Entity?.Name ?? entityType, 256);
        var error = SanitizeEventText(args.Exception?.Message ?? "Sequence entity failed.", 1_024);
        var entityIdentity = args.Entity is null
            ? 0
            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(args.Entity);
        var key = $"{historyGeneration}|{entityIdentity}|{entityType}|{error}";
        var now = DateTimeOffset.UtcNow;
        lock (sequenceFailureGate)
        {
            if (string.Equals(lastSequenceFailureKey, key, StringComparison.Ordinal)
                && now - lastSequenceFailureAt < TimeSpan.FromSeconds(2))
            {
                return Task.CompletedTask;
            }
            lastSequenceFailureKey = key;
            lastSequenceFailureAt = now;
        }

        if (!TryAddEventCore(
            historyGeneration,
            DateTime.Now,
            "SEQUENCE-ENTITY-FAILED",
            chatEnabled: true,
            ("Entity", entity),
            ("EntityType", entityType),
            ("Error", error),
            (SequenceFailureDeliveryScopesField, deliveryScopeMask)))
        {
            return Task.CompletedTask;
        }
        lock (sequenceGate)
        {
            if (!started
                || sequenceSubscriptionsPaused
                || subscriptionVersion != Volatile.Read(
                    ref sequenceFailureSubscriptionVersion)
                || !ReferenceEquals(sequenceFailureRoot, subscribedRoot)
                || sequenceFailureGeneration != historyGeneration
                || !IsHistoryGenerationCurrent(historyGeneration))
            {
                return Task.CompletedTask;
            }
            lock (sequenceFailureGate)
            {
                sequenceHadFailure = true;
            }
        }
        return Task.CompletedTask;
    }

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

internal enum DirectHighWindState
{
    Unknown,
    BelowThreshold,
    High,
}

internal sealed record DirectWeatherSnapshot(
    bool Connected,
    double? TemperatureCelsius,
    double? DewPointCelsius,
    double? HumidityPercent,
    double? PressureHectopascals,
    double? CloudCoverPercent,
    double? RainRateMillimetersPerHour,
    double? WindSpeedMetersPerSecond,
    double? WindGustMetersPerSecond,
    double? WindDirectionDegrees,
    double? SkyTemperatureCelsius,
    double? SkyBrightnessLux,
    double? SkyQualityMagnitudesPerSquareArcsecond,
    double? StarFwhmArcseconds)
{
    internal bool HasAnyReading =>
        TemperatureCelsius.HasValue
        || DewPointCelsius.HasValue
        || HumidityPercent.HasValue
        || PressureHectopascals.HasValue
        || CloudCoverPercent.HasValue
        || RainRateMillimetersPerHour.HasValue
        || WindSpeedMetersPerSecond.HasValue
        || WindGustMetersPerSecond.HasValue
        || WindDirectionDegrees.HasValue
        || SkyTemperatureCelsius.HasValue
        || SkyBrightnessLux.HasValue
        || SkyQualityMagnitudesPerSquareArcsecond.HasValue
        || StarFwhmArcseconds.HasValue;

    internal static DirectWeatherSnapshot FromNina(WeatherDataInfo info) => new(
        Connected: info.Connected,
        TemperatureCelsius: Finite(info.Temperature),
        DewPointCelsius: Finite(info.DewPoint),
        HumidityPercent: InRange(info.Humidity, 0, 100),
        PressureHectopascals: InRange(info.Pressure, double.Epsilon, 2_000),
        CloudCoverPercent: InRange(info.CloudCover, 0, 100),
        RainRateMillimetersPerHour: NonNegative(info.RainRate),
        WindSpeedMetersPerSecond: NonNegative(info.WindSpeed),
        WindGustMetersPerSecond: NonNegative(info.WindGust),
        WindDirectionDegrees: InRange(info.WindDirection, 0, 360),
        SkyTemperatureCelsius: Finite(info.SkyTemperature),
        SkyBrightnessLux: NonNegative(info.SkyBrightness),
        SkyQualityMagnitudesPerSquareArcsecond: Finite(info.SkyQuality),
        StarFwhmArcseconds: NonNegative(info.StarFWHM));

    internal DirectWeatherSnapshot FillMissingFrom(DirectWeatherSnapshot current) =>
        this with
        {
            TemperatureCelsius = TemperatureCelsius ?? current.TemperatureCelsius,
            DewPointCelsius = DewPointCelsius ?? current.DewPointCelsius,
            HumidityPercent = HumidityPercent ?? current.HumidityPercent,
            PressureHectopascals = PressureHectopascals ?? current.PressureHectopascals,
            CloudCoverPercent = CloudCoverPercent ?? current.CloudCoverPercent,
            RainRateMillimetersPerHour = RainRateMillimetersPerHour
                ?? current.RainRateMillimetersPerHour,
            WindSpeedMetersPerSecond = WindSpeedMetersPerSecond
                ?? current.WindSpeedMetersPerSecond,
            WindGustMetersPerSecond = WindGustMetersPerSecond
                ?? current.WindGustMetersPerSecond,
            WindDirectionDegrees = WindDirectionDegrees ?? current.WindDirectionDegrees,
            SkyTemperatureCelsius = SkyTemperatureCelsius ?? current.SkyTemperatureCelsius,
            SkyBrightnessLux = SkyBrightnessLux ?? current.SkyBrightnessLux,
            SkyQualityMagnitudesPerSquareArcsecond =
                SkyQualityMagnitudesPerSquareArcsecond
                ?? current.SkyQualityMagnitudesPerSquareArcsecond,
            StarFwhmArcseconds = StarFwhmArcseconds ?? current.StarFwhmArcseconds,
        };

    internal DirectWeatherSnapshot MergeAvailableFrom(DirectWeatherSnapshot current) =>
        this with
        {
            Connected = current.Connected,
            TemperatureCelsius = current.TemperatureCelsius ?? TemperatureCelsius,
            DewPointCelsius = current.DewPointCelsius ?? DewPointCelsius,
            HumidityPercent = current.HumidityPercent ?? HumidityPercent,
            PressureHectopascals = current.PressureHectopascals ?? PressureHectopascals,
            CloudCoverPercent = current.CloudCoverPercent ?? CloudCoverPercent,
            RainRateMillimetersPerHour = current.RainRateMillimetersPerHour
                ?? RainRateMillimetersPerHour,
            WindSpeedMetersPerSecond = current.WindSpeedMetersPerSecond
                ?? WindSpeedMetersPerSecond,
            WindGustMetersPerSecond = current.WindGustMetersPerSecond
                ?? WindGustMetersPerSecond,
            WindDirectionDegrees = current.WindDirectionDegrees ?? WindDirectionDegrees,
            SkyTemperatureCelsius = current.SkyTemperatureCelsius ?? SkyTemperatureCelsius,
            SkyBrightnessLux = current.SkyBrightnessLux ?? SkyBrightnessLux,
            SkyQualityMagnitudesPerSquareArcsecond =
                current.SkyQualityMagnitudesPerSquareArcsecond
                ?? SkyQualityMagnitudesPerSquareArcsecond,
            StarFwhmArcseconds = current.StarFwhmArcseconds ?? StarFwhmArcseconds,
        };

    internal DirectWeatherSnapshot MergeWindFrom(DirectWeatherSnapshot current) =>
        this with
        {
            Connected = current.Connected,
            WindSpeedMetersPerSecond = current.WindSpeedMetersPerSecond
                ?? WindSpeedMetersPerSecond,
            WindGustMetersPerSecond = current.WindGustMetersPerSecond
                ?? WindGustMetersPerSecond,
            WindDirectionDegrees = current.WindDirectionDegrees ?? WindDirectionDegrees,
        };

    private static double? Finite(double value) =>
        double.IsFinite(value) ? value : null;

    private static double? NonNegative(double value) =>
        double.IsFinite(value) && value >= 0 ? value : null;

    private static double? InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum
            ? value
            : null;
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
