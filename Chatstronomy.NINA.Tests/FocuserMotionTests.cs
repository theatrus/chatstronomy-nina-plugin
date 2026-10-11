using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using NINA.Equipment.Equipment.MyFocuser;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chatstronomy.NINA.Tests;

internal static partial class Program
{
    private static async Task FocuserMotionCapturesState()
    {
        FocuserMotionSettingsPersistPerProfile();
        FocuserMotionFixturePreservesAdditiveContract();
        AssertFalse(DirectEventDeliveryOptions.Default.FocuserMotion);
        AssertFalse(DirectEventDeliveryOptions.Default.ShouldSendEvent("FOCUSER-MOVED"));
        AssertTrue((DirectEventDeliveryOptions.Default with { FocuserMotion = true })
            .ShouldSendEvent("FOCUSER-MOVED"));

        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default), deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            var info = new FocuserInfo
            {
                Connected = true,
                Position = 100,
                Temperature = 12.5,
            };
            void Observe(int position, bool moving = false, bool settling = false,
                double temperature = 12.5)
            {
                info.Position = position;
                info.IsMoving = moving;
                info.IsSettling = settling;
                info.Temperature = temperature;
                provider.UpdateDeviceInfo(info);
            }
            async Task<JsonElement[]> Moves() => (await SnapshotEvents(provider))
                .Where(item => item.GetProperty("Event").GetString() == "FOCUSER-MOVED")
                .ToArray();

