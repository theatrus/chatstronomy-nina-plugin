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
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
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
internal sealed class NinaDirectDataProvider : INinaDirectDataProvider, IFocuserConsumer, ISubscriber
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
    private readonly object autofocusReportGate = new();
    private CancellationTokenSource? guideCommandStop;
    private CancellationTokenSource? cameraCommandStop;
    private CancellationTokenSource? autofocusCommandStop;
    private CancellationTokenSource profileSession = new();
    private AutoFocusReport? lastAutofocusReport;
    private long guideStepId;
    private long commandGeneration;
    private bool started;

    internal NinaDirectDataProvider(
        IProfileService profileService,
        ITelescopeMediator telescope,
        ICameraMediator camera,
        IFilterWheelMediator filterWheel,
        IGuiderMediator guider,
        IRotatorMediator rotator,
        IFocuserMediator focuser,
        ISequenceMediator sequence,
        IImageSaveMediator imageSave,
        IApplicationStatusMediator applicationStatus,
        IAutoFocusVMFactory autoFocusFactory,
        IImageHistoryVM imageHistory,
        IWindowServiceFactory windowFactory,
        IMessageBroker messageBroker,
        DirectEventDeliveryPolicy eventDelivery,
        DirectAccessPolicy accessPolicy)
    {
        this.profileService = profileService;
        this.telescope = telescope;
        this.camera = camera;
        this.filterWheel = filterWheel;
        this.guider = guider;
        this.rotator = rotator;
        this.focuser = focuser;
        this.sequence = sequence;
        this.imageSave = imageSave;
        this.applicationStatus = applicationStatus;
        this.autoFocusFactory = autoFocusFactory;
        this.imageHistory = imageHistory;
        this.windowFactory = windowFactory;
        this.messageBroker = messageBroker;
        this.eventDelivery = eventDelivery;
        this.accessPolicy = accessPolicy;
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

        imageSave.ImageSaved += ImageSaved;
        foreach (var topic in TargetSchedulerTopics)
        {
            messageBroker.Subscribe(topic, this);
        }
        ApplyLogDeliveryOptions();
        _ = notificationWatcher.Start();
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
            logWatcher.Start();
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
        Interlocked.Increment(ref commandGeneration);
        CancelOutstandingCommands();
    }

    public void RevokeProfileAccess()
    {
        // Publish the replacement before invalidating the old token: a new
        // profile can start connecting while the old socket is being aborted.
        // Cancellation callbacks only abort a socket or close a named pipe;
        // none waits on a plugin lifecycle lock or blocks the N.I.N.A. UI.
        var previous = Interlocked.Exchange(
            ref profileSession,
            new CancellationTokenSource());
        Interlocked.Increment(ref commandGeneration);
        previous.Cancel();
        CancelOutstandingCommands();
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

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

        foreach (var topic in TargetSchedulerTopics)
        {
            messageBroker.Unsubscribe(topic, this);
        }
        logWatcher.Stop();
        notificationWatcher.Stop();

        started = false;
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
        imageSave.ImageSaved -= ImageSaved;
        CancelOutstandingCommands();
    }

    public void Reset()
    {
        events.Clear();
        logEvents.Clear();
        images.Clear();
        guideSteps.Clear();
        lock (autofocusReportGate)
        {
            lastAutofocusReport = null;
        }
        Interlocked.Exchange(ref guideStepId, 0);
    }

    public async Task<object?> ExecuteAsync(
        DirectQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? result = query.Kind switch
        {
            DirectQueryKind.EventHistory =>
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(
                    SnapshotEventHistory()),
            DirectQueryKind.ImageHistory =>
                DirectApiEnvelope<IReadOnlyList<DirectImageMetadata>>.Ok(
                    images.Snapshot().Select(image => image.Metadata).ToArray()),
            DirectQueryKind.Sequence =>
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(
                    RunOnUiThread(() => NinaDirectSequenceSnapshot.Build(
                        sequence,
                        eventDelivery.Current,
                        accessPolicy.Current))),
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

    public void Dispose() => Stop();

    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
    }

    public void UpdateEndAutoFocusRun(AutoFocusInfo info) => AddEvent("AUTOFOCUS-FINISHED");

    public void UpdateUserFocused(FocuserInfo info) => AddEvent("FOCUSER-USER-FOCUSED");

    public void AutoFocusRunStarting() => AddEvent("AUTOFOCUS-STARTING");

    public void NewAutoFocusPoint(DataPoint dataPoint) => AddEvent(
        "AUTOFOCUS-POINT-ADDED",
        ("Position", FiniteInt(dataPoint.X)),
        ("HFR", FiniteOrZero(dataPoint.Y)));

    private async Task<JsonElement> GetLastAutofocusAsync(CancellationToken cancellationToken)
    {
        lock (autofocusReportGate)
        {
            if (lastAutofocusReport is not null)
            {
                return DirectPrivacyProjection.Redact(
                    JsonSerializer.SerializeToElement(
                        lastAutofocusReport,
                        DirectProtocol.JsonOptions),
                    accessPolicy.Current);
            }
        }

        var directory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("No completed autofocus report is available.");
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var newest = Directory.EnumerateFiles(directory)
                    .OrderBy(File.GetCreationTimeUtc)
                    .LastOrDefault()
                    ?? throw new InvalidOperationException(
                        "No completed autofocus report is available.");
                var json = await File.ReadAllTextAsync(newest, cancellationToken)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                return DirectPrivacyProjection.Redact(
                    document.RootElement,
                    accessPolicy.Current);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                lastError = exception;
                if (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            "The latest autofocus report could not be read.",
            lastError);
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
            DirectRigCommandKind.UnparkMount => UnparkMount(authorize),
            DirectRigCommandKind.HomeMount => HomeMount(authorize),
            DirectRigCommandKind.ChangeFilter => ChangeFilter(command.FilterId, authorize),
            DirectRigCommandKind.StartGuiding => StartGuiding(command.Calibrate, authorize),
            DirectRigCommandKind.StopGuiding => StopGuiding(authorize),
            DirectRigCommandKind.CoolCamera =>
                CoolCamera(command.Temperature, command.Minutes, authorize),
            DirectRigCommandKind.WarmCamera => WarmCamera(command.Minutes, authorize),
            DirectRigCommandKind.StartAutofocus =>
                StartAutofocus(query, cancellationToken, generation, authorize),
            DirectRigCommandKind.CancelAutofocus => CancelAutofocus(authorize),
            DirectRigCommandKind.ParkMount => ParkMount(authorize),
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

    private DirectApiEnvelope<string> UnparkMount(Action authorize)
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
            "Unpark mount");
        return DirectApiEnvelope<string>.Accepted("Mount unparking requested");
    }

    private DirectApiEnvelope<string> HomeMount(Action authorize)
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
            "Home mount");
        return DirectApiEnvelope<string>.Accepted("Mount homing requested");
    }

    private DirectApiEnvelope<string> ParkMount(Action authorize)
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
            "Park mount");
        return DirectApiEnvelope<string>.Accepted("Mount parking requested");
    }

    private DirectApiEnvelope<string> ChangeFilter(int? filterId, Action authorize)
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
            $"Change filter to {selected.Name}");
        return DirectApiEnvelope<string>.Accepted($"Filter change to {selected.Name} requested");
    }

    private DirectApiEnvelope<string> StartGuiding(bool? calibrate, Action authorize)
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
            "Start guiding");
        return DirectApiEnvelope<string>.Accepted("Guiding start requested");
    }

    private DirectApiEnvelope<string> StopGuiding(Action authorize)
    {
        if (!guider.GetInfo().Connected)
        {
            throw new InvalidOperationException("Guider is not connected.");
        }
        authorize();
        CancelCommand(ref guideCommandStop);
        authorize();
        ObserveCommand(guider.StopGuiding(CancellationToken.None), "Stop guiding");
        return DirectApiEnvelope<string>.Accepted("Guiding stop requested");
    }

    private DirectApiEnvelope<string> CoolCamera(
        double? temperature,
        double? minutes,
        Action authorize)
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
            "Cool camera");
        return DirectApiEnvelope<string>.Accepted(
            $"Camera cooling to {target:0.##} C over {duration.TotalMinutes:0.##} minutes requested");
    }

    private DirectApiEnvelope<string> WarmCamera(double? minutes, Action authorize)
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
            "Warm camera");
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
            authorize();
            autofocus = viewModel.StartAutoFocus(
                selectedFilter,
                stop.Token,
                CreateProgress());
            return true;
        });

        ObserveAutofocus(
            autofocus ?? throw new InvalidOperationException("Autofocus did not start."),
            window ?? throw new InvalidOperationException("Autofocus window did not open."));
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
        ObserveCommand(task, "Start sequence");
        return DirectApiEnvelope<string>.Accepted("Sequence start requested");
    }

    private void EnsureSequenceReady()
    {
        if (!sequence.Initialized)
        {
            throw new InvalidOperationException("Sequence is not initialized.");
        }
    }

    private void ObserveAutofocus(Task<AutoFocusReport> task, IWindowService window)
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
                    lock (autofocusReportGate)
                    {
                        lastAutofocusReport = completed.Result;
                    }
                    imageHistory.AppendAutoFocusPoint(completed.Result);
                    window.DelayedClose(TimeSpan.FromSeconds(10));
                    return;
                }

                if (completed.IsFaulted)
                {
                    AddCommandFailure(
                        "Autofocus",
                        completed.Exception?.GetBaseException().Message ?? "Unknown error");
                }
                else if (completed.IsCanceled)
                {
                    AddCommandFailure("Autofocus", "Command canceled before completion");
                }
                _ = window.Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveCommand(Task task, string commandName)
    {
        if (task.IsFaulted || task.IsCanceled)
        {
            task.GetAwaiter().GetResult();
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    AddCommandFailure(
                        commandName,
                        completed.Exception?.GetBaseException().Message ?? "Unknown error");
                }
                else if (completed.IsCanceled)
                {
                    AddCommandFailure(commandName, "Command canceled before completion");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        if (!index.HasValue || index.Value > int.MaxValue
            || !images.TryGetAt((int)index.Value, out var savedImage)
            || savedImage is null)
        {
            throw new InvalidOperationException("The requested image is no longer in history.");
        }

        var data = savedImage.ThumbnailData;
        if (data is null)
        {
            throw new InvalidOperationException("The image thumbnail is still being prepared.");
        }
        return new DirectThumbnail(data, "image/jpeg", 200);
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
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
        images.Add(savedImage);
        AddEvent("IMAGE-SAVE");
        QueueThumbnail(args.Image, savedImage);
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
        var id = Interlocked.Increment(ref guideStepId);
        guideSteps.Add(new DirectGuideStep(
            Id: id,
            IdOffsetLeft: id - 0.15,
            IdOffsetRight: id + 0.15,
            RADistanceRaw: FiniteOrZero(step.RADistanceRaw),
            RADistanceRawDisplay: FiniteOrZero(step.RADistanceRaw),
            RADuration: FiniteOrZero(step.RADuration),
            DECDistanceRaw: FiniteOrZero(step.DECDistanceRaw),
            DECDistanceRawDisplay: FiniteOrZero(step.DECDistanceRaw),
            DECDuration: FiniteOrZero(step.DECDuration),
            Dither: "NO"));
    }

    public Task OnMessageReceived(IMessage message)
    {
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
                message.SentAt,
                eventName,
                ("WaitEndTime", message.Content));
            return Task.CompletedTask;
        }

        message.CustomHeaders.TryGetValue("ProjectName", out var projectName);
        message.CustomHeaders.TryGetValue("Coordinates", out var coordinates);
        message.CustomHeaders.TryGetValue("Rotation", out var rotation);
        AddEventAt(
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

    private void RecordLog(NinaLogRecord record)
    {
        if (!eventDelivery.Current.ShouldSendLogLevel(record.Level))
        {
            return;
        }
        logEvents.Add(BuildEvent(
            record.Time,
            "NINA-LOG",
            chatEnabled: true,
            ("Level", record.Level),
            ("Source", record.Source),
            ("Member", record.Member),
            ("Line", record.Line),
            ("Message", record.Message)));
    }

    /// Equipment events and forwarded log lines, merged in time order.
    ///
    /// They are buffered separately so log volume cannot evict equipment
    /// history, but the consumer still expects one chronological list.
    private IReadOnlyList<Dictionary<string, object?>> SnapshotEventHistory()
    {
        var access = accessPolicy.Current;
        var delivery = eventDelivery.Current;
        // Apply privacy choices on every poll, not just when an event first
        // entered its ring. Clone recursively so turning location or a log
        // level off takes effect immediately without destroying history the
        // user may explicitly choose to share again later.
        var equipment = events.Snapshot()
            .Select(item => DirectPrivacyProjection.RedactedCopy(item, access))
            .ToArray();
        var logs = logEvents.Snapshot()
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

    private void RecordNotification(NinaNotificationRecord notification)
    {
        if (!eventDelivery.Current.NinaNotifications)
        {
            return;
        }
        AddEventCore(
            notification.Time,
            "NINA-NOTIFICATION",
            chatEnabled: true,
            ("Level", notification.Level),
            ("Header", notification.Header),
            ("Message", notification.Message));
    }

    private void AddEvent(string eventName, params (string Name, object? Value)[] details) =>
        AddEventCore(
            DateTime.Now,
            eventName,
            eventDelivery.Current.ShouldSendEvent(eventName),
            details);

    private void AddEventAt(
        DateTimeOffset time,
        string eventName,
        params (string Name, object? Value)[] details) =>
        AddEventCore(
            time,
            eventName,
            eventDelivery.Current.ShouldSendEvent(eventName),
            details);

    private void AddEventCore(
        object time,
        string eventName,
        bool chatEnabled,
        params (string Name, object? Value)[] details) =>
        events.Add(BuildEvent(time, eventName, chatEnabled, details));

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
        var id = Interlocked.Increment(ref guideStepId);
        guideSteps.Add(new DirectGuideStep(
            Id: id,
            IdOffsetLeft: id - 0.15,
            IdOffsetRight: id + 0.15,
            RADistanceRaw: 0,
            RADistanceRawDisplay: 0,
            RADuration: 0,
            DECDistanceRaw: 0,
            DECDistanceRawDisplay: 0,
            DECDuration: 0,
            Dither: "0.01"));
        return AddSimpleEvent("GUIDER-DITHER");
    }
    private Task GuiderStarted(object sender, EventArgs args) => AddSimpleEvent("GUIDER-START");
    private Task GuiderStopped(object sender, EventArgs args) => AddSimpleEvent("GUIDER-STOP");
    private Task RotatorConnected(object sender, EventArgs args) => AddSimpleEvent("ROTATOR-CONNECTED");
    private Task RotatorDisconnected(object sender, EventArgs args) => AddSimpleEvent("ROTATOR-DISCONNECTED");
    private Task FocuserConnected(object sender, EventArgs args) => AddSimpleEvent("FOCUSER-CONNECTED");
    private Task FocuserDisconnected(object sender, EventArgs args) => AddSimpleEvent("FOCUSER-DISCONNECTED");
    private Task SequenceStarting(object sender, EventArgs args) => AddSimpleEvent("SEQUENCE-STARTING");
    private Task SequenceFinished(object sender, EventArgs args) => AddSimpleEvent("SEQUENCE-FINISHED");

    private Task FilterWheelChanged(object sender, FilterChangedEventArgs args)
    {
        AddEvent(
            "FILTERWHEEL-CHANGED",
            ("Previous", new DirectFilterInfo(args.From?.Name ?? string.Empty, args.From?.Position ?? -1)),
            ("New", new DirectFilterInfo(args.To?.Name ?? string.Empty, args.To?.Position ?? -1)));
        return Task.CompletedTask;
    }

    private Task RotatorMoved(object sender, RotatorEventArgs args)
    {
        AddEvent("ROTATOR-MOVED", ("From", args.From), ("To", args.To));
        return Task.CompletedTask;
    }

    private Task RotatorMovedMechanical(object sender, RotatorEventArgs args)
    {
        AddEvent("ROTATOR-MOVED-MECHANICAL", ("From", args.From), ("To", args.To));
        return Task.CompletedTask;
    }

    private void RotatorSynced(object? sender, RotatorEventArgs args) => AddEvent("ROTATOR-SYNCED");

    private Task AddSimpleEvent(string eventName)
    {
        AddEvent(eventName);
        return Task.CompletedTask;
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static int FiniteInt(double value) =>
        double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)
            : 0;
}

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
