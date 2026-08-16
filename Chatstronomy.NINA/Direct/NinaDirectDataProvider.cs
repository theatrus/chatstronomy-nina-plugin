using Chatstronomy.NINA.Protocol;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
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
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Native implementation of the Advanced API-compatible read surface used by
/// Chatstronomy. It reads live device state from N.I.N.A. mediators and keeps
/// only bounded callback history; it does not host an HTTP server.
/// </summary>
internal sealed class NinaDirectDataProvider : INinaDirectDataProvider, IFocuserConsumer
{
    private const int EventHistoryCapacity = 2_000;
    private const int ImageHistoryCapacity = 500;
    private const int GuideHistoryCapacity = 10_000;

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
    private readonly BoundedHistory<Dictionary<string, object?>> events =
        new(EventHistoryCapacity);
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
    private AutoFocusReport? lastAutofocusReport;
    private long guideStepId;
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
        IWindowServiceFactory windowFactory)
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
    }

    public DirectCapabilities Capabilities { get; } = new(
        EventHistory: true,
        ImageHistory: true,
        Thumbnails: true,
        Sequence: true,
        EquipmentSnapshots: true,
        AutofocusDetails: true,
        GuiderGraph: true,
        Commands: true);

    public void Start()
    {
        if (started)
        {
            return;
        }

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
        started = true;
        sequenceSubscriptionStop = new CancellationTokenSource();
        _ = SubscribeToSequenceWhenReadyAsync(sequenceSubscriptionStop.Token);
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
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(events.Snapshot()),
            DirectQueryKind.ImageHistory =>
                DirectApiEnvelope<IReadOnlyList<DirectImageMetadata>>.Ok(
                    images.Snapshot().Select(image => image.Metadata).ToArray()),
            DirectQueryKind.Sequence =>
                DirectApiEnvelope<IReadOnlyList<Dictionary<string, object?>>>.Ok(
                    RunOnUiThread(() => NinaDirectSequenceSnapshot.Build(sequence))),
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
            DirectQueryKind.RotatorInfo => DirectApiEnvelope<object>.Ok(rotator.GetInfo()),
            DirectQueryKind.FocuserInfo => DirectApiEnvelope<object>.Ok(focuser.GetInfo()),
            DirectQueryKind.Command =>
                await ExecuteCommandAsync(
                    query.Command ?? throw new InvalidOperationException(
                        "The Direct command payload is missing."),
                    cancellationToken).ConfigureAwait(false),
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
                return JsonSerializer.SerializeToElement(
                    lastAutofocusReport,
                    DirectProtocol.JsonOptions);
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
                return document.RootElement.Clone();
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
        DirectRigCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = command.Kind switch
        {
            DirectRigCommandKind.UnparkMount => UnparkMount(),
            DirectRigCommandKind.HomeMount => HomeMount(),
            DirectRigCommandKind.ChangeFilter => ChangeFilter(command.FilterId),
            DirectRigCommandKind.StartGuiding => StartGuiding(command.Calibrate),
            DirectRigCommandKind.StopGuiding => StopGuiding(),
            DirectRigCommandKind.CoolCamera => CoolCamera(command.Temperature, command.Minutes),
            DirectRigCommandKind.WarmCamera => WarmCamera(command.Minutes),
            DirectRigCommandKind.StartAutofocus => StartAutofocus(),
            DirectRigCommandKind.CancelAutofocus => CancelAutofocus(),
            DirectRigCommandKind.ParkMount => ParkMount(),
            DirectRigCommandKind.AbortExposure => AbortExposure(),
            DirectRigCommandKind.StopSequence => StopSequence(),
            DirectRigCommandKind.StartSequence => StartSequence(command.SkipValidation),
            _ => throw new NotSupportedException(
                $"Direct command '{command.Kind}' is not implemented."),
        };
        return Task.FromResult<object?>(response);
    }

    private DirectApiEnvelope<string> UnparkMount()
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
        ObserveCommand(
            telescope.UnparkTelescope(CreateProgress(), CancellationToken.None),
            "Unpark mount");
        return DirectApiEnvelope<string>.Ok("Mount unparking started");
    }

    private DirectApiEnvelope<string> HomeMount()
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
            telescope.StopSlew();
        }
        ObserveCommand(
            telescope.FindHome(CreateProgress(), CancellationToken.None),
            "Home mount");
        return DirectApiEnvelope<string>.Ok("Mount homing started");
    }

    private DirectApiEnvelope<string> ParkMount()
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
            telescope.StopSlew();
        }
        ObserveCommand(
            telescope.ParkTelescope(CreateProgress(), CancellationToken.None),
            "Park mount");
        return DirectApiEnvelope<string>.Ok("Mount parking started");
    }

    private DirectApiEnvelope<string> ChangeFilter(int? filterId)
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

        ObserveCommand(
            filterWheel.ChangeFilter(selected, CancellationToken.None, CreateProgress()),
            $"Change filter to {selected.Name}");
        return DirectApiEnvelope<string>.Ok($"Filter change to {selected.Name} started");
    }

    private DirectApiEnvelope<string> StartGuiding(bool? calibrate)
    {
        if (!guider.GetInfo().Connected)
        {
            throw new InvalidOperationException("Guider is not connected.");
        }
        var stop = ReplaceCommandToken(ref guideCommandStop);
        ObserveCommand(
            guider.StartGuiding(calibrate ?? false, CreateProgress(), stop.Token),
            "Start guiding");
        return DirectApiEnvelope<string>.Ok("Guiding start requested");
    }

    private DirectApiEnvelope<string> StopGuiding()
    {
        if (!guider.GetInfo().Connected)
        {
            throw new InvalidOperationException("Guider is not connected.");
        }
        CancelCommand(ref guideCommandStop);
        ObserveCommand(guider.StopGuiding(CancellationToken.None), "Stop guiding");
        return DirectApiEnvelope<string>.Ok("Guiding stop requested");
    }

    private DirectApiEnvelope<string> CoolCamera(double? temperature, double? minutes)
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
        var stop = ReplaceCommandToken(ref cameraCommandStop);
        ObserveCommand(
            camera.CoolCamera(target, duration, CreateProgress(), stop.Token),
            "Cool camera");
        return DirectApiEnvelope<string>.Ok(
            $"Camera cooling to {target:0.##} C over {duration.TotalMinutes:0.##} minutes");
    }

    private DirectApiEnvelope<string> WarmCamera(double? minutes)
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
        var stop = ReplaceCommandToken(ref cameraCommandStop);
        ObserveCommand(
            camera.WarmCamera(duration, CreateProgress(), stop.Token),
            "Warm camera");
        return DirectApiEnvelope<string>.Ok(
            $"Camera warming over {duration.TotalMinutes:0.##} minutes");
    }

    private DirectApiEnvelope<string> StartAutofocus()
    {
        if (!focuser.GetInfo().Connected)
        {
            throw new InvalidOperationException("Focuser is not connected.");
        }

        var stop = ReplaceCommandToken(ref autofocusCommandStop);
        IWindowService? window = null;
        Task<AutoFocusReport>? autofocus = null;
        RunOnUiThread(() =>
        {
            window = windowFactory.Create();
            var viewModel = autoFocusFactory.Create();
            window.Show(
                viewModel,
                "Autofocus",
                ResizeMode.CanResize,
                WindowStyle.ToolWindow);
            autofocus = viewModel.StartAutoFocus(
                filterWheel.GetInfo().SelectedFilter,
                stop.Token,
                CreateProgress());
            return true;
        });

        ObserveAutofocus(
            autofocus ?? throw new InvalidOperationException("Autofocus did not start."),
            window ?? throw new InvalidOperationException("Autofocus window did not open."));
        return DirectApiEnvelope<string>.Ok("Autofocus started");
    }

    private DirectApiEnvelope<string> CancelAutofocus()
    {
        CancelCommand(ref autofocusCommandStop);
        return DirectApiEnvelope<string>.Ok("Autofocus cancellation requested");
    }

    private DirectApiEnvelope<string> AbortExposure()
    {
        if (!camera.GetInfo().Connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
        if (!camera.GetInfo().IsExposing)
        {
            return DirectApiEnvelope<string>.Ok("Camera is not exposing");
        }
        camera.AbortExposure();
        return DirectApiEnvelope<string>.Ok("Exposure aborted");
    }

    private DirectApiEnvelope<string> StopSequence()
    {
        EnsureSequenceReady();
        RunOnUiThread(() =>
        {
            sequence.CancelAdvancedSequence();
            return true;
        });
        return DirectApiEnvelope<string>.Ok("Sequence stopped");
    }

    private DirectApiEnvelope<string> StartSequence(bool? skipValidation)
    {
        EnsureSequenceReady();
        if (sequence.IsAdvancedSequenceRunning())
        {
            throw new InvalidOperationException("Sequence is already running.");
        }
        var task = RunOnUiThread(() => sequence.StartAdvancedSequence(skipValidation ?? false));
        ObserveCommand(task, "Start sequence");
        return DirectApiEnvelope<string>.Ok("Sequence started");
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
                    AddEvent(
                        "CHATSTRONOMY-COMMAND-FAILED",
                        ("Command", "Autofocus"),
                        ("Error", completed.Exception?.GetBaseException().Message ?? "Unknown error"));
                }
                _ = window.Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveCommand(Task task, string commandName)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    AddEvent(
                        "CHATSTRONOMY-COMMAND-FAILED",
                        ("Command", commandName),
                        ("Error", completed.Exception?.GetBaseException().Message ?? "Unknown error"));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        var coordinates = info.Coordinates;
        var coordinateRa = FiniteOrZero(coordinates?.RA ?? info.RightAscension);
        var coordinateDec = FiniteOrZero(coordinates?.Dec ?? info.Declination);
        var coordinateRaDegrees = FiniteOrZero(coordinates?.RADegrees ?? coordinateRa * 15);
        var coordinateEpoch = coordinates?.Epoch.ToString() ?? info.EquatorialSystem.ToString();
        var now = coordinates?.DateTime.Now ?? DateTime.Now;
        var utcNow = coordinates?.DateTime.UtcNow ?? DateTime.UtcNow;
        var emptyObject = new Dictionary<string, object?>();

        return new Dictionary<string, object?>
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
            IsBayered: args.IsBayered);
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
                var scale = frozen.PixelWidth > 256 ? 256d / frozen.PixelWidth : 1d;
                BitmapSource thumbnail = frozen;
                if (scale < 1)
                {
                    var transformed = new TransformedBitmap(
                        frozen,
                        new ScaleTransform(scale, scale));
                    transformed.Freeze();
                    thumbnail = transformed;
                }

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(thumbnail));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                savedImage.ThumbnailData = stream.ToArray();
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

    private void AddEvent(string eventName, params (string Name, object? Value)[] details)
    {
        var item = new Dictionary<string, object?>
        {
            ["Time"] = DateTime.Now,
            ["Event"] = eventName,
        };
        foreach (var (name, value) in details)
        {
            item[name] = value;
        }
        events.Add(item);
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
    bool IsBayered);

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