            // Native sequencer actions do not necessarily raise UserFocused.
            // Position-only polling must recover their completed short moves.
            Observe(100);
            Observe(110);
            Observe(110);
            AssertEqual(0, (await Moves()).Length);
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { FocuserMotion = true });
            Observe(110);
            Observe(110);
            AssertEqual(0, (await Moves()).Length);
            Observe(120);
            AssertEqual(0, (await Moves()).Length);
            Observe(120);
            var first = (await Moves()).Single();
            AssertEqual(110, first.GetProperty("From").GetInt32());
            AssertEqual(120, first.GetProperty("To").GetInt32());
            AssertEqual(12.5, first.GetProperty("Temperature").GetDouble());
            AssertFalse(first.GetProperty("ObservedInProgress").GetBoolean());
            AssertEqual("position_change", first.GetProperty("EndDetection").GetString());

            // The queued history owns primitives, not the mutable device info.
            info.Position = 999;
            info.Temperature = 99;
            var captured = (await Moves()).Single();
            AssertEqual(120, captured.GetProperty("To").GetInt32());
            AssertEqual(12.5, captured.GetProperty("Temperature").GetDouble());

            Observe(125, moving: true);
            Observe(130, settling: true, temperature: 11);
            Observe(130, temperature: 10.5);
            AssertEqual(1, (await Moves()).Length);
            Observe(130, temperature: 10.5);
            var observed = (await Moves())[1];
            AssertEqual(120, observed.GetProperty("From").GetInt32());
            AssertEqual(130, observed.GetProperty("To").GetInt32());
            AssertEqual(10.5, observed.GetProperty("Temperature").GetDouble());
            AssertTrue(observed.GetProperty("ObservedInProgress").GetBoolean());
            AssertEqual("motion_state", observed.GetProperty("EndDetection").GetString());
            Observe(130, temperature: 9);
            Observe(130, temperature: 8);
            AssertEqual(2, (await Moves()).Length);

            // Toggling a different category must not break a visible motion.
            Observe(135, moving: true);
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { Guiding = !delivery.Current.Guiding });
            Observe(140, temperature: double.NaN);
            Observe(140, temperature: double.NaN);
            var unrelatedChange = (await Moves())[^1];
            AssertEqual(3, (await Moves()).Length);
            AssertEqual(130, unrelatedChange.GetProperty("From").GetInt32());
            AssertEqual(140, unrelatedChange.GetProperty("To").GetInt32());
            AssertFalse(unrelatedChange.TryGetProperty("Temperature", out _));

            // Hidden moves may neither appear on re-enable nor contribute a
            // predecessor endpoint to a subsequently permitted movement.
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { FocuserMotion = false });
            Observe(160, moving: true);
            Observe(180);
            Observe(180);
            AssertEqual(0, (await Moves()).Length);
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { FocuserMotion = true });
            Observe(180);
            Observe(180);
            AssertEqual(0, (await Moves()).Length);
            Observe(181);
            Observe(181);
            var afterPrivateGap = (await Moves()).Single();
            AssertEqual(180, afterPrivateGap.GetProperty("From").GetInt32());
            AssertEqual(181, afterPrivateGap.GetProperty("To").GetInt32());

            // A move already in progress across off/on is private even if no
            // update callback happened while sharing was disabled.
            Observe(185, moving: true);
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { FocuserMotion = false });
            ApplyEventDeliveryChange(provider, delivery,
                delivery.Current with { FocuserMotion = true });
            Observe(190, moving: true);
            Observe(192);
            Observe(192);
            AssertEqual(0, (await Moves()).Length);
            Observe(193);
            Observe(193);
            var afterCrossingMove = (await Moves()).Single();
            AssertEqual(192, afterCrossingMove.GetProperty("From").GetInt32());
            AssertEqual(193, afterCrossingMove.GetProperty("To").GetInt32());

            // Profile revocation must discard an unfinished predecessor move
            // and rebaseline from the successor's first stable state.
            Observe(195, moving: true);
            provider.RevokeProfileAccess();
            provider.Reset();
            provider.ResumeEventCapture();
            Observe(200);
            Observe(200);
            AssertEqual(0, (await Moves()).Length);
            Observe(201);
            Observe(201);
            var afterProfile = (await Moves()).Single();
            AssertEqual(200, afterProfile.GetProperty("From").GetInt32());
            AssertEqual(201, afterProfile.GetProperty("To").GetInt32());

            // A failed third-party AF may never emit a terminal callback. An
            // AF latch must not permanently suppress subsequent focus moves.
            provider.AutoFocusRunStarting();
            Observe(202);
            Observe(202);
            AssertEqual(2, (await Moves()).Length);
            provider.UpdateUserFocused(info);
            AssertTrue((await SnapshotEvents(provider)).Any(item =>
                item.GetProperty("Event").GetString() == "FOCUSER-USER-FOCUSED"));

            // A replacement device-info object may belong to a newly
            // connected focuser; do not bridge the two devices' positions.
            var replacement = new FocuserInfo { Connected = true, Position = 900 };
            provider.UpdateDeviceInfo(replacement);
            provider.UpdateDeviceInfo(replacement);
            AssertEqual(2, (await Moves()).Length);
            replacement.Position = 901;
            provider.UpdateDeviceInfo(replacement);
            provider.UpdateDeviceInfo(replacement);
            AssertEqual(3, (await Moves()).Length);

            SetProviderStarted(provider, false);
            Observe(205, moving: true);
            Observe(210);
            Observe(210);
            AssertEqual(3, (await Moves()).Length);
        }
        finally
        {
            SetProviderStarted(provider, false);
        }

        await FocuserPolicyPublicationGapsStaySilent();
        await NativeTemperatureFocuserActionPublishesMovement();
    }

    private static async Task FocuserPolicyPublicationGapsStaySilent()
    {
        var enabled = DirectEventDeliveryOptions.Default with { FocuserMotion = true };
        var disabled = enabled with { FocuserMotion = false };
        var delivery = new DirectEventDeliveryPolicy(enabled);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default), deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            var info = new FocuserInfo { Connected = true, Position = 100 };
            async Task<JsonElement[]> Moves() => (await SnapshotEvents(provider))
                .Where(item => item.GetProperty("Event").GetString() == "FOCUSER-MOVED")
                .ToArray();
            provider.UpdateDeviceInfo(info);
            info.Position = 110;
            info.IsMoving = true;
            provider.UpdateDeviceInfo(info);

            // Disable must close capture before the old policy object is
            // replaced. An in-flight callback must not win that window.
            provider.EventDeliveryPolicyChanging(enabled, disabled);
            info.IsMoving = false;
            foreach (var callback in new[] { "FocuserConnected", "FocuserDisconnected" })
            {
                await InvokeProviderCallbackAsync(provider, callback, EventArgs.Empty);
                info.Position += 10;
                provider.UpdateDeviceInfo(info);
                provider.UpdateDeviceInfo(info);
                info.Position += 10;
                provider.UpdateDeviceInfo(info);
                provider.UpdateDeviceInfo(info);
                AssertEqual(0, (await Moves()).Length);
            }
            delivery.Update(disabled);
            provider.EventDeliveryPolicyChanged(enabled, disabled);

            // Conversely, publishing an enable value is not sufficient until
            // the post-publication hook opens its new observation baseline.
            provider.EventDeliveryPolicyChanging(disabled, enabled);
            delivery.Update(enabled);
            foreach (var callback in new[] { "FocuserConnected", "FocuserDisconnected" })
            {
                await InvokeProviderCallbackAsync(provider, callback, EventArgs.Empty);
                info.Position += 10;
                provider.UpdateDeviceInfo(info);
                provider.UpdateDeviceInfo(info);
                info.Position += 10;
                provider.UpdateDeviceInfo(info);
                provider.UpdateDeviceInfo(info);
                AssertEqual(0, (await Moves()).Length);
            }
            provider.EventDeliveryPolicyChanged(disabled, enabled);
            info.Position = 140;
            provider.UpdateDeviceInfo(info);
            provider.UpdateDeviceInfo(info);
            AssertEqual(0, (await Moves()).Length);
            info.Position = 150;
            provider.UpdateDeviceInfo(info);
            provider.UpdateDeviceInfo(info);
            var captured = (await Moves()).Single();
            AssertEqual("FOCUSER-MOVED", captured.GetProperty("Event").GetString());
            AssertEqual(140, captured.GetProperty("From").GetInt32());
            AssertEqual(150, captured.GetProperty("To").GetInt32());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task NativeTemperatureFocuserActionPublishesMovement()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            delivery: DirectEventDeliveryOptions.Default with { FocuserMotion = true });
        SetProviderStarted(provider, true);
        try
        {
            var info = new FocuserInfo
            {
                Connected = true,
                Position = 1000,
                Temperature = 12.5,
            };
            provider.UpdateDeviceInfo(info);
            var mediator = DispatchProxy.Create<
                global::NINA.Equipment.Interfaces.Mediator.IFocuserMediator,
                TemperatureMoveFocuserProxy>();
            var mediatorStub = (TemperatureMoveFocuserProxy)(object)mediator;
            mediatorStub.Info = info;
            mediatorStub.OnMove = position =>
            {
                info.IsMoving = true;
                provider.UpdateDeviceInfo(info);
                info.Position = position;
                provider.UpdateDeviceInfo(info);
                info.IsMoving = false;
                info.IsSettling = true;
                provider.UpdateDeviceInfo(info);
                info.IsSettling = false;
                provider.UpdateDeviceInfo(info);
                provider.UpdateDeviceInfo(info);
            };
            // Execute N.I.N.A.'s actual action, rather than reproducing its
            // temperature-compensation calculation in the test or plugin.
            var action = new global::NINA.Sequencer.SequenceItem.Focuser.MoveFocuserByTemperature(
                mediator)
            {
                Absolute = true,
                Slope = 10,
                Intercept = 1000,
            };
            await action.Execute(new Progress<global::NINA.Core.Model.ApplicationStatus>(),
                CancellationToken.None);
            AssertEqual(1, mediatorStub.MoveCount);
            AssertEqual(1125, mediatorStub.RequestedPosition);
            var events = await SnapshotEvents(provider);
            var move = events.Single(item => item.GetProperty("Event").GetString() == "FOCUSER-MOVED");
            AssertEqual(1000, move.GetProperty("From").GetInt32());
            AssertEqual(1125, move.GetProperty("To").GetInt32());
            AssertEqual(12.5, move.GetProperty("Temperature").GetDouble());
            AssertTrue(move.GetProperty("ObservedInProgress").GetBoolean());
            AssertEqual("motion_state", move.GetProperty("EndDetection").GetString());
            AssertFalse(events.Any(item => item.GetProperty("Event").GetString()
                == "FOCUSER-USER-FOCUSED"));
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private class TemperatureMoveFocuserProxy : DispatchProxy
    {
        internal FocuserInfo Info { get; set; } = null!;
        internal Action<int> OnMove { get; set; } = null!;
        internal int MoveCount { get; private set; }
        internal int RequestedPosition { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == "GetInfo")
            {
                return Info;
            }
            if (method?.Name == "MoveFocuser")
            {
                ((CancellationToken)arguments![1]!).ThrowIfCancellationRequested();
                RequestedPosition = (int)arguments[0]!;
                MoveCount++;
                OnMove(RequestedPosition);
                return Task.FromResult(RequestedPosition);
            }
            throw new NotSupportedException($"Unexpected temperature focuser operation '{method?.Name}'.");
        }
    }

    private static void FocuserMotionSettingsPersistPerProfile()
    {
        static global::NINA.Profile.Interfaces.IProfile Profile()
        {
            var profile = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfile,
                ActiveProfileProxy>();
            var stub = (ActiveProfileProxy)(object)profile;
            stub.Id = Guid.NewGuid();
            stub.PluginSettings = new global::NINA.Profile.PluginSettings();
            return profile;
        }

        var first = Profile();
        var second = Profile();
        var service = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfileService,
            ActiveProfileServiceProxy>();
        var serviceStub = (ActiveProfileServiceProxy)(object)service;
        serviceStub.ActiveProfile = first;
        var settings = new ChatstronomySettings(service);
        AssertFalse(settings.SendFocuserMoveEvents);
        AssertFalse(settings.EventDeliveryOptions.FocuserMotion);
        settings.SendFocuserMoveEvents = true;
        settings.SendFilterFocuserRotatorEvents = false;
        var reloaded = new ChatstronomySettings(service);
        AssertTrue(reloaded.SendFocuserMoveEvents);
        AssertTrue(reloaded.EventDeliveryOptions.FocuserMotion);
        AssertTrue(reloaded.EventDeliveryOptions.ShouldSendEvent("FOCUSER-MOVED"));
        AssertFalse(reloaded.EventDeliveryOptions.ShouldSendEvent("FOCUSER-USER-FOCUSED"));

        serviceStub.ActiveProfile = second;
        AssertFalse(settings.SendFocuserMoveEvents);
        AssertFalse(settings.EventDeliveryOptions.FocuserMotion);
        AssertTrue(settings.SendFilterFocuserRotatorEvents);
        settings.SendFocuserMoveEvents = true;
        serviceStub.ActiveProfile = first;
        AssertTrue(settings.SendFocuserMoveEvents);
        AssertFalse(settings.SendFilterFocuserRotatorEvents);
        settings.SendFocuserMoveEvents = false;
        serviceStub.ActiveProfile = second;
        AssertTrue(settings.SendFocuserMoveEvents);
    }

    private static void FocuserMotionFixturePreservesAdditiveContract()
    {
        var contracts = Environment.GetEnvironmentVariable("CHATSTRONOMY_CONTRACTS_DIR");
        if (string.IsNullOrWhiteSpace(contracts))
        {
            return;
        }
        var fixturePath = Path.Combine(contracts, "direct", "v1", "fixtures",
            "query-result-focuser-moved.json");
        if (!File.Exists(fixturePath))
        {
            // A previously released backend contract remains a supported pin.
            return;
        }
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        AssertEqual("query_result", fixture.RootElement.GetProperty("type").GetString());
        var payload = fixture.RootElement.GetProperty("payload");
        var response = payload.GetProperty("payload");
        using var encoded = JsonDocument.Parse(DirectProtocol.SerializeSuccess(
            payload.GetProperty("id").GetGuid(), response));
        AssertTrue(JsonNode.DeepEquals(JsonNode.Parse(response.GetRawText()),
            JsonNode.Parse(encoded.RootElement.GetProperty("payload")
                .GetProperty("payload").GetRawText())));
        var events = response.GetProperty("Response").EnumerateArray().ToArray();
        AssertEqual(3, events.Length);
        AssertEqual("FOCUSER-MOVED", events[0].GetProperty("Event").GetString());
        AssertEqual(30105, events[0].GetProperty("From").GetInt32());
        AssertEqual(30137, events[0].GetProperty("To").GetInt32());
        AssertEqual(9.4, events[0].GetProperty("Temperature").GetDouble());
        AssertTrue(events[0].GetProperty("ObservedInProgress").GetBoolean());
        AssertEqual("motion_state", events[0].GetProperty("EndDetection").GetString());
        AssertEqual("FOCUSER-MOVED", events[1].GetProperty("Event").GetString());
        AssertFalse(events[1].TryGetProperty("Temperature", out _));
        AssertFalse(events[1].GetProperty("ObservedInProgress").GetBoolean());
        AssertEqual("position_change", events[1].GetProperty("EndDetection").GetString());
        AssertEqual("FOCUSER-USER-FOCUSED", events[2].GetProperty("Event").GetString());
        AssertFalse(events[2].TryGetProperty("From", out _));
        AssertFalse(events[2].TryGetProperty("To", out _));
    }

    private static void FocuserMotionTrackerHandlesEdges()
    {
        var tracker = new DirectFocuserMotionTracker();
        DirectFocuserMovement? Observe(int? position, bool moving = false,
            bool settling = false, double? temperature = 12.5, bool connected = true) =>
            tracker.Observe(new DirectFocuserMotionSnapshot(
                connected, position, temperature, moving, settling));
        static void Silent(DirectFocuserMovement? value) => AssertTrue(value is null);
        static DirectFocuserMovement Movement(DirectFocuserMovement? value) => value
            ?? throw new InvalidOperationException("Expected a stable completed focuser move.");

        // First idle observation is a baseline, never a synthetic completion.
        Silent(Observe(100));
        Silent(Observe(100, temperature: 10));
        Silent(Observe(100, moving: true));
        Silent(Observe(110, moving: true));
        Silent(Observe(110));
        Silent(Observe(110, settling: true));
        Silent(Observe(110));
        var settled = Movement(Observe(110));
        AssertEqual<int?>(100, settled.From);
        AssertEqual(110, settled.To);
        AssertTrue(settled.ObservedInProgress);
        AssertEqual("motion_state", settled.EndDetection);

        // A move shorter than the device update interval still has an honest
        // endpoint, without claiming to have observed motion in progress.
        Silent(Observe(120));
        var shortMove = Movement(Observe(120));
        AssertEqual<int?>(110, shortMove.From);
        AssertEqual(120, shortMove.To);
        AssertFalse(shortMove.ObservedInProgress);
        AssertEqual("position_change", shortMove.EndDetection);

        // Settling alone still delays the endpoint, but does not prove that
        // an actual moving sample was observed.
        Silent(Observe(122, settling: true));
        Silent(Observe(122));
        var settlingOnly = Movement(Observe(122));
        AssertEqual<int?>(120, settlingOnly.From);
        AssertEqual(122, settlingOnly.To);
        AssertFalse(settlingOnly.ObservedInProgress);
        AssertEqual("position_change", settlingOnly.EndDetection);

        // An idle glimpse between backlash legs does not emit an intermediate
        // endpoint; resumed movement resets the consecutive idle counter.
        Silent(Observe(125, moving: true));
        Silent(Observe(125));
        Silent(Observe(123, moving: true));
        Silent(Observe(121, settling: true));
        Silent(Observe(121));
        var backlash = Movement(Observe(121));
        AssertEqual<int?>(122, backlash.From);
        AssertEqual(121, backlash.To);
        AssertTrue(backlash.ObservedInProgress);
        Silent(Observe(121));

        // Busy/no-op operations and sensor-only changes never create moves.
        Silent(Observe(121, moving: true));
        Silent(Observe(121));
        Silent(Observe(121));
        Silent(Observe(121, temperature: 0));
        Silent(Observe(121, temperature: -5));

        // Every changing idle position restarts stabilization, rather than
        // counting unrelated endpoints as two stable samples.
        Silent(Observe(130));
        Silent(Observe(131));
        var changedIdle = Movement(Observe(131));
        AssertEqual<int?>(121, changedIdle.From);
        AssertEqual(131, changedIdle.To);

        // Invalid or disconnected snapshots sever provenance. The next valid
        // reading only establishes a new connected baseline.
        foreach (var invalid in new[]
        {
            new DirectFocuserMotionSnapshot(false, 150, 12, false, false),
            new DirectFocuserMotionSnapshot(true, null, 12, false, false),
            new DirectFocuserMotionSnapshot(true, -1, 12, false, false),
        })
        {
            Silent(Observe(140, moving: true));
            Silent(tracker.Observe(invalid));
            Silent(Observe(200));
            Silent(Observe(200));
            Silent(Observe(201));
            var successor = Movement(Observe(201));
            AssertEqual<int?>(200, successor.From);
            AssertEqual(201, successor.To);
            AssertFalse(successor.ObservedInProgress);
        }

        tracker.Reset();
        Silent(Observe(0, temperature: double.NaN));
        Silent(Observe(1, temperature: double.PositiveInfinity));
        var nonFinite = Movement(Observe(1, temperature: double.NegativeInfinity));
        AssertEqual<int?>(0, nonFinite.From);
        AssertTrue(nonFinite.Temperature is null);
        Silent(Observe(0, temperature: 0));
        var realZero = Movement(Observe(0, temperature: 0));
        AssertEqual(0, realZero.To);
        AssertEqual<double?>(0, realZero.Temperature);

        tracker.Reset();
        Silent(Observe(500, moving: true));
        Silent(Observe(600, settling: true));
        Silent(Observe(600));
        Silent(Observe(600));
        Silent(Observe(601));
        var afterMidMoveStart = Movement(Observe(601));
        AssertEqual<int?>(600, afterMidMoveStart.From);
        AssertFalse(afterMidMoveStart.ObservedInProgress);

        tracker.Reset();
        Silent(Observe(500));
        Silent(Observe(600, moving: true));
        tracker.Reset();
        Silent(Observe(700));
        Silent(Observe(700));
        Silent(Observe(701));
        var afterReset = Movement(Observe(701));
        AssertEqual<int?>(700, afterReset.From);
        AssertEqual(701, afterReset.To);
        AssertFalse(afterReset.ObservedInProgress);
    }
}
