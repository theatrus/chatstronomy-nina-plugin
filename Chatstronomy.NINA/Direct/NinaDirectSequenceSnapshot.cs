using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private static readonly ConditionalWeakTable<BitmapSource, EncodedThumbnail>
        EncodedThumbnails = new();

    private sealed record EncodedThumbnail(byte[] Data);

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
            ["Time"] = "Delay",
            ["Iterations"] = "Iterations",
            ["CompletedIterations"] = "CompletedIterations",
        };

    internal static IReadOnlyList<Dictionary<string, object?>> Build(
        ISequenceMediator sequence,
        DirectEventDeliveryOptions? delivery = null,
        DirectAccessOptions? access = null)
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
        var options = delivery ?? DirectEventDeliveryOptions.Default;
        result.AddRange(root.GetItemsSnapshot().Select(item => BuildItem(item, options)));
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

    private static Dictionary<string, object?> BuildItem(
        ISequenceItem item,
        DirectEventDeliveryOptions delivery)
    {
        var itemType = item.GetType().Name;
        var expandContainer = item is ISequenceContainer
            && itemType is not "SmartExposure" and not "TakeManyExposures";
        var result = BaseEntity(item, expandContainer ? "_Container" : string.Empty);

        if (item is IDeepSkyObjectContainer)
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
                .Select(child => BuildItem(child, delivery))
                .ToArray();
            result["Conditions"] = container is IConditionable conditionable
                ? conditionable.GetConditionsSnapshot().Select(BuildCondition).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["Triggers"] = container is ITriggerable triggerable
                ? triggerable.GetTriggersSnapshot().Select(BuildTrigger).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
        }

        AddItemDetails(item, result);
        AddDeliveryDetails(item, result, delivery);
        return result;
    }

    private static void AddDeliveryDetails(
        ISequenceItem item,
        IDictionary<string, object?> result,
        DirectEventDeliveryOptions delivery)
    {
        var typeName = item.GetType().Name;
        if (typeName is "SlewScopeToRaDec" or "SlewScopeToAltAz" or "Center" or "CenterAndRotate")
        {
            result["ChatEnabled"] = delivery.Mount;
        }
        else if (typeName is "CoolCamera" or "WarmCamera" or "WaitForTime" or "WaitForTimeSpan")
        {
            result["ChatEnabled"] = delivery.Sequence;
        }
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
            result["OperationKind"] = "camera_cooling";
            AddIfPresent(item, result, "Duration", "MinCoolingTime");
        }
        else if (typeName is "SlewScopeToRaDec" or "SlewScopeToAltAz")
        {
            result["OperationKind"] = "mount_slew";
        }
        else if (typeName is "Center" or "CenterAndRotate")
        {
            result["OperationKind"] = "mount_center";
            AddPlateSolveOutput(item, result);
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
            result["OperationKind"] = "time_wait";
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
        else if (typeName == "WaitForTimeSpan")
        {
            result["OperationKind"] = "time_wait";
            var duration = OptionalMethod(item, "GetEstimatedDuration");
            if (duration is TimeSpan wait)
            {
                result["CalculatedWaitDuration"] = wait;
            }
        }
    }

    private static void AddPlateSolveOutput(
        object item,
        IDictionary<string, object?> result)
    {
        var status = OptionalProperty(item, "PlateSolveStatusVM");
        var solve = OptionalProperty(status, "PlateSolveResult");
        var thumbnail = OptionalProperty(status, "Thumbnail") as BitmapSource;
        if (solve is null && thumbnail is null)
        {
            return;
        }

        var output = new Dictionary<string, object?>();
        if (solve is not null)
        {
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
        }

        var encoded = EncodeThumbnail(thumbnail);
        if (encoded is not null)
        {
            output["ThumbnailBase64"] = Convert.ToBase64String(encoded);
            output["ThumbnailMediaType"] = "image/jpeg";
        }

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

    private static byte[]? EncodeThumbnail(BitmapSource? source)
    {
        if (source is null)
        {
            return null;
        }
        if (EncodedThumbnails.TryGetValue(source, out var cached))
        {
            return cached.Data;
        }

        try
        {
            var scale = source.PixelWidth > 256 ? 256d / source.PixelWidth : 1d;
            BitmapSource thumbnail = source;
            if (scale < 1)
            {
                thumbnail = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var encoded = stream.ToArray();
            try
            {
                EncodedThumbnails.Add(source, new EncodedThumbnail(encoded));
            }
            catch (ArgumentException)
            {
                // Another query cached this same WPF image concurrently.
            }
            return encoded;
        }
        catch
        {
            // Solve metadata remains useful if a third-party image source
            // cannot be encoded by WPF.
            return null;
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
