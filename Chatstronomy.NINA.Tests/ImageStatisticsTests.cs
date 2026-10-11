using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using NINA.Image.ImageData;
using NINA.WPF.Base.Interfaces.Mediator;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Chatstronomy.NINA.Tests;

internal static class ImageStatisticsTests
{
    internal static async Task RunAsync()
    {
        CaptureContextPreservesUnitsAndMissingReadings();
        CaptureTimestampIsZonedAndLegacyDateIsUnchanged();
        SettingsPersistPerProfileAndHaveVisibleControls();
        LegacyMetadataAndEnvelopesOmitNewFields();
        await SavedImageCarriesCaptureContextAndPrivacyPolicy();
    }

    private static void CaptureContextPreservesUnitsAndMissingReadings()
    {
        var empty = new DirectImageCaptureContext(null, null, null, null, null, null);
        Require(DirectImageCaptureContext.FromMetadata(null) == empty,
            "Missing metadata must not become zero-valued context.");
        Require(DirectImageCaptureContext.FromMetadata(new ImageMetaData()) == empty,
            "N.I.N.A.'s default NaN/null readings must remain unavailable.");
        var metadata = new ImageMetaData();
        metadata.Focuser.Position = 30105;
        metadata.Focuser.Temperature = 23.3;
        metadata.WeatherData.Temperature = -2.5;
        metadata.WeatherData.DewPoint = -5.1;
        metadata.WeatherData.WindSpeed = 3.2;
        metadata.WeatherData.WindGust = 5.4;
        Require(DirectImageCaptureContext.FromMetadata(metadata)
            == new DirectImageCaptureContext(23.3, 30105, -2.5, -5.1, 3.2, 5.4),
            "Captured context must retain Celsius, steps, and m/s without unit conversion.");
        metadata.Focuser.Position = -1;
        metadata.Focuser.Temperature = double.PositiveInfinity;
        metadata.WeatherData.Temperature = double.NegativeInfinity;
        metadata.WeatherData.DewPoint = double.NaN;
        metadata.WeatherData.WindSpeed = -1;
        metadata.WeatherData.WindGust = double.PositiveInfinity;
        Require(DirectImageCaptureContext.FromMetadata(metadata) == empty,
            "Invalid/unavailable context must never be advertised as a real measurement.");
        metadata.Focuser.Position = 0;
        metadata.Focuser.Temperature = 0;
        metadata.WeatherData.Temperature = 0;
        metadata.WeatherData.DewPoint = 0;
        metadata.WeatherData.WindSpeed = 0;
        metadata.WeatherData.WindGust = 0;
        Require(DirectImageCaptureContext.FromMetadata(metadata)
            == new DirectImageCaptureContext(0, 0, 0, 0, 0, 0),
            "Actual zero readings are valid and must not be omitted.");
    }

