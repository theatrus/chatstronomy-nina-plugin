using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Astrometry;
using NINA.Image.ImageData;
using NINA.Sequencer.Container;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Chatstronomy.NINA.Tests;

internal static class ObservationStateTests
{
    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new DirectObservationState();
        var owner = new object();
        state.BeginTarget(owner, "M31", now, complete: false);
        Check(Read(state, now).GetProperty("completed_exposures").ValueKind == JsonValueKind.Null,
            "Joining mid-target must not invent zero or a partial count.");
        state.BeginTarget(owner, "M31", now, complete: true, force: true);
        var run = Read(state, now).GetProperty("target").GetProperty("run_id").GetGuid();
        state.ImageSaved("M31", "LIGHT", now.AddSeconds(1), now.AddSeconds(30));
        state.ImageSaved("M31", "LIGHT", now.AddSeconds(1), now.AddSeconds(30));
        state.ImageSaved("M31", "FLAT", now.AddSeconds(2), now.AddSeconds(30));
        state.ImageSaved("Other", "LIGHT", now.AddSeconds(3), now.AddSeconds(30));
        Check(Count(Read(state, now.AddSeconds(30))) == 1, "Only distinct, associated saved LIGHTs count.");
        state.BeginTarget(owner, "M31", now.AddSeconds(30), complete: true);
        Check(Read(state, now.AddSeconds(30)).GetProperty("target").GetProperty("run_id").GetGuid() == run,
            "Repeated same-target scheduler observations must not restart the visit.");
        state.BeginTarget(owner, "M31", now.AddSeconds(40), complete: true, force: true);
        state.ImageSaved("M31", "LIGHT", now.AddSeconds(2), now.AddSeconds(45));
        Check(Count(Read(state, now.AddSeconds(45))) == 0, "Late saves from a prior visit cannot cross a reset.");
        Check(Read(state, now.AddSeconds(45)).GetProperty("target").GetProperty("run_id").GetGuid() != run,
            "Returning to the same named target needs a distinct visit ID.");
        state.Temperature(true, 0, now);
        state.Temperature(true, 25, now.AddSeconds(-5));
        state.Temperature(false, double.NaN, now);
        var temperatures = Read(state, now).GetProperty("temperatures");
        Check(temperatures.GetProperty("focuser").GetProperty("celsius").GetDouble() == 0
            && temperatures.GetProperty("external").ValueKind == JsonValueKind.Null,
            "Sensor identity and true zero readings must survive; unknown is null.");
        Check(Read(state, now.AddMinutes(3)).GetProperty("temperatures").GetProperty("focuser").ValueKind == JsonValueKind.Null,
            "Old measurements must expire without being refreshed by polling.");
        var hidden = state.Snapshot(now, false, true, true, true, true);
        Check(!hidden.GetProperty("available").GetBoolean() && hidden.GetProperty("target").ValueKind == JsonValueKind.Null,
            "The local opt-in is an actual transmission boundary.");
        var epoch = Read(state, now).GetProperty("state_epoch").GetGuid();
        state.Reset();
        Check(Read(state, now).GetProperty("state_epoch").GetGuid() != epoch, "Privacy/profile reset rotates state identity.");
        Check(!DirectEventDeliveryOptions.Default.ObservationTelemetry, "Overlay telemetry requires local consent.");
        await NativeCallbacksMaintainAnAuthoritativeCount(now);
    }

    private static async Task NativeCallbacksMaintainAnAuthoritativeCount(DateTimeOffset now)
    {
        var clock = now;
        var policy = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default with { ObservationTelemetry = true });
        using var provider = new NinaDirectDataProvider(
            profileService: null!, telescope: null!, camera: null!, filterWheel: null!, guider: null!,
            rotator: null!, focuser: null!, sequence: null!, safetyMonitor: null!, dome: null!, flatDevice: null!,
            weatherData: null!, switchMediator: null!, imageSave: null!, applicationStatus: null!,
            autoFocusFactory: null!, imageHistory: null!, windowFactory: null!, messageBroker: null!,
            eventDelivery: policy, accessPolicy: new DirectAccessPolicy(DirectAccessOptions.Default), utcNow: () => clock);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var started = typeof(NinaDirectDataProvider).GetField("started", flags)!;
        started.SetValue(provider, true);
        try
        {
            var root = new SequenceRootContainer();
            var input = (InputTarget)RuntimeHelpers.GetUninitializedObject(typeof(InputTarget));
            typeof(InputTarget).GetField("targetName", flags)!.SetValue(input, "M31");
            var target = new TestTarget { Target = input };
            root.Items.Add(target);
            typeof(NinaDirectDataProvider).GetMethod("BindObservationTargets", flags)!.Invoke(provider, [root, null]);
            target.Status = SequenceEntityStatus.RUNNING;
            var metadata = new ImageMetaData();
            metadata.Target.Name = "M31";
            metadata.Image.ImageType = "LIGHT";
            var callback = typeof(NinaDirectDataProvider).GetMethod("ImageSaved", flags)!;
            for (var i = 0; i < 505; i++)
            {
                metadata.Image.ExposureStart = now.AddSeconds(i + 1).UtcDateTime;
                clock = now.AddSeconds(i + 2);
                callback.Invoke(provider, [null, new ImageSavedEventArgs { MetaData = metadata, Duration = 1 }]);
            }
            var query = new DirectQuery(Guid.NewGuid(), DirectQueryKind.ObservationState);
            var first = (JsonElement)(await provider.ExecuteAsync(query, default))!;
            Check(Count(first) == 505, "Counts must not be truncated to the 500-entry image history.");
            provider.RotateDirectSession();
            var reconnect = (JsonElement)(await provider.ExecuteAsync(query, default))!;
            Check(Count(reconnect) == 505 && first.GetProperty("state_epoch").GetGuid() == reconnect.GetProperty("state_epoch").GetGuid(),
                "A transport reconnect must preserve the source-owned counter and epoch.");
            target.Status = SequenceEntityStatus.FINISHED;
            target.Status = SequenceEntityStatus.RUNNING;
            var next = (JsonElement)(await provider.ExecuteAsync(query, default))!;
            Check(Count(next) == 0 && first.GetProperty("target").GetProperty("run_id").GetGuid()
                != next.GetProperty("target").GetProperty("run_id").GetGuid(), "Re-entering a native container starts a new target visit.");
            var second = new TestTarget { Target = input };
            root.Items.Add(second);
            // Rebind a fresh root baseline to exercise ambiguous native targets.
            typeof(NinaDirectDataProvider).GetMethod("ResetObservationState", flags)!.Invoke(provider, null);
            typeof(NinaDirectDataProvider).GetMethod("BindObservationTargets", flags)!.Invoke(provider, [root, null]);
            second.Status = SequenceEntityStatus.RUNNING;
            var parallel = (JsonElement)(await provider.ExecuteAsync(query, default))!;
            Check(parallel.GetProperty("target").ValueKind == JsonValueKind.Null,
                "Parallel targets must not publish whichever target callback happened last.");
            var previous = policy.Current;
            var current = previous with { Images = false };
            provider.EventDeliveryPolicyChanging(previous, current);
            policy.Update(current);
            provider.EventDeliveryPolicyChanged(previous, current);
            var privateState = (JsonElement)(await provider.ExecuteAsync(query, default))!;
            Check(privateState.GetProperty("completed_exposures").ValueKind == JsonValueKind.Null,
                "Disabling image transmission must revoke the saved-frame tally.");
        }
        finally { started.SetValue(provider, false); }
    }

    private sealed class TestTarget : SequentialContainer, IDeepSkyObjectContainer
    {
        public InputTarget Target { get; set; } = null!;
        public NighttimeData NighttimeData => null!;
    }
    private static JsonElement Read(DirectObservationState state, DateTimeOffset now) => state.Snapshot(now, true, true, true, true, true);
    private static ulong Count(JsonElement state) => state.GetProperty("completed_exposures").GetProperty("count").GetUInt64();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
