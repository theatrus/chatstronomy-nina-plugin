using System.Reflection;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Projects the loaded advanced sequence into the small, stable JSON tree
/// consumed by Chatstronomy. N.I.N.A. does not expose its root container on
/// <see cref="ISequenceMediator"/>, so root discovery is isolated here while
/// the actual projection uses public sequencer interfaces.
/// </summary>
internal static class NinaDirectSequenceSnapshot
{
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
            ["Value"] = "Value",
            ["Text"] = "Text",
            ["Script"] = "Script",
            ["FilePath"] = "FilePath",
            ["Time"] = "Delay",
            ["Iterations"] = "Iterations",
            ["CompletedIterations"] = "CompletedIterations",
        };

    internal static IReadOnlyList<Dictionary<string, object?>> Build(ISequenceMediator sequence)
    {
        if (!sequence.Initialized)
        {
            throw new InvalidOperationException("Sequence is not initialized.");
        }

        var root = GetSequenceRoot(sequence);
        var result = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["GlobalTriggers"] = root is ITriggerable triggerable
                    ? triggerable.GetTriggersSnapshot().Select(BuildTrigger).ToArray()
                    : Array.Empty<Dictionary<string, object?>>(),
            },
        };
        result.AddRange(root.GetItemsSnapshot().Select(BuildItem));
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

    private static Dictionary<string, object?> BuildItem(ISequenceItem item)
    {
        var itemType = item.GetType().Name;
        var expandContainer = item is ISequenceContainer
            && itemType is not "SmartExposure" and not "TakeManyExposures";
        var result = BaseEntity(item, expandContainer ? "_Container" : string.Empty);

        if (expandContainer && item is ISequenceContainer container)
        {
            result["Items"] = container.GetItemsSnapshot().Select(BuildItem).ToArray();
            result["Conditions"] = container is IConditionable conditionable
                ? conditionable.GetConditionsSnapshot().Select(BuildCondition).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["Triggers"] = container is ITriggerable triggerable
                ? triggerable.GetTriggersSnapshot().Select(BuildTrigger).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
        }

        AddItemDetails(item, result);
        return result;
    }

    private static Dictionary<string, object?> BuildTrigger(ISequenceTrigger trigger)
    {
        var result = BaseEntity(trigger, "_Trigger");
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
        AddIfPresent(trigger, result, "Coordinates", "Coordinates");
        AddIfPresent(trigger, result, "LastDistanceArcMinutes", "Drift");
        AddIfPresent(trigger, result, "DistanceArcMinutes", "TargetDrift");
        return result;
    }

    private static Dictionary<string, object?> BuildCondition(ISequenceCondition condition)
    {
        var result = BaseEntity(condition, "_Condition");
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
            AddIfPresent(data, result, "ExpectedDateTime", "ExpectedDateTime");
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
        IDictionary<string, object?> result)
    {
        foreach (var (propertyName, wireName) in DetailNames)
        {
            AddIfPresent(item, result, propertyName, wireName);
        }

        var typeName = item.GetType().Name;
        if (typeName == "CoolCamera")
        {
            AddIfPresent(item, result, "Duration", "MinCoolingTime");
        }
        else if (typeName == "WarmCamera")
        {
            AddIfPresent(item, result, "Duration", "MinWarmingTime");
        }
        else if (typeName == "DewHeater")
        {
            AddIfPresent(item, result, "OnOff", "DewHeaterOn");
        }

        var coordinates = OptionalProperty(item, "Coordinates");
        if (coordinates is not null)
        {
            result["Coordinates"] = OptionalProperty(coordinates, "Coordinates") ?? coordinates;
        }

        var filter = OptionalProperty(item, "Filter");
        if (filter is not null)
        {
            result["Filter"] = OptionalProperty(filter, "Name") ?? "Current";
        }

        if (typeName is "SmartExposure" or "TakeManyExposures")
        {
            AddAggregateExposureDetails(item, result, typeName == "SmartExposure");
        }

        var selectedSwitch = OptionalProperty(item, "SelectedSwitch");
        if (selectedSwitch is not null)
        {
            AddIfPresent(selectedSwitch, result, "Id", "Index");
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
            var duration = OptionalMethod(item, "GetEstimatedDuration");
            if (duration is TimeSpan wait)
            {
                result["CalculatedWaitDuration"] = wait;
                var dateTimeProvider = OptionalProperty(item, "DateTime");
                if (OptionalProperty(dateTimeProvider, "Now") is DateTime now)
                {
                    result["TargetTime"] = now + wait;
                }
            }
        }
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
            destination["TargetTime"] = now + remaining;
        }
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
