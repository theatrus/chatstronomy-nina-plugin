using NINA.Image.ImageData;

namespace Chatstronomy.NINA.Direct;

/// Primitive capture-time values already copied by N.I.N.A. into the saved
/// image. Never query a driver or replace a missing reading with zero here.
/// Temperatures are Celsius, focuser position is steps, and wind is m/s.
internal sealed record DirectImageCaptureContext(
    double? FocuserTemperature,
    int? FocuserPosition,
    double? WeatherTemperature,
    double? DewPoint,
    double? WindSpeed,
    double? WindGust)
{
    internal static DirectImageCaptureContext FromMetadata(ImageMetaData? metadata) => new(
        Finite(metadata?.Focuser?.Temperature),
        metadata?.Focuser?.Position is >= 0 ? metadata.Focuser.Position : null,
        Finite(metadata?.WeatherData?.Temperature),
        Finite(metadata?.WeatherData?.DewPoint),
        NonNegative(metadata?.WeatherData?.WindSpeed),
        NonNegative(metadata?.WeatherData?.WindGust));

    private static double? Finite(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    private static double? NonNegative(double? value) =>
        value is >= 0 && double.IsFinite(value.Value) ? value : null;

    internal static DateTimeOffset Timestamp(ImageMetaData? metadata, DateTimeOffset savedAt)
    {
        // A third-party saver may provide an unzoned time. Do not guess its
        // timezone or mislabel the chart; use our zoned callback time instead.
        return HasExposureStartTimestamp(metadata)
                ? new DateTimeOffset(metadata!.Image.ExposureStart.ToUniversalTime())
                : savedAt.ToUniversalTime();
    }

    internal static bool HasExposureStartTimestamp(ImageMetaData? metadata) =>
        metadata?.Image?.ExposureStart is { } start
        && start > DateTime.MinValue
        && start < DateTime.MaxValue
        && start.Kind != DateTimeKind.Unspecified;
}
