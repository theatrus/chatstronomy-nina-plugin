using System.Reflection;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using Chatstronomy.NINA.Settings;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Projects the loaded advanced sequence into the small, stable JSON tree
/// consumed by Chatstronomy. N.I.N.A. does not expose its root container on
/// <see cref="ISequenceMediator"/>, so root discovery is isolated here while
/// the actual projection uses public sequencer interfaces.
/// </summary>
internal static class NinaDirectSequenceSnapshot
{
    [Flags]
    private enum ProjectionScope
    {
        None = 0,
        Images = 1 << 0,
        Autofocus = 1 << 1,
        Guiding = 1 << 2,
        Mount = 1 << 3,
        Sequence = 1 << 4,
        Safety = 1 << 5,
        FilterFocuserRotator = 1 << 6,
        ObservatoryAndFlatPanel = 1 << 7,
        EquipmentConnections = 1 << 8,
        Other = 1 << 9,
        TargetScheduler = 1 << 10,
    }

    private static readonly IReadOnlyDictionary<string, string> DetailNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Temperature"] = "Temperature",
            ["ExposureTime"] = "ExposureTime",
            ["ExposureCount"] = "ExposureCount",
            ["Binning"] = "Binning",
            ["Gain"] = "Gain",
            ["Offset"] = "Offset",
            ["ImageType"] = "Type",
            ["ROI"] = "ROI",
            ["AzimuthDegrees"] = "Azimuth",
            ["Position"] = "Position",
            ["RelativePosition"] = "RelativePosition",
            ["Slope"] = "Slope",
            ["Absolute"] = "Absolute",
            ["Intercept"] = "Intercept",
            ["ForceCalibration"] = "ForceCalibration",
            ["PositionAngle"] = "Rotation",
            ["Text"] = "Text",
            ["Time"] = "Delay",
            ["Iterations"] = "Iterations",
            ["CompletedIterations"] = "CompletedIterations",
        };

    internal static IReadOnlyList<Dictionary<string, object?>> Build(
        ISequenceMediator sequence,
        DirectEventDeliveryOptions? delivery = null,
        DirectAccessOptions? access = null,
        bool? safetyMonitorIsSafe = null)
    {
        if (!sequence.Initialized)
        {
            throw new InvalidOperationException("Sequence is not initialized.");
        }

        var root = GetSequenceRoot(sequence);
        var options = delivery ?? DirectEventDeliveryOptions.Default;
        var result = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["GlobalTriggers"] = root is ITriggerable triggerable
                    ? triggerable.GetTriggersSnapshot()
                        .Select(trigger => BuildTrigger(trigger, options))
                        .ToArray()
                    : Array.Empty<Dictionary<string, object?>>(),
            },
        };
        result.AddRange(root.GetItemsSnapshot()
            .Select(item => BuildItem(item, options, safetyMonitorIsSafe)));
        var privacy = access ?? DirectAccessOptions.Default;
        foreach (var entity in result)
        {
            DirectPrivacyProjection.Redact(entity, privacy);
        }
        return result;
    }

    private static ISequenceContainer GetSequenceRoot(ISequenceMediator sequence)
    {
        var navigationField = sequence.GetType().GetField(
            "sequenceNavigation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var navigation = navigationField?.GetValue(sequence)
            ?? throw new InvalidOperationException(
                "This N.I.N.A. version does not expose the loaded sequence navigation object.");
        var sequence2 = RequiredProperty(navigation, "Sequence2VM");
        var sequencer = RequiredProperty(sequence2, "Sequencer");
        return RequiredProperty(sequencer, "MainContainer") as ISequenceContainer
            ?? throw new InvalidOperationException("The loaded N.I.N.A. sequence has no root container.");
    }

    internal static ISequenceRootContainer? TryGetSequenceRoot(ISequenceMediator sequence)
    {
        try
        {
            return GetSequenceRoot(sequence) as ISequenceRootContainer;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NullReferenceException
                or TargetInvocationException)
        {
            return null;
        }
    }

    internal static ISequenceRootContainer? TryGetSequenceRootFromOwner(object? owner)
    {
        try
        {
            var sequencer = OptionalProperty(owner, "Sequencer");
            return OptionalProperty(sequencer, "MainContainer") as ISequenceRootContainer;
        }
        catch (Exception exception) when (
            exception is NullReferenceException or TargetInvocationException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildItem(
        ISequenceItem item,
        DirectEventDeliveryOptions delivery,
        bool? safetyMonitorIsSafe)
    {
        var itemType = item.GetType().Name;
        var isTargetContainer = IsActualTargetContainer(item);
        if (isTargetContainer
            && item is ISequenceContainer privateTarget
            && !delivery.TargetScheduler)
        {
            // Target containers often carry a user label in Name and the
            // actual object name in Target. Keep neither when target sharing
            // is disabled. Their nested instructions remain independently
            // projectable so, for example, a consented cooling or wait state
            // does not disappear with its private parent.
            return new Dictionary<string, object?>
            {
                ["Name"] = "_Container",
                ["Status"] = "SUPPRESSED",
                ["IsTargetContainer"] = true,
                ["ChatEnabled"] = false,
                ["Items"] = privateTarget.GetItemsSnapshot()
                    .Select(child => BuildItem(child, delivery, safetyMonitorIsSafe))
                    .ToArray(),
            };
        }
        var requiredScopes = RequiredScopes(item);
        if (!ScopesEnabled(requiredScopes, delivery))
        {
            return BuildSuppressedItem(item, delivery, safetyMonitorIsSafe);
        }

        var expandContainer = ShouldExpandContainer(item);
        var result = BaseEntity(item, expandContainer ? "_Container" : string.Empty);

        if (isTargetContainer)
        {
            result["IsTargetContainer"] = true;
            result["ChatEnabled"] = delivery.TargetScheduler;
            var target = OptionalProperty(item, "Target");
            if (OptionalProperty(target, "TargetName") is string targetName
                && !string.IsNullOrWhiteSpace(targetName))
            {
                result["TargetName"] = targetName;
            }
        }

        if (expandContainer && item is ISequenceContainer container)
        {
            result["Items"] = container.GetItemsSnapshot()
                .Select(child => BuildItem(child, delivery, safetyMonitorIsSafe))
                .ToArray();
            result["Conditions"] = container is IConditionable conditionable
                ? conditionable.GetConditionsSnapshot()
                    .Select(condition => BuildCondition(condition, delivery))
                    .ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["Triggers"] = container is ITriggerable triggerable
                ? triggerable.GetTriggersSnapshot()
                    .Select(trigger => BuildTrigger(trigger, delivery))
                    .ToArray()
                : Array.Empty<Dictionary<string, object?>>();
        }

        AddItemDetails(item, result, safetyMonitorIsSafe, delivery);
        AddDeliveryDetails(result, requiredScopes);
        return result;
    }

    private static Dictionary<string, object?> BuildSuppressedItem(
        ISequenceItem item,
        DirectEventDeliveryOptions delivery,
        bool? safetyMonitorIsSafe)
    {
        var result = SuppressedEntity("SequenceItem");
        if (ShouldExpandContainer(item) && item is ISequenceContainer container)
        {
            result["Items"] = container.GetItemsSnapshot()
                .Select(child => BuildItem(child, delivery, safetyMonitorIsSafe))
                .ToArray();
            result["Conditions"] = container is IConditionable conditionable
                ? conditionable.GetConditionsSnapshot()
                    .Select(condition => BuildCondition(condition, delivery))
                    .ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["Triggers"] = container is ITriggerable triggerable
                ? triggerable.GetTriggersSnapshot()
                    .Select(trigger => BuildTrigger(trigger, delivery))
                    .ToArray()
                : Array.Empty<Dictionary<string, object?>>();
        }
        return result;
    }

    private static Dictionary<string, object?> SuppressedEntity(string kind) => new()
    {
        ["Name"] = $"Suppressed_{kind}",
        ["Status"] = "SUPPRESSED",
        ["ChatEnabled"] = false,
        ["Suppressed"] = true,
    };

    private static bool ShouldExpandContainer(ISequenceItem item) =>
        item is ISequenceContainer
        && item.GetType().Name is not "SmartExposure"
            and not "TakeManyExposures"
            and not "SmartSubframeExposure";

    private static bool IsActualTargetContainer(ISequenceItem item)
    {
        if (item is not IDeepSkyObjectContainer)
        {
            return false;
        }

        // IDeepSkyObjectContainer is also used by execution-context and
        // third-party proxy containers. Only N.I.N.A.'s concrete observing
        // target types are targets; unknown implementations remain ordinary
        // containers and are governed by their own event scope.
        return item.GetType().FullName is
            "NINA.Sequencer.Container.DeepSkyObjectContainer"
            or "NINA.ViewModel.Sequencer.SimpleSequence.SimpleDSOContainer";
    }

    private static ProjectionScope RequiredScopes(ISequenceEntity entity)
    {
        if (entity is ISequenceItem item && IsActualTargetContainer(item))
        {
            return ProjectionScope.TargetScheduler;
        }

        var type = entity.GetType();
        var typeName = type.Name;
        var namespaceName = type.Namespace ?? string.Empty;
        if (typeName.StartsWith("UnknownSequence", StringComparison.Ordinal))
        {
            return ProjectionScope.Other;
        }
        var isNinaSequencer = namespaceName.StartsWith(
            "NINA.Sequencer",
            StringComparison.Ordinal);
        var isSequencerPlus = namespaceName.StartsWith(
            "NINA.Plugin.SequencerPlus",
            StringComparison.Ordinal);
        if (!isNinaSequencer && !isSequencerPlus)
        {
            // A third-party item may reuse a familiar simple type name. Do not
            // let that accidentally bypass Other Events; only N.I.N.A. and the
            // explicitly supported Sequencer+ surface use the mappings below.
            return ProjectionScope.Other;
        }
        if (IsSafetyEntity(typeName))
        {
            return ProjectionScope.Sequence | ProjectionScope.Safety;
        }
        if (typeName.StartsWith("Autofocus", StringComparison.Ordinal)
            || typeName == "RunAutofocus"
            || namespaceName.Contains(".Autofocus", StringComparison.Ordinal))
        {
            return ProjectionScope.Autofocus;
        }
        if (typeName.Contains("MeridianFlip", StringComparison.OrdinalIgnoreCase)
            || typeName is "FindHome" or "ParkScope" or "SetTracking"
                or "SlewScopeToRaDec" or "SlewScopeToAltAz"
                or "SlewToRADec" or "SlewToAltAz" or "UnparkScope"
                or "Center" or "CenterAndRotate" or "SolveAndSync" or "SolveAndRotate"
                or "CenterAfterDriftTrigger" or "PlatesolvingImageFollower"
                or "DoFlip" or "PassMeridian"
            || namespaceName.Contains(".Telescope", StringComparison.Ordinal)
            || namespaceName.Contains(".Platesolving", StringComparison.Ordinal)
            || namespaceName.Contains(".MeridianFlip", StringComparison.Ordinal))
        {
            return ProjectionScope.Mount;
        }
        if (typeName is "TakeExposure" or "TakeSubframeExposure"
            or "TakeManyExposures" or "SmartExposure" or "SmartSubframeExposure"
            or "DewHeater" or "SetReadoutMode" or "SetUSBLimit"
            || namespaceName.Contains(".Imaging", StringComparison.Ordinal))
        {
            return ProjectionScope.Images;
        }
        if (typeName is "AutoBrightnessFlat" or "AutoExposureFlat" or "SkyFlat"
            or "TrainedDarkFlatExposure" or "TrainedFlatExposure")
        {
            return ProjectionScope.Images | ProjectionScope.ObservatoryAndFlatPanel;
        }
        if (typeName is "Dither" or "StartGuiding" or "StopGuiding"
            or "DitherAfterExposures" or "RestoreGuiding"
            || namespaceName.Contains(".Guider", StringComparison.Ordinal))
        {
            return ProjectionScope.Guiding;
        }
        if (typeName is "SwitchFilter" or "MoveFocuserAbsolute"
            or "MoveFocuserByTemperature" or "MoveFocuserRelative"
            or "MoveRotatorMechanical" or "RotateImage" or "FlipRotator"
            || namespaceName.Contains(".FilterWheel", StringComparison.Ordinal)
            || namespaceName.Contains(".Focuser", StringComparison.Ordinal)
            || namespaceName.Contains(".Rotator", StringComparison.Ordinal))
        {
            return ProjectionScope.FilterFocuserRotator;
        }
        if (typeName is "OpenDomeShutter" or "CloseDomeShutter"
            or "DisableDomeSynchronization" or "EnableDomeSynchronization"
            or "FindHomeDome" or "ParkDome" or "SlewDomeAzimuth"
            or "SynchronizeDome" or "CloseCover" or "OpenCover"
            or "SetBrightness" or "ToggleLight" or "SynchronizeDomeTrigger"
            || namespaceName.Contains(".Dome", StringComparison.Ordinal)
            || namespaceName.Contains(".FlatDevice", StringComparison.Ordinal))
        {
            return ProjectionScope.ObservatoryAndFlatPanel;
        }
        if (typeName is "ConnectAllEquipment" or "ConnectEquipment"
            or "DisconnectAllEquipment" or "DisconnectEquipment" or "SwitchProfile"
            or "ReconnectOnDownloadFailure" or "ReconnectTrigger"
            || namespaceName.Contains(".Connect", StringComparison.Ordinal))
        {
            return ProjectionScope.EquipmentConnections;
        }
        if (typeName is "CoolCamera" or "WarmCamera"
            or "WaitForTime" or "WaitForTimeSpan" or "WaitUntil"
            or "WaitIndefinitely" or "Break" or "WaitForAltitude"
            or "WaitForMoonAltitude" or "WaitForSunAltitude"
            or "WaitUntilAboveHorizon")
        {
            return ProjectionScope.Sequence;
        }

        if (typeName == "SetSwitchValue"
            || namespaceName.Contains(".Switch", StringComparison.Ordinal))
        {
            return ProjectionScope.Other;
        }
        return isNinaSequencer ? ProjectionScope.Sequence : ProjectionScope.Other;
    }

    private static bool IsSafetyEntity(string typeName) =>
        typeName.Contains("Safety", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Unsafe", StringComparison.OrdinalIgnoreCase)
        || typeName is "WaitUntilSafe" or "IfSafe" or "OnceSafe" or "SafeTrigger";

    private static bool ScopesEnabled(
        ProjectionScope scopes,
        DirectEventDeliveryOptions delivery) =>
        (!scopes.HasFlag(ProjectionScope.Images) || delivery.Images)
        && (!scopes.HasFlag(ProjectionScope.Autofocus) || delivery.Autofocus)
        && (!scopes.HasFlag(ProjectionScope.Guiding) || delivery.Guiding)
        && (!scopes.HasFlag(ProjectionScope.Mount) || delivery.Mount)
        && (!scopes.HasFlag(ProjectionScope.Sequence) || delivery.Sequence)
        && (!scopes.HasFlag(ProjectionScope.Safety) || delivery.Safety)
        && (!scopes.HasFlag(ProjectionScope.FilterFocuserRotator)
            || delivery.FilterFocuserRotator)
        && (!scopes.HasFlag(ProjectionScope.ObservatoryAndFlatPanel)
            || delivery.ObservatoryAndFlatPanel)
        && (!scopes.HasFlag(ProjectionScope.EquipmentConnections)
            || delivery.EquipmentConnections)
        && (!scopes.HasFlag(ProjectionScope.Other) || delivery.OtherEvents)
        && (!scopes.HasFlag(ProjectionScope.TargetScheduler)
            || delivery.TargetScheduler);

    internal static bool ShouldSendSequenceFailure(
        ISequenceEntity? entity,
        DirectEventDeliveryOptions delivery) =>
        ShouldSendSequenceFailure(
            GetSequenceFailureDeliveryScopeMask(entity),
            delivery);

    internal static int GetSequenceFailureDeliveryScopeMask(ISequenceEntity? entity)
    {
        var entityScopes = entity is null
            ? ProjectionScope.Other
            : RequiredScopes(entity);
        return (int)(ProjectionScope.Sequence | entityScopes);
    }

    internal static bool ShouldSendSequenceFailure(
        int deliveryScopeMask,
        DirectEventDeliveryOptions delivery) =>
        ScopesEnabled((ProjectionScope)deliveryScopeMask, delivery);

    /// <summary>
    /// A terminal sequence result can only claim success when every category
    /// that may own an entity failure was visible for the whole run. This is
    /// intentionally independent from popup and log forwarding: neither is a
    /// source for the root container's failure event.
    /// </summary>
    internal static bool HasCompleteSequenceFailureCoverage(
        DirectEventDeliveryOptions delivery) =>
        delivery.Images
        && delivery.Autofocus
        && delivery.Guiding
        && delivery.Mount
        && delivery.Sequence
        && delivery.Safety
        && delivery.FilterFocuserRotator
        && delivery.ObservatoryAndFlatPanel
        && delivery.EquipmentConnections
        && delivery.OtherEvents
        && delivery.TargetScheduler;

    private static void AddDeliveryDetails(
        IDictionary<string, object?> result,
        ProjectionScope requiredScopes)
    {
        if (requiredScopes != ProjectionScope.None)
        {
            result["ChatEnabled"] = true;
        }
    }

    private static Dictionary<string, object?> BuildTrigger(
        ISequenceTrigger trigger,
        DirectEventDeliveryOptions delivery)
    {
        var requiredScopes = RequiredScopes(trigger);
        if (!ScopesEnabled(requiredScopes, delivery))
        {
            return SuppressedEntity("Trigger");
        }

        var result = BaseEntity(trigger, "_Trigger");
        AddDeliveryDetails(result, requiredScopes);
        if (trigger.GetType().Name.Contains("MeridianFlip", StringComparison.OrdinalIgnoreCase))
        {
            result["OperationKind"] = "meridian_flip";
        }
        if (IsSafetyEntity(trigger.GetType().Name))
        {
            result["OperationKind"] = "safety_trigger";
        }
        AddIfPresent(trigger, result, "TimeToMeridianFlip", "TimeToFlip");
        AddIfPresent(trigger, result, "HFRTrendPercentage", "HFRTrendPercentage");
        AddIfPresent(trigger, result, "OriginalHFR", "OriginalHFR");
        AddIfPresent(trigger, result, "SampleSize", "SampleSize");
        AddIfPresent(trigger, result, "Amount",
            trigger.GetType().Name.Contains("Temperature", StringComparison.Ordinal)
                ? "TargetTemperature"
                : trigger.GetType().Name.Contains("HFR", StringComparison.Ordinal)
                    ? "DeltaHFR"
                    : trigger.GetType().Name.Contains("Time", StringComparison.Ordinal)
                        ? "DeltaTime"
                        : "DeltaExposures");
        AddIfPresent(trigger, result, "DeltaT", "DeltaTemperature");
        AddIfPresent(trigger, result, "Elapsed", "ElapsedTime");
        AddIfPresent(trigger, result, "ProgressExposures", "Exposures");
        AddIfPresent(
            trigger,
            result,
            "AfterExposures",
            trigger.GetType().Name == "DitherAfterExposures"
                ? "TargetExposures"
                : "DeltaExposures");
        var triggerCoordinates = OptionalProperty(trigger, "Coordinates");
        if (triggerCoordinates is not null)
        {
            var projected = CompactCoordinates(triggerCoordinates);
            if (projected.Count != 0)
            {
                result["Coordinates"] = projected;
            }
        }
        AddIfPresent(trigger, result, "LastDistanceArcMinutes", "Drift");
        AddIfPresent(trigger, result, "DistanceArcMinutes", "TargetDrift");
        return result;
    }

    private static Dictionary<string, object?> BuildCondition(
        ISequenceCondition condition,
        DirectEventDeliveryOptions delivery)
    {
        var requiredScopes = RequiredScopes(condition);
        if (!ScopesEnabled(requiredScopes, delivery))
        {
            return SuppressedEntity("Condition");
        }

        var result = BaseEntity(condition, "_Condition");
        AddDeliveryDetails(result, requiredScopes);
        if (IsSafetyEntity(condition.GetType().Name))
        {
            result["OperationKind"] = "safety_condition";
            AddIfPresent(condition, result, "IsSafe", "IsSafe");
        }
        AddIfPresent(condition, result, "RemainingTime", "RemainingTime");
        AddTargetTime(condition, result);
        AddIfPresent(condition, result, "Iterations", "Iterations");
        AddIfPresent(condition, result, "CompletedIterations", "CompletedIterations");
        AddIfPresent(condition, result, "UserMoonIllumination", "TargetIllumination");
        AddIfPresent(condition, result, "CurrentMoonIllumination", "CurrentIllumination");

        var data = OptionalProperty(condition, "Data");
        if (data is not null)
        {
            AddIfPresent(data, result, "Offset", "Altitude");
            AddIfPresent(data, result, "CurrentAltitude", "CurrentAltitude");
            AddIfPresent(data, result, "ExpectedTime", "ExpectedTime");
            AddTimestampIfPresent(data, result, "ExpectedDateTime", "ExpectedDateTime");
        }
        return result;
    }

    private static Dictionary<string, object?> BaseEntity(ISequenceEntity entity, string suffix) =>
        new()
        {
            ["Name"] = $"{entity.Name ?? string.Empty}{suffix}",
            ["Status"] = entity.Status.ToString(),
        };

    private static void AddItemDetails(
        ISequenceItem item,
        IDictionary<string, object?> result,
        bool? safetyMonitorIsSafe,
        DirectEventDeliveryOptions delivery)
    {
        var typeName = item.GetType().Name;
        var hasRestrictedWaitDetails = typeName is "WaitUntil" or "WaitIndefinitely" or "Break";
        if (!hasRestrictedWaitDetails)
        {
            foreach (var (propertyName, wireName) in DetailNames)
            {
                AddIfPresent(item, result, propertyName, wireName);
            }
        }

        if (typeName == "CoolCamera")
        {
            result["OperationKind"] = "camera_cooling";
            AddIfPresent(item, result, "Duration", "MinCoolingTime");
        }
        else if (typeName == "WarmCamera")
        {
            result["OperationKind"] = "camera_warming";
            AddIfPresent(item, result, "Duration", "MinWarmingTime");
        }
        else if (typeName is "SlewScopeToRaDec" or "SlewScopeToAltAz"
            or "SlewToRADec" or "SlewToAltAz")
        {
            result["OperationKind"] = "mount_slew";
        }
        else if (typeName is "Center" or "CenterAndRotate")
        {
            result["OperationKind"] = "mount_center";
            AddPlateSolveOutput(item, result);
        }
        else if (typeName is "SolveAndSync" or "SolveAndRotate")
        {
            result["OperationKind"] = "plate_solve";
            AddPlateSolveOutput(item, result);
        }
        else if (typeName == "DewHeater")
        {
            AddIfPresent(item, result, "OnOff", "DewHeaterOn");
        }

        var coordinates = OptionalProperty(item, "Coordinates");
        if (coordinates is not null)
        {
            var projected = CompactCoordinates(
                OptionalProperty(coordinates, "Coordinates") ?? coordinates);
            if (projected.Count != 0)
            {
                result["Coordinates"] = projected;
            }
        }

        var filter = OptionalProperty(item, "Filter");
        if (filter is not null)
        {
            result["Filter"] = OptionalProperty(filter, "Name") ?? "Current";
        }

        if (typeName is "SmartExposure" or "TakeManyExposures" or "SmartSubframeExposure")
        {
            AddAggregateExposureDetails(item, result, typeName == "SmartExposure");
        }

        AddIfPresent(item, result, "TrackingMode", "TrackingMode");

        var data = OptionalProperty(item, "Data");
        if (data is not null)
        {
            AddIfPresent(data, result, "Offset", "Altitude");
            AddIfPresent(data, result, "CurrentAltitude", "CurrentAltitude");
            AddIfPresent(data, result, "ExpectedTime", "ExpectedTime");
        }

        if (typeName == "WaitForTime")
        {
            result["OperationKind"] = "time_wait";
            var duration = OptionalMethod(item, "GetEstimatedDuration");
            if (duration is TimeSpan wait)
            {
                result["CalculatedWaitDuration"] = wait;
                var dateTimeProvider = OptionalProperty(item, "DateTime");
                if (OptionalProperty(dateTimeProvider, "Now") is DateTime now)
                {
                    AddTimestamp(result, "TargetTime", now + wait);
                }
            }
        }
        else if (typeName == "WaitForTimeSpan")
        {
            result["OperationKind"] = "time_wait";
            var duration = OptionalMethod(item, "GetEstimatedDuration");
            if (duration is TimeSpan wait)
            {
                result["CalculatedWaitDuration"] = wait;
            }
        }
        else if (typeName is "WaitForAltitude" or "WaitForMoonAltitude"
            or "WaitForSunAltitude" or "WaitUntilAboveHorizon")
        {
            result["OperationKind"] = "astronomical_wait";
            if (data is not null)
            {
                AddIfPresent(data, result, "TargetAltitude", "TargetAltitude");
                AddIfPresent(data, result, "Comparator", "Comparator");
                AddTimestampIfPresent(data, result, "ExpectedDateTime", "ExpectedDateTime");
            }
        }
        else if (typeName == "WaitUntilSafe")
        {
            // Both N.I.N.A.'s built-in instruction and Sequencer+ use this
            // simple type name. The instruction's own IsSafe property can be
            // stale while it is waiting, so project the mediator state that
            // was captured by the caller instead.
            result["OperationKind"] = "safety_wait";
            result["IsSafe"] = safetyMonitorIsSafe;
            AddIfPresent(item, result, "WaitInterval", "WaitInterval");
        }
        else if (typeName == "WaitUntil")
        {
            // The expression can contain private observatory or equipment
            // values. Only advertise that an expression wait is active and
            // how often it is evaluated.
            result["OperationKind"] = "condition_wait";
            AddIfPresent(item, result, "WaitInterval", "WaitInterval");
        }
        else if (typeName is "WaitIndefinitely" or "Break")
        {
            // Sequencer+ implements these with placeholder Time values that
            // do not represent the actual wait. Break.Reason may also carry
            // arbitrary user text, so neither is part of the wire contract.
            result["OperationKind"] = "manual_wait";
        }
    }

    private static void AddPlateSolveOutput(
        object item,
        IDictionary<string, object?> result)
    {
        var status = OptionalProperty(item, "PlateSolveStatusVM");
        var solve = OptionalProperty(status, "PlateSolveResult");
        if (solve is null)
        {
            return;
        }

        var output = new Dictionary<string, object?>();
        AddIfPresent(solve, output, "SolveTime", "SolveTime");
        AddIfPresent(solve, output, "Success", "Success");
        AddFiniteIfPresent(solve, output, "PositionAngle", "PositionAngle");
        AddFiniteIfPresent(solve, output, "Pixscale", "PixelScale");
        AddFiniteIfPresent(solve, output, "Radius", "RadiusDegrees");
        AddIfPresent(solve, output, "Flipped", "Flipped");

        var coordinates = OptionalProperty(solve, "Coordinates");
        if (coordinates is not null)
        {
            output["Coordinates"] = CompactCoordinates(coordinates);
        }

        var separation = OptionalProperty(solve, "Separation");
        var distance = OptionalProperty(separation, "Distance");
        AddFiniteIfPresent(distance, output, "ArcSeconds", "SeparationArcseconds");
        AddIfPresent(solve, output, "RaErrorString", "RaError");
        AddIfPresent(solve, output, "DecErrorString", "DecError");
        AddFiniteIfPresent(solve, output, "RaPixError", "RaPixelError");
        AddFiniteIfPresent(solve, output, "DecPixError", "DecPixelError");

        // N.I.N.A. keeps plate-solve thumbnails on sequence instructions long
        // after capture. Their origin carries no image-consent provenance, so
        // embedding one in a routinely polled state snapshot could disclose
        // an image created while sharing was disabled. Consented image bytes
        // are available only through the guarded image-history/thumbnail API.
        result["PlateSolveOutput"] = output;
    }

    internal static Dictionary<string, object?>? ProjectCoordinates(
        object? coordinates,
        DirectAccessOptions access)
    {
        if (coordinates is null)
        {
            return null;
        }

        var projected = CompactCoordinates(coordinates);
        DirectPrivacyProjection.Redact(projected, access);
        return projected.Count == 0 ? null : projected;
    }

    private static Dictionary<string, object?> CompactCoordinates(object coordinates)
    {
        var result = new Dictionary<string, object?>();
        AddFiniteIfPresent(coordinates, result, "RA", "RA");
        AddFiniteIfPresent(coordinates, result, "RADegrees", "RADegrees");
        AddIfPresent(coordinates, result, "RAString", "RAString");
        AddFiniteIfPresent(coordinates, result, "Dec", "Dec");
        AddIfPresent(coordinates, result, "DecString", "DecString");
        AddIfPresent(coordinates, result, "Epoch", "Epoch");
        AddFiniteIfPresent(coordinates, result, "Altitude", "Altitude");
        AddFiniteIfPresent(coordinates, result, "Azimuth", "Azimuth");
        return result;
    }

    private static void AddAggregateExposureDetails(
        object item,
        IDictionary<string, object?> result,
        bool includeDither)
    {
        var exposure = OptionalMethod(item, "GetTakeExposure");
        if (exposure is not null)
        {
            foreach (var name in new[] { "ExposureTime", "ExposureCount", "Binning", "Gain", "Offset" })
            {
                AddIfPresent(exposure, result, name, name);
            }
            AddIfPresent(exposure, result, "ImageType", "Type");
        }

        var loop = OptionalMethod(item, "GetLoopCondition");
        if (loop is not null)
        {
            AddIfPresent(loop, result, "Iterations", "Iterations");
            AddIfPresent(loop, result, "CompletedIterations", "CompletedIterations");
        }

        if (includeDither)
        {
            var dither = OptionalMethod(item, "GetDitherAfterExposures");
            if (dither is not null)
            {
                AddIfPresent(dither, result, "ProgressExposures", "DitherProgressExposures");
                AddIfPresent(dither, result, "AfterExposures", "DitherTargetExposures");
            }

            var switchFilter = OptionalMethod(item, "GetSwitchFilter");
            var filter = switchFilter is null ? null : OptionalProperty(switchFilter, "Filter");
            result["Filter"] = filter is null
                ? "Current"
                : OptionalProperty(filter, "Name") ?? "Current";
        }
    }

    private static void AddIfPresent(
        object source,
        IDictionary<string, object?> destination,
        string propertyName,
        string wireName)
    {
        var value = OptionalProperty(source, propertyName);
        if (value is not null)
        {
            destination[wireName] = value.GetType().IsEnum ? value.ToString() : value;
        }
    }

    private static void AddFiniteIfPresent(
        object? source,
        IDictionary<string, object?> destination,
        string propertyName,
        string wireName)
    {
        var value = OptionalProperty(source, propertyName);
        if (value is not null
            && double.TryParse(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && double.IsFinite(number))
        {
            destination[wireName] = number;
        }
    }

    private static void AddTargetTime(
        object source,
        IDictionary<string, object?> destination)
    {
        if (OptionalProperty(source, "RemainingTime") is not TimeSpan remaining)
        {
            return;
        }
        var dateTimeProvider = OptionalProperty(source, "DateTime");
        if (OptionalProperty(dateTimeProvider, "Now") is DateTime now)
        {
            AddTimestamp(destination, "TargetTime", now + remaining);
        }
    }

    private static void AddTimestampIfPresent(
        object source,
        IDictionary<string, object?> destination,
        string propertyName,
        string wireName)
    {
        var value = OptionalProperty(source, propertyName);
        switch (value)
        {
            case DateTime dateTime:
                AddTimestamp(destination, wireName, dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                destination[wireName] = dateTimeOffset;
                break;
        }
    }

    private static void AddTimestamp(
        IDictionary<string, object?> destination,
        string wireName,
        DateTime value)
    {
        // N.I.N.A. computes waits in observatory-local wall time. Some
        // providers return DateTimeKind.Unspecified, which System.Text.Json
        // would serialize without an offset and the Hub could then interpret
        // in its own timezone. Normalize every usable wall-clock value to an
        // explicit offset before it crosses the Direct boundary.
        if (value == DateTime.MinValue || value == DateTime.MaxValue)
        {
            return;
        }

        var timestamp = value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
                TimeZoneInfo.Local.GetUtcOffset(value)),
        };
        destination[wireName] = timestamp;
    }

    private static object RequiredProperty(object source, string name) =>
        OptionalProperty(source, name)
        ?? throw new InvalidOperationException(
            $"N.I.N.A. sequence object '{source.GetType().Name}' has no '{name}' value.");

    private static object? OptionalProperty(object? source, string name)
    {
        if (source is null)
        {
            return null;
        }
        try
        {
            return source.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(source);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static object? OptionalMethod(object source, string name)
    {
        try
        {
            return source.GetType()
                .GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)
                ?.Invoke(source, null);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