    private static void CaptureTimestampIsZonedAndLegacyDateIsUnchanged()
    {
        var savedAt = new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.FromHours(-7));
        var metadata = new ImageMetaData();
        Require(DirectImageCaptureContext.Timestamp(metadata, savedAt) == savedAt,
            "Missing exposure time must fall back to the zoned save time.");
        Require(!DirectImageCaptureContext.HasExposureStartTimestamp(metadata),
            "A fallback save time must not claim to be an exposure start.");
        metadata.Image.ExposureStart = new DateTime(2026, 10, 10, 22, 55, 0, DateTimeKind.Utc);
        var timestamp = DirectImageCaptureContext.Timestamp(metadata, savedAt);
        Require(timestamp == new DateTimeOffset(metadata.Image.ExposureStart)
            && timestamp.Offset == TimeSpan.Zero
            && DirectImageCaptureContext.HasExposureStartTimestamp(metadata),
            "A known capture instant must be normalized to UTC.");
        metadata.Image.ExposureStart = DateTime.SpecifyKind(
            metadata.Image.ExposureStart, DateTimeKind.Unspecified);
        Require(DirectImageCaptureContext.Timestamp(metadata, savedAt) == savedAt,
            "An unzoned third-party capture time must not be guessed.");
        Require(!DirectImageCaptureContext.HasExposureStartTimestamp(metadata),
            "An unzoned capture time must not imply reliable exposure provenance.");
        foreach (var unavailable in new[] { DateTime.MinValue, DateTime.MaxValue })
        {
            metadata.Image.ExposureStart = unavailable;
            Require(DirectImageCaptureContext.Timestamp(metadata, savedAt) == savedAt
                && !DirectImageCaptureContext.HasExposureStartTimestamp(metadata),
                "Unset/invalid exposure sentinels must use explicit save-time provenance.");
        }
        Require(DirectProtocol.CurrentVersion == 1 && DirectProtocol.CurrentPayloadVersion == 3,
            "The additive extensions must not change the Direct handshake version.");
    }

    private static void SettingsPersistPerProfileAndHaveVisibleControls()
    {
        var service = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfileService,
            TestProfileService>();
        var state = (TestProfileService)(object)service;
        var first = CreateProfile();
        state.ActiveProfile = first;
        var settings = new ChatstronomySettings(service);
        Require(settings.ImageStatisticsOptions == DirectImageStatisticsOptions.Disabled,
            "New profiles must not post additional charts without selecting them.");
        settings.ImageStatisticsEveryNFrames = 12;
        settings.ImageStatisticsOnSequenceEnd = true;
        var reopened = new ChatstronomySettings(service);
        Require(reopened.ImageStatisticsOptions == new DirectImageStatisticsOptions(12, true),
            "Both chart choices must persist in the N.I.N.A. profile.");
        state.ActiveProfile = CreateProfile();
        Require(settings.ImageStatisticsOptions == DirectImageStatisticsOptions.Disabled,
            "Chart configuration must not leak into another profile.");
        state.ActiveProfile = first;
        Require(settings.ImageStatisticsEveryNFrames == 12 && settings.ImageStatisticsOnSequenceEnd,
            "Returning to a profile must restore its own settings.");
        settings.ImageStatisticsEveryNFrames = -1;
        Require(settings.ImageStatisticsEveryNFrames == 0,
            "A negative interval must disable periodic charts.");
        settings.ImageStatisticsEveryNFrames = int.MaxValue;
        Require(settings.ImageStatisticsEveryNFrames == 10_000,
            "A malformed interval must be bounded.");

        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var (setting, control, bindingAttribute) in new[]
        {
            (nameof(ChatstronomySettings.ImageStatisticsEveryNFrames), "TextBox", "Text"),
            (nameof(ChatstronomySettings.ImageStatisticsOnSequenceEnd), "CheckBox", "IsChecked"),
        })
        {
            var element = document.Descendants(presentation + control).Single(element =>
                ((string?)element.Attribute(bindingAttribute))?.StartsWith(
                    $"{{Binding {setting}", StringComparison.Ordinal) == true);
            Require(element.Parent!.Elements(presentation + "TextBlock").Any(),
                $"{setting} must have a visible label, not Checkbox.Content.");
            Require(element.Ancestors().Any(ancestor =>
                (string?)ancestor.Attribute("IsEnabled") == "{Binding SendImageEvents}"),
                $"{setting} must be gated by image sharing.");
            var property = typeof(ChatstronomyPlugin).GetProperty(setting);
            Require(property?.CanRead == true && property.CanWrite,
                $"{setting} must have a writable view-model property.");
        }
    }

    private static void LegacyMetadataAndEnvelopesOmitNewFields()
    {
        var image = new DirectImageMetadata(30, "LIGHT", "L", "", -10, "Camera", 0, 0,
            DateTime.UtcNow, "Telescope", 500, 1, 2, 3, 30, 1.5, false, true);
        var envelope = DirectApiEnvelope<DirectImageMetadata[]>.Ok([image]);
        var json = JsonSerializer.SerializeToElement(envelope, DirectProtocol.JsonOptions);
        Require(!json.TryGetProperty("ImageStatistics", out _),
            "Other/legacy envelope constructors must not advertise image statistics.");
        var encodedImage = json.GetProperty("Response")[0];
        foreach (var name in new[] { "FocuserTemperature", "FocuserPosition", "WeatherTemperature",
            "DewPoint", "WindSpeed", "WindGust", "StarDetectionAvailable", "Timestamp",
            "TimestampIsExposureStart" })
        {
            Require(!encodedImage.TryGetProperty(name, out _),
                $"A missing {name} must be omitted from the wire, not emitted as zero.");
        }
        var decoded = encodedImage.Deserialize<DirectImageMetadata>(DirectProtocol.JsonOptions)!;
        Require(decoded.FocuserPosition is null && decoded.StarDetectionAvailable is null,
            "Old image payloads must remain deserializable without the new fields.");
    }

    private static async Task SavedImageCarriesCaptureContextAndPrivacyPolicy()
    {
        var policy = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        var options = new DirectImageStatisticsOptions(5, true);
        var savedAt = DateTimeOffset.UtcNow;
        using var provider = new NinaDirectDataProvider(
            profileService: null!, telescope: null!, camera: null!, filterWheel: null!,
            guider: null!, rotator: null!, focuser: null!, sequence: null!, safetyMonitor: null!,
            dome: null!, flatDevice: null!, weatherData: null!, switchMediator: null!, imageSave: null!,
            applicationStatus: null!, autoFocusFactory: null!, imageHistory: null!, windowFactory: null!,
            messageBroker: null!, eventDelivery: policy,
            accessPolicy: new DirectAccessPolicy(DirectAccessOptions.Default),
            utcNow: () => savedAt, imageStatisticsOptions: () => options);
        var metadata = new ImageMetaData();
        metadata.Image.ImageType = "LIGHT";
        metadata.Focuser.Position = 12345;
        metadata.Focuser.Temperature = 5.5;
        metadata.WeatherData.Temperature = 6.5;
        metadata.WeatherData.DewPoint = 3.5;
        metadata.WeatherData.WindSpeed = 1.5;
        metadata.WeatherData.WindGust = 2.5;
        var callback = typeof(NinaDirectDataProvider).GetMethod("ImageSaved",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        callback.Invoke(provider, [null, new ImageSavedEventArgs { MetaData = metadata, Duration = 30 }]);
        // Mutating the original N.I.N.A. object after save must not change our
        // snapshot or perform any live driver reads during the query.
        metadata.Focuser.Position = 54321;
        metadata.WeatherData.WindSpeed = 99;
        var result = await ReadImages(provider);
        var image = result.GetProperty("Response")[0];
        Require(image.GetProperty("FocuserPosition").GetInt32() == 12345
            && image.GetProperty("WindSpeed").GetDouble() == 1.5,
            "Saved capture context must be an immutable primitive snapshot.");
        Require(!image.GetProperty("StarDetectionAvailable").GetBoolean(),
            "No star analysis must be distinguishable from a real zero-star frame.");
        Require(image.GetProperty("Timestamp").GetDateTimeOffset() == savedAt,
            "New images must carry a zoned timestamp without changing legacy Date.");
        Require(!image.GetProperty("TimestampIsExposureStart").GetBoolean(),
            "Save-time timestamps must explicitly identify their weaker provenance.");
        Require(result.GetProperty("ImageStatistics").GetProperty("EveryNFrames").GetInt32() == 5
            && result.GetProperty("ImageStatistics").GetProperty("OnSequenceEnd").GetBoolean(),
            "Image history must carry both configured chart settings.");

        metadata.Image.ExposureStart = savedAt.UtcDateTime.AddSeconds(-30);
        callback.Invoke(provider, [null, new ImageSavedEventArgs
        {
            MetaData = metadata,
            Duration = 30,
            StarDetectionAnalysis = DispatchProxy.Create<global::NINA.Image.Interfaces.IStarDetectionAnalysis,
                ZeroStarAnalysis>(),
        }]);
        result = await ReadImages(provider);
        var zeroStarImage = result.GetProperty("Response")[1];
        Require(zeroStarImage.GetProperty("StarDetectionAvailable").GetBoolean()
            && zeroStarImage.GetProperty("Stars").GetInt32() == 0,
            "A measured zero-star frame must remain distinguishable from no analysis.");
        Require(zeroStarImage.GetProperty("TimestampIsExposureStart").GetBoolean()
            && zeroStarImage.GetProperty("Timestamp").GetDateTimeOffset()
                == new DateTimeOffset(metadata.Image.ExposureStart),
            "A known exposure start must retain its exact instant and provenance.");

        policy.Update(policy.Current with { Sequence = false });
        result = await ReadImages(provider);
        Require(!result.GetProperty("ImageStatistics").GetProperty("OnSequenceEnd").GetBoolean(),
            "A disabled sequence category must suppress sequence-end charts.");
        policy.Update(policy.Current with { Images = false });
        result = await ReadImages(provider);
        Require(result.GetProperty("Response").GetArrayLength() == 0,
            "Turning off images must withhold environmental context too.");
        Require(result.GetProperty("ImageStatistics").GetProperty("EveryNFrames").GetInt32() == 0
            && !result.GetProperty("ImageStatistics").GetProperty("OnSequenceEnd").GetBoolean(),
            "Turning off images must revoke chart delivery, not just thumbnails.");
        callback.Invoke(provider, [null, new ImageSavedEventArgs { MetaData = metadata, Duration = 30 }]);
        policy.Update(policy.Current with { Images = true });
        Require((await ReadImages(provider)).GetProperty("Response").GetArrayLength() == 2,
            "A frame captured without image consent must never become shareable later.");
        options = DirectImageStatisticsOptions.Disabled;
        Require((await ReadImages(provider)).GetProperty("ImageStatistics")
            .GetProperty("EveryNFrames").GetInt32() == 0,
            "Chart settings must be read live rather than captured at plugin startup.");
    }

    private static async Task<JsonElement> ReadImages(NinaDirectDataProvider provider) =>
        JsonSerializer.SerializeToElement(await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.ImageHistory), CancellationToken.None),
            DirectProtocol.JsonOptions);

    private static global::NINA.Profile.Interfaces.IProfile CreateProfile()
    {
        var profile = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfile, TestProfile>();
        var state = (TestProfile)(object)profile;
        state.Id = Guid.NewGuid();
        state.PluginSettings = new global::NINA.Profile.PluginSettings();
        return profile;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private class TestProfile : DispatchProxy
    {
        internal Guid Id { get; set; }
        internal global::NINA.Profile.Interfaces.IPluginSettings PluginSettings { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_Id" => Id,
            "get_PluginSettings" => PluginSettings,
            "Dispose" => null,
            _ => throw new NotSupportedException($"Unexpected profile operation {method?.Name}."),
        };
    }

    private class TestProfileService : DispatchProxy
    {
        internal global::NINA.Profile.Interfaces.IProfile ActiveProfile { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_ActiveProfile" => ActiveProfile,
            _ => throw new NotSupportedException($"Unexpected profile-service operation {method?.Name}."),
        };
    }

    private class ZeroStarAnalysis : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_DetectedStars" => (object)0,
            "get_HFR" => 0d,
            _ => throw new NotSupportedException($"Unexpected star-analysis operation {method?.Name}."),
        };
    }
}
