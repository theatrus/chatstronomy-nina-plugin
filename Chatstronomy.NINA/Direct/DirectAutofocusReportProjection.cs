using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Projects native N.I.N.A. and third-party autofocus reports onto the
/// stable, path-free report surface consumed by Chatstronomy.
/// </summary>
internal static class DirectAutofocusReportProjection
{
    internal static JsonElement Project(
        JsonElement report,
        DirectAutofocusCompletion? completion = null)
    {
        var source = Unwrap(report);
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The autofocus report root must be an object.");
        }

        var calculated = RequiredObject(source, "CalculatedFocusPoint");
        var timestamp = RequiredStringProperty(source, "Timestamp");

        var projected = new JsonObject
        {
            ["Version"] = IntegerProperty(source, "Version", 2),
            ["Filter"] = StringProperty(source, "Filter", completion?.Filter ?? string.Empty),
            ["AutoFocuserName"] = StringProperty(source, "AutoFocuserName", "N.I.N.A."),
            ["StarDetectorName"] = StringProperty(source, "StarDetectorName", string.Empty),
            ["Timestamp"] = timestamp,
            ["Temperature"] = TemperatureProperty(source, completion),
            ["Method"] = StringProperty(source, "Method", string.Empty),
            ["Fitting"] = StringProperty(source, "Fitting", string.Empty),
            ["InitialFocusPoint"] = ProjectFocusPoint(
                OptionalObject(source, "InitialFocusPoint") ?? calculated),
            ["CalculatedFocusPoint"] = ProjectFocusPoint(calculated),
            ["PreviousFocusPoint"] = ProjectPreviousFocusPoint(
                OptionalObject(source, "PreviousFocusPoint")),
            ["MeasurePoints"] = ProjectMeasurePoints(source),
            ["Intersections"] = ProjectIntersections(source),
            ["Fittings"] = ProjectFittings(source),
            ["RSquares"] = ProjectRSquares(source),
            ["BacklashCompensation"] = ProjectBacklashCompensation(source),
            ["Duration"] = StringProperty(source, "Duration", "00:00:00"),
        };

        CopyOptionalNumber(source, projected, "FinalHFR");
        CopyOptionalNumber(source, projected, "HyperbolicMinimumStdError");
        CopyOptionalNumber(source, projected, "HyperbolicReducedChiSquared");
        CopyOptionalNumber(source, projected, "HyperbolicLeaveOneOutStdError");
        CopyOptionalInteger(source, projected, "AcceptedStarCountMin");
        CopyOptionalInteger(source, projected, "AcceptedStarCountMax");
        CopyOptionalHyperbolicFitModel(source, projected);
        CopyOptionalRegion(source, projected);
        CopyOptionalHocusAlgorithm(source, projected);

        return JsonSerializer.SerializeToElement(projected, DirectProtocol.JsonOptions);
    }

    internal static JsonElement Unwrap(JsonElement report)
    {
        if (report.ValueKind == JsonValueKind.Object
            && TryGetProperty(report, "Response", out var response)
            && response.ValueKind == JsonValueKind.Object)
        {
            return response;
        }
        return report;
    }

    private static JsonObject ProjectFocusPoint(JsonElement point) => new()
    {
        ["Position"] = RequiredFiniteNumberProperty(point, "Position"),
        ["Value"] = NumberOrNamedProperty(point, "Value", "NaN"),
        ["Error"] = NumberOrNamedProperty(point, "Error", "NaN"),
    };

    private static JsonObject ProjectPreviousFocusPoint(JsonElement? point)
    {
        if (point is not JsonElement value)
        {
            return new JsonObject
            {
                ["Position"] = "NaN",
                ["Value"] = "NaN",
                ["Error"] = "NaN",
            };
        }
        return new JsonObject
        {
            ["Position"] = NumberOrNamedProperty(value, "Position", "NaN"),
            ["Value"] = NumberOrNamedProperty(value, "Value", "NaN"),
            ["Error"] = NumberOrNamedProperty(value, "Error", "NaN"),
        };
    }

    private static JsonArray ProjectMeasurePoints(JsonElement source)
    {
        var result = new JsonArray();
        if (!TryGetProperty(source, "MeasurePoints", out var points)
            || points.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    result.Add(ProjectFocusPoint(point));
                }
                catch (JsonException)
                {
                    // A malformed sample cannot be plotted. Keep the valid
                    // samples instead of rejecting the completed run.
                }
            }
        }
        return result;
    }

    private static JsonObject ProjectIntersections(JsonElement source)
    {
        var intersections = OptionalObject(source, "Intersections");
        var result = new JsonObject();
        foreach (var name in new[]
        {
            "TrendLineIntersection",
            "HyperbolicMinimum",
            "QuadraticMinimum",
            "GaussianMaximum",
        })
        {
            result[name] = intersections is JsonElement value
                && OptionalObject(value, name) is JsonElement point
                    ? ProjectFocusPoint(point)
                    : null;
        }
        return result;
    }

    private static JsonObject ProjectFittings(JsonElement source)
    {
        var fittings = OptionalObject(source, "Fittings");
        var result = new JsonObject();
        foreach (var name in new[] { "Quadratic", "Hyperbolic", "Gaussian", "LeftTrend", "RightTrend" })
        {
            result[name] = fittings is JsonElement value
                ? StringProperty(value, name, string.Empty)
                : JsonValue.Create(string.Empty);
        }
        return result;
    }

    private static JsonObject ProjectRSquares(JsonElement source)
    {
        var squares = OptionalObject(source, "RSquares");
        var result = new JsonObject();
        foreach (var name in new[] { "Quadratic", "Hyperbolic", "LeftTrend", "RightTrend" })
        {
            result[name] = squares is JsonElement value
                ? NumberOrNamedProperty(value, name, "NaN")
                : JsonValue.Create("NaN");
        }
        return result;
    }

    private static JsonObject ProjectBacklashCompensation(JsonElement source)
    {
        var backlash = OptionalObject(source, "BacklashCompensation");
        return new JsonObject
        {
            ["BacklashCompensationModel"] = backlash is JsonElement value
                ? StringProperty(value, "BacklashCompensationModel", string.Empty)
                : JsonValue.Create(string.Empty),
            ["BacklashIN"] = backlash is JsonElement inValue
                ? IntegerProperty(inValue, "BacklashIN", 0)
                : JsonValue.Create(0),
            ["BacklashOUT"] = backlash is JsonElement outValue
                ? IntegerProperty(outValue, "BacklashOUT", 0)
                : JsonValue.Create(0),
        };
    }

    private static void CopyOptionalNumber(
        JsonElement source,
        JsonObject projected,
        string name)
    {
        if (TryGetProperty(source, name, out _))
        {
            projected[name] = NumberOrNamedProperty(source, name, "NaN");
        }
    }

    private static void CopyOptionalInteger(
        JsonElement source,
        JsonObject projected,
        string name)
    {
        if (TryGetProperty(source, name, out var value)
            && ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer))
                || (value.ValueKind == JsonValueKind.String
                    && int.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out integer))))
        {
            projected[name] = integer;
        }
    }

    private static void CopyOptionalHyperbolicFitModel(
        JsonElement source,
        JsonObject projected)
    {
        if (!TryGetProperty(source, "HyperbolicFitModelChosen", out var model)
            || model.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (model.ValueKind == JsonValueKind.String)
        {
            projected["HyperbolicFitModelChosen"] = model.GetString();
            return;
        }
        if (model.TryGetInt32(out var modelCode))
        {
            projected["HyperbolicFitModelChosen"] = HyperbolicFitModelName(modelCode);
        }
    }

    private static string HyperbolicFitModelName(int modelCode) => modelCode switch
    {
        0 => "Symmetric",
        1 => "Uneven Blend",
        2 => "Tilted Hyperbola",
        3 => "Smooth Blend",
        4 => "Hybrid (Best Fit)",
        _ => $"Model {modelCode}",
    };

    private static void CopyOptionalRegion(JsonElement source, JsonObject projected)
    {
        if (OptionalObject(source, "Region") is not JsonElement region)
        {
            return;
        }

        try
        {
            var result = new JsonObject
            {
                ["Index"] = IntegerProperty(region, "Index", 0),
                ["OuterBoundary"] = ProjectRatioRect(
                    RequiredObject(region, "OuterBoundary")),
                ["InnerCropBoundary"] = OptionalObject(region, "InnerCropBoundary")
                    is JsonElement inner
                        ? ProjectRatioRect(inner)
                        : null,
            };
            projected["Region"] = result;
        }
        catch (JsonException)
        {
            // Region geometry enriches Hocus Focus results but is not needed
            // to render a normal N.I.N.A. autofocus report.
        }
    }

    private static JsonObject ProjectRatioRect(JsonElement rectangle) => new()
    {
        ["StartX"] = RequiredFiniteNumberProperty(rectangle, "StartX"),
        ["StartY"] = RequiredFiniteNumberProperty(rectangle, "StartY"),
        ["Width"] = RequiredFiniteNumberProperty(rectangle, "Width"),
        ["Height"] = RequiredFiniteNumberProperty(rectangle, "Height"),
    };

    private static void CopyOptionalHocusAlgorithm(
        JsonElement source,
        JsonObject projected)
    {
        var projectedAlgorithm = OptionalObject(source, "HocusFocusAlgorithm");
        var autofocusOptions = OptionalObject(source, "HocusFocusAutoFocusOptions");
        var focuserOptions = OptionalObject(source, "FocuserOptions");
        var detectionOptions = OptionalObject(source, "HocusFocusStarDetectionOptions");
        if (projectedAlgorithm is null
            && autofocusOptions is null
            && focuserOptions is null
            && detectionOptions is null)
        {
            return;
        }

        var algorithm = new JsonObject();
        if (projectedAlgorithm is JsonElement existing)
        {
            foreach (var name in new[]
            {
                "ValidateHfrImprovement",
                "WeightedHyperbolicFitEnabled",
                "ModelPSF",
                "UseOptimizedSettings",
                "HasOptimizedSettings",
            })
            {
                CopyOptionalBoolean(existing, algorithm, name);
            }
            foreach (var name in new[]
            {
                "HFRImprovementThreshold",
                "ReducedChiSquaredRejectionThreshold",
                "OutlierRejectionConfidence",
                "RSquaredThreshold",
            })
            {
                CopyOptionalFiniteNumber(existing, algorithm, name);
            }
            foreach (var name in new[] { "MaxOutlierRejections", "DetectionBinning" })
            {
                CopyOptionalInteger(existing, algorithm, name);
            }
            foreach (var name in new[]
            {
                "ConfiguredHyperbolicModel",
                "FitRejectionCriterion",
                "MeasurementAverage",
                "StarDetectionMode",
            })
            {
                CopyOptionalString(existing, algorithm, name);
            }
        }

        if (autofocusOptions is JsonElement autofocus)
        {
            foreach (var name in new[]
            {
                "ValidateHfrImprovement",
                "WeightedHyperbolicFitEnabled",
            })
            {
                CopyOptionalBoolean(autofocus, algorithm, name);
            }
            foreach (var name in new[]
            {
                "HFRImprovementThreshold",
                "ReducedChiSquaredRejectionThreshold",
                "OutlierRejectionConfidence",
            })
            {
                CopyOptionalFiniteNumber(autofocus, algorithm, name);
            }
            CopyOptionalInteger(autofocus, algorithm, "MaxOutlierRejections");

            if (TryGetProperty(autofocus, "HyperbolicFitModel", out var model)
                && model.TryGetInt32(out var modelCode))
            {
                algorithm["ConfiguredHyperbolicModel"] =
                    HyperbolicFitModelName(modelCode);
            }
            if (TryGetProperty(autofocus, "FitRejectionCriterion", out var criterion))
            {
                if (criterion.TryGetInt32(out var criterionCode))
                {
                    algorithm["FitRejectionCriterion"] = criterionCode switch
                    {
                        0 => "R²",
                        1 => "Reduced χ²",
                        _ => $"Criterion {criterionCode}",
                    };
                }
                else if (criterion.ValueKind == JsonValueKind.String)
                {
                    algorithm["FitRejectionCriterion"] = criterion.GetString();
                }
            }
            if (TryGetBoolean(autofocus, "ValidateHfrImprovement", out var validate))
            {
                projected["InitialHFRMeasured"] = validate;
                projected["FinalHFRSource"] = validate
                    ? "measured_validation"
                    : "fitted_estimate";
            }
        }

        if (focuserOptions is JsonElement focuser)
        {
            CopyOptionalFiniteNumber(focuser, algorithm, "RSquaredThreshold");
        }

        if (detectionOptions is JsonElement detection)
        {
            foreach (var name in new[]
            {
                "ModelPSF",
                "UseOptimizedSettings",
                "HasOptimizedSettings",
            })
            {
                CopyOptionalBoolean(detection, algorithm, name);
            }
            CopyOptionalInteger(detection, algorithm, "DetectionBinning");
            if (TryGetProperty(detection, "MeasurementAverage", out var average))
            {
                if (average.TryGetInt32(out var averageCode))
                {
                    algorithm["MeasurementAverage"] = averageCode switch
                    {
                        0 => "Median",
                        1 => "Mean + outlier detection",
                        _ => $"Mode {averageCode}",
                    };
                }
                else if (average.ValueKind == JsonValueKind.String)
                {
                    algorithm["MeasurementAverage"] = average.GetString();
                }
            }
            var optimized = TryGetBoolean(
                detection,
                "UseOptimizedSettings",
                out var useOptimized) && useOptimized;
            var hasOptimized = TryGetBoolean(
                detection,
                "HasOptimizedSettings",
                out var hasOptimizedSettings) && hasOptimizedSettings;
            var advanced = TryGetBoolean(detection, "UseAdvanced", out var useAdvanced)
                && useAdvanced;
            algorithm["StarDetectionMode"] = advanced
                ? "Advanced"
                : optimized && hasOptimized ? "Optimized" : "Simple";
        }

        if (algorithm.Count > 0)
        {
            projected["HocusFocusAlgorithm"] = algorithm;
        }

        if (TryGetBoolean(source, "InitialHFRMeasured", out var measured))
        {
            projected["InitialHFRMeasured"] = measured;
        }
        if (TryGetProperty(source, "FinalHFRSource", out var finalSource)
            && finalSource.ValueKind == JsonValueKind.String
            && finalSource.GetString() is "measured_validation" or "fitted_estimate")
        {
            projected["FinalHFRSource"] = finalSource.GetString();
        }
    }

    private static void CopyOptionalBoolean(
        JsonElement source,
        JsonObject projected,
        string name)
    {
        if (TryGetBoolean(source, name, out var value))
        {
            projected[name] = value;
        }
    }

    private static void CopyOptionalString(
        JsonElement source,
        JsonObject projected,
        string name)
    {
        if (TryGetProperty(source, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            projected[name] = value.GetString();
        }
    }

    private static bool TryGetBoolean(JsonElement source, string name, out bool value)
    {
        if (TryGetProperty(source, name, out var property))
        {
            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.GetBoolean();
                return true;
            }
            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void CopyOptionalFiniteNumber(
        JsonElement source,
        JsonObject projected,
        string name)
    {
        if (TryGetProperty(source, name, out var value)
            && TryGetFiniteDouble(value, out var number))
        {
            projected[name] = number;
        }
    }

    private static JsonElement RequiredObject(JsonElement source, string name) =>
        OptionalObject(source, name)
        ?? throw new JsonException($"The autofocus report has no {name} object.");

    private static JsonElement? OptionalObject(JsonElement source, string name) =>
        TryGetProperty(source, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonNode IntegerProperty(JsonElement source, string name, int fallback)
    {
        if (TryGetProperty(source, name, out var value))
        {
            if (value.TryGetInt32(out var integer))
            {
                return JsonValue.Create(integer)!;
            }
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out integer))
            {
                return JsonValue.Create(integer)!;
            }
        }
        return JsonValue.Create(fallback)!;
    }

    private static JsonNode RequiredFiniteNumberProperty(JsonElement source, string name)
    {
        if (TryGetProperty(source, name, out var value)
            && TryGetFiniteDouble(value, out var number))
        {
            return JsonValue.Create(number)!;
        }
        throw new JsonException($"The autofocus report has no finite {name} value.");
    }

    private static JsonNode TemperatureProperty(
        JsonElement source,
        DirectAutofocusCompletion? completion)
    {
        if (TryGetProperty(source, "Temperature", out _))
        {
            return NumberOrNamedProperty(source, "Temperature", "NaN");
        }
        return completion is not null && double.IsFinite(completion.Temperature)
            ? JsonValue.Create(completion.Temperature)!
            : JsonValue.Create("NaN")!;
    }

    private static JsonNode NumberOrNamedProperty(
        JsonElement source,
        string name,
        string fallback)
    {
        if (TryGetProperty(source, name, out var value))
        {
            if (TryGetFiniteDouble(value, out var number))
            {
                return JsonValue.Create(number)!;
            }
            if (value.ValueKind == JsonValueKind.String
                && value.GetString() is string named
                && (named.Equals("NaN", StringComparison.OrdinalIgnoreCase)
                    || named.Equals("Infinity", StringComparison.OrdinalIgnoreCase)
                    || named.Equals("-Infinity", StringComparison.OrdinalIgnoreCase)))
            {
                return JsonValue.Create(named)!;
            }
        }
        return JsonValue.Create(fallback)!;
    }

    private static bool TryGetFiniteDouble(JsonElement value, out double result)
    {
        if ((value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
            || (value.ValueKind == JsonValueKind.String
                && double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result)))
        {
            return double.IsFinite(result);
        }
        result = default;
        return false;
    }

    private static JsonNode StringProperty(
        JsonElement source,
        string name,
        string fallback)
    {
        if (TryGetProperty(source, name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return JsonValue.Create(value.GetString() ?? fallback)!;
        }
        return JsonValue.Create(fallback)!;
    }

    private static JsonNode RequiredStringProperty(JsonElement source, string name)
    {
        if (TryGetProperty(source, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return JsonValue.Create(value.GetString())!;
        }
        throw new JsonException($"The autofocus report has no {name} string.");
    }

    private static bool TryGetProperty(
        JsonElement source,
        string name,
        out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty(name, out value))
            {
                return true;
            }
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
