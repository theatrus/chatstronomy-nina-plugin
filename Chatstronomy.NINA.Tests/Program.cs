using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Remote;
using Chatstronomy.NINA.Runtime;
using Chatstronomy.NINA.Settings;
using Newtonsoft.Json.Linq;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.ManifestDefinition;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Chatstronomy.NINA.Tests;

internal static class Program
{
    private static int failures;

    public static async Task<int> Main()
    {
        Run("Matrix accepts HTTPS homeservers", MatrixAcceptsHttpsHomeserver);
        Run("Matrix rejects HTTP homeservers", MatrixRejectsHttpHomeserver);
        Run("Discord accepts complete webhook URLs", DiscordAcceptsCompleteWebhookUrls);
        Run("Discord rejects incomplete webhook URLs", DiscordRejectsIncompleteWebhookUrls);
        Run("Discord application ID is optional", DiscordApplicationIdIsOptional);
        Run("Hosted mode defaults to the Chatstronomy hub", HostedModeDefaultsToHub);
        Run("Hosted Hub is the first chat delivery option", HostedHubIsFirstDeliveryOption);
        Run("Plugin metadata distinguishes hosted Discord from local Matrix", HostedAndLocalDeliveryAreClearlyDescribed);
        Run("Hosted setup links to the Hub pairing flow", HostedSetupLinksToHubPairingFlow);
        Run("Local security and privacy controls are visible", LocalSecurityOptionsAreVisible);
        Run("Event delivery switches have visible labels", EventDeliverySwitchesHaveVisibleLabels);
        Run("New profiles default to hosted delivery", NewProfilesDefaultToHostedDelivery);
        Run("Existing webhook profiles keep local delivery", ExistingWebhookProfilesKeepLocalDelivery);
        Run("Unknown log levels stay silent", UnknownLogLevelsStaySilent);
        Run("Oversized log messages are truncated", OversizedLogMessagesAreTruncated);
        Run("Legacy hosted defaults migrate to the hub", LegacyHostedDefaultsMigrateToHub);
        Run("Hosted hub URLs require TLS and map to Direct WSS", HostedHubUrlsAreSecure);
        await RunAsync(
            "Development repository manifest matches N.I.N.A.'s plugin contract",
            DevelopmentRepositoryManifestMatchesNinaContract);
        Run("Hosted pair and auth frames match the Rust contract", HostedHandshakeFramesMatchRust);
        Run("Hosted client accepts unmarked legacy hub payloads", HostedLegacyHubPayloadsAreAccepted);
        Run("Hosted secrets are scoped to profile and hub origin", HostedSecretsAreOriginScoped);
        Run("Delivery and Direct diagnostics never reveal credentials", CredentialDiagnosticsAreRedacted);
        Run("Direct query deadlines match the hub clock-skew contract", DirectQueryDeadlinesMatchHub);
        await RunAsync(
            "Hosted credentials take precedence over stale pairing codes",
            HostedCredentialTakesPrecedenceOverPairingCode);
        await RunAsync(
            "Hosted client labels unmarked legacy hub payloads",
            HostedLegacyHubPayloadsAreLabeled);
        await RunAsync(
            "Hosted connection and authentication attempts time out",
            HostedConnectionAttemptsTimeOut);
        await RunAsync(
            "Hosted stop finalizes cleanup before honoring caller cancellation",
            HostedStopFinalizesBeforeCallerCancellation);
        await RunAsync(
            "Hosted client reconnects when heartbeat acknowledgements stop",
            HostedMissingHeartbeatAcknowledgementReconnects);
        await RunAsync(
            "Hosted heartbeat acknowledgements keep one connection alive",
            HostedHeartbeatAcknowledgementsKeepConnectionAlive);
        await RunAsync(
            "Hosted stalled sends abort and reconnect",
            HostedStalledSendReconnects);
        await RunAsync(
            "Hosted stop aborts cancellation-resistant sockets",
            HostedStopAbortsCancellationResistantSocket);
        await RunAsync(
            "Profile changes immediately invalidate authenticated hosted sessions",
            HostedSessionsCannotCrossProfiles);
        await RunAsync(
            "Hosted concurrent starts leave one owned connection",
            HostedConcurrentStartsLeaveOneConnection);
        await RunAsync(
            "Hosted queries cannot block heartbeat liveness",
            HostedBlockedQueryDoesNotBlockHeartbeats);
        Run("Local runtime requires an existing executable", LocalRuntimeRequiresExecutable);
        Run("Direct runtime bootstrap carries only its pipe", DirectRuntimeBootstrapCarriesOnlyPipe);
        Run("Direct access defaults to local read-only monitoring", DirectAccessDefaultsToReadOnly);
        Run("Each hardware command requires its own local consent", EveryCommandRequiresIndividualConsent);
        Run("Skipping sequence validation requires separate explicit consent", SequenceValidationBypassRequiresConsent);
        Run("Changing N.I.N.A. profiles immediately revokes in-flight hardware commands", ProfileChangesRevokeRemoteControl);
        Run("Queued UI hardware callbacks recheck consent, deadlines, and cancellation", QueuedHardwareActionsRecheckConsent);
        Run("Queued hardware commands cannot cross equally authorized N.I.N.A. profiles", QueuedHardwareActionsCannotCrossProfiles);
        await RunAsync(
            "Hardware commands recheck consent and expiry after blocking device reads",
            HardwareCommandsRecheckConsentAfterDeviceReads);
        await RunAsync(
            "Direct commands require explicit local N.I.N.A. consent",
            DirectCommandsRequireLocalConsent);
        await RunAsync(
            "Local Direct pipes cannot bypass local command consent",
            LocalDirectPipesEnforceConsent);
        await RunAsync(
            "Expired local Direct commands never reach authorized hardware providers",
            LocalDirectPipesRejectExpiredCommands);
        await RunAsync(
            "Profile changes immediately invalidate authenticated local Direct pipes",
            LocalDirectPipesCannotCrossProfiles);
        await RunAsync(
            "Synchronous local Direct failures never expose observatory filesystem paths",
            LocalDirectPipesRedactSynchronousFailures);
        await RunAsync(
            "Local Direct pipes mark autofocus reports that are still being written",
            LocalDirectPipesMarkResourcesNotReady);
        await RunAsync(
            "Disabled events and images never cross the local Direct pipe",
            LocalDirectPipesDoNotTransmitDisabledEvents);
        await RunAsync(
            "Hosted Direct connections cannot bypass local command consent",
            HostedDirectConnectionsEnforceConsent);
        await RunAsync(
            "Synchronous hosted Direct failures never expose observatory filesystem paths",
            HostedDirectConnectionsRedactSynchronousFailures);
        await RunAsync(
            "Hosted Direct connections mark autofocus reports that are still being written",
            HostedDirectConnectionsMarkResourcesNotReady);
        await RunAsync(
            "Disabled events and images never cross the hosted Hub WebSocket",
            HostedConnectionsDoNotTransmitDisabledEvents);
        Run("Observatory location is safely redacted without breaking legacy runtimes", ObservatoryLocationIsRedacted);
        Run("Nested device identifiers and sequence paths never leave N.I.N.A.", NestedSensitiveDataIsRedacted);
        await RunAsync(
            "Cached Target Scheduler events immediately honor live location consent",
            CachedEventHistoryHonorsLiveLocationConsent);
        await RunAsync(
            "Cached N.I.N.A. logs immediately honor live per-level consent",
            CachedLogHistoryHonorsLiveLevelConsent);
        await RunAsync(
            "Every event family requires consent at capture and transmission",
            EveryEventFamilyRequiresCaptureAndTransmissionConsent);
        await RunAsync(
            "Image history and thumbnails require consent at capture and transmission",
            ImageDataRequiresCaptureAndTransmissionConsent);
        Run("Equipment snapshots contain only approved operational fields", EquipmentSnapshotsUseSafeProjections);
        Run("Asynchronous commands are acknowledged without claiming completion", AsyncCommandsUseAcceptedEnvelopes);
        await RunAsync(
            "Asynchronous command failures stay visible without leaking local paths",
            CommandFailuresAreVisibleAndRedacted);
        await RunAsync(
            "Asynchronous command cancellations produce a visible terminal notification",
            CommandCancellationsAreVisible);
        await RunAsync(
            "Stale asynchronous command completions cannot cross profile sessions",
            StaleCommandCompletionsDoNotCrossProfiles);
        await RunAsync(
            "Profile history generations reject stale callback writes without erasing successors",
            ProfileHistoryGenerationRejectsStaleWrites);
        await RunAsync(
            "Late log and popup callbacks cannot cross N.I.N.A. profiles",
            LateBackgroundRecordsCannotCrossProfiles);
        await RunAsync(
            "Accepted command failures remain visible regardless of event toggles",
            CommandFailuresRemainVisibleAcrossEventToggles);
        Run("Direct commands use semantic wire names", DirectCommandsUseSemanticWireNames);
        Run("Direct camera queries use the shared equipment contract", DirectCameraQueryUsesSharedContract);
        Run("Direct event delivery categories are independently configurable", DirectEventDeliveryIsConfigurable);
        Run(
            "Sequence privacy changes invalidate old Direct peers before consent changes",
            SequencePrivacyChangesRotateOnlyDirectSession);
        await RunAsync(
            "Unrelated delivery changes preserve in-flight safety and autofocus events",
            UnrelatedDeliveryChangesPreserveInflightEvents);
        Run(
            "Location privacy changes invalidate old Direct peers before consent changes",
            LocationPrivacyChangesRotateBeforePublication);
        await RunAsync(
            "Queued automatic starts cannot cross Direct session rotations",
            QueuedConfiguredStartsCannotCrossDirectSessions);
        await RunAsync(
            "Profile-change restarts cannot outlive plugin teardown",
            ProfileChangeRestartsCannotOutliveTeardown);
        Run(
            "Plugin initialization resynchronizes the active profile before transport startup",
            InitializationResynchronizesActiveProfile);
        await RunAsync(
            "Direct session rotation preserves profile-bound autofocus state",
            DirectSessionRotationPreservesProfileWork);
        await RunAsync(
            "Direct session rotations replay events and images captured during reconnect",
            DirectSessionRotationsReplayUnseenHistory);
        await RunAsync(
            "Direct session rotations replay the last written event and image delta",
            DirectSessionRotationsReplayLastWrittenDelta);
        await RunAsync(
            "Direct session rotations retain autofocus delivery through report fetch",
            DirectSessionRotationsRetainPendingAutofocusDelivery);
        await RunAsync(
            "Physical Direct reconnects replay the last delta without changing logical identity",
            PhysicalDirectReconnectsReplayLastDelta);
        Run("N.I.N.A. log lines preserve structured source and message data", NinaLogLinesAreStructured);
        Run("N.I.N.A. popup colors map to chat severity", NinaPopupColorsMapToSeverity);
        Run(
            "N.I.N.A. popup watcher supports 3.2 and 3.3 without ToastNotifications",
            NinaPopupWatcherSupportsBothNotificationImplementations);
        Run("Direct sequence marks chat-visible operations", DirectSequenceMarksChatVisibleOperations);
        Run(
            "Sequence snapshots enforce every delivery scope",
            DirectSequenceSnapshotsEnforceEveryDeliveryScope);
        Run(
            "Sequence snapshots project warming, plate solves, and astronomical waits",
            DirectSequenceProjectsAdditionalOperations);
        Run(
            "Known Sequencer+ operations use safe projections and delivery controls",
            DirectSequenceProjectsKnownSequencerPlusOperations);
        Run(
            "Wait-until-safe projection uses live safety state and both delivery controls",
            DirectSequenceProjectsWaitUntilSafe);
        Run(
            "Disabled Target Scheduler sharing keeps children but hides target identity",
            DirectSequenceHidesDisabledTargetContainers);
        Run(
            "Sequencer+ coordinate proxies are never mistaken for targets",
            SequencerPlusProxyContainersAreNotTargets);
        await RunAsync(
            "Native slew, dome, flat, and connection events use stable consented payloads",
            NativeLifecycleEventsUseStableConsentedPayloads);
        await RunAsync(
            "Sequence failures are sanitized, deduplicated, rebound, and truthfully summarized",
            SequenceFailuresAreSafeAndTruthful);
        await RunAsync(
            "Sequence failures honor entity scopes and incomplete-consent provenance",
            SequenceFailuresHonorEntityScopesAndConsentGaps);
        await RunAsync(
            "Sequence lifecycle callbacks cannot cross profile generations",
            SequenceLifecycleCallbacksCannotCrossProfileGenerations);
        await RunAsync(
            "Sequence root refresh cannot bind a predecessor profile",
            SequenceRootRefreshCannotCrossProfileGenerations);
        await RunAsync(
            "Sequence provenance is conservative across policy publication",
            SequenceProvenanceIsConservativeAcrossPolicyPublication);
        await RunAsync(
            "N.I.N.A. 3.3 image-save failures are observed safely on a 3.2 build",
            OptionalImageSaveFailuresAreObservedSafely);
        await RunAsync(
            "Safety monitor transitions are normalized, deduplicated, and consent gated",
            SafetyMonitorTransitionsAreNormalized);
        await RunAsync(
            "Re-enabled safety sharing publishes a fresh current baseline",
            ReenabledSafetySharingPublishesFreshBaseline);
        await RunAsync(
            "Safety baselines cannot race newer state or consent",
            SafetyBaselinesCannotRaceNewerStateOrConsent);
        Run(
            "Weather reporting and high-wind alerts are independent opt-ins",
            WeatherReportingDefaultsAndRoutingAreIndependent);
        await RunAsync(
            "Meaningful weather changes are cumulative, bounded, and sanitized",
            MeaningfulWeatherChangesAreBoundedAndSanitized);
        await RunAsync(
            "High-wind alerts are deduplicated and recover with hysteresis",
            HighWindAlertsAreDeduplicatedAndHysteretic);
        await RunAsync(
            "Weather callbacks cannot cross local consent boundaries",
            WeatherCallbacksCannotCrossConsentBoundaries);
        await RunAsync(
            "Weather history purges preserve Direct replay cursors",
            WeatherHistoryPurgesPreserveDirectReplayCursors);
        await RunAsync(
            "Autofocus completion waits for the matching N.I.N.A. report",
            AutofocusCompletionWaitsForMatchingReport);
        await RunAsync(
            "Hocus Focus reports support fractional positions without leaking plugin settings",
            HocusFocusReportsAreProjectedSafely);
        Run(
            "Hocus Focus star-detection feedback modes match the effective configuration",
            HocusFocusStarDetectionModesAreProjectedAccurately);
        Run(
            "Hocus Focus string enums are projected without losing the native report",
            HocusFocusStringEnumsAreProjectedSafely);
        await RunAsync(
            "Derived Hocus Focus reports survive command and file cache ordering",
            DerivedHocusFocusReportsSurviveCacheOrdering);
        await RunAsync(
            "Partial Hocus Focus enrichment merges in either cache order",
            PartialHocusFocusEnrichmentMergesInEitherOrder);
        await RunAsync(
            "Profile-scoped autofocus reports precede profileless fallbacks",
            ProfileScopedAutofocusReportsPrecedeProfilelessFallbacks);
        await RunAsync(
            "Profileless autofocus reports require the exact completion timestamp",
            ProfilelessAutofocusReportsRequireExactTimestamp);
        await RunAsync(
            "Profileless autofocus reports require completion identity fields",
            ProfilelessAutofocusReportsRequireIdentityFields);
        await RunAsync(
            "Autofocus reports require in-session provenance and live consent",
            AutofocusReportsRequireProvenanceAndLiveConsent);
        await RunAsync(
            "Autofocus report capture honors completion consent and profile generations",
            AutofocusCaptureHonorsConsentAndGenerations);
        await RunAsync(
            "Autofocus report sharing requires uninterrupted run consent",
            AutofocusRunSharingRequiresContinuousConsent);
        await RunAsync(
            "Profile changes cancel pending autofocus report reads",
            ProfileChangesCancelPendingAutofocusReads);
        await RunAsync(
            "Disabled guiding samples are never retained for later sharing",
            DisabledGuidingSamplesAreNotRetained);
        Run("Direct guider payload matches the Rust chart contract", DirectGuiderPayloadMatchesRustChart);
        Run("Direct query results match Rust envelope", DirectQueryResultsMatchRustEnvelope);
        Run("Direct histories stay insertion ordered and bounded", DirectHistoriesAreBounded);
        Run("Direct image thumbnails are sized for chat", DirectImageThumbnailsAreSizedForChat);
        await RunAsync(
            "Thumbnail preparation preserves active work and keeps only the latest pending image",
            ThumbnailPreparationIsBoundedAndLatestWins);
        await RunAsync("Direct pipe serves camera snapshots", DirectPipeServesCameraSnapshots);
        await RunAsync(
            "Hosted plugin pairs and serves native guider graphs",
            HostedPluginPairsAndServesGuiderGraphs);
        await RunAsync(
            "Hosted plugin rejects expired commands before N.I.N.A.",
            HostedPluginRejectsExpiredCommands);

        var contractsDirectory = Environment.GetEnvironmentVariable("CHATSTRONOMY_CONTRACTS_DIR");
        if (!string.IsNullOrWhiteSpace(contractsDirectory) && Directory.Exists(contractsDirectory))
        {
            Run(
                "Pinned backend Direct v1 fixtures match the C# protocol client",
                () => PinnedDirectFixturesMatch(contractsDirectory));
        }
        else
        {
            Console.WriteLine(
                "SKIP: Backend contract fixtures (CHATSTRONOMY_CONTRACTS_DIR is not set).");
        }

        var runtimePath = Environment.GetEnvironmentVariable("CHATSTRONOMY_RUNTIME_EXE");
        if (!string.IsNullOrWhiteSpace(runtimePath) && File.Exists(runtimePath))
        {
            await RunAsync(
                "Plugin runtime starts and stops over its control pipe",
                () => PluginRuntimeStartsAndStops(runtimePath));
            await RunAsync(
                "Failed webhook requests never expose credentials in the local runtime log",
                () => PluginRuntimeRedactsFailedWebhookDeliveries(runtimePath));
            await RunAsync(
                "Plugin runtime queries the native Direct data pipe",
                () => PluginRuntimeUsesDirectPipe(runtimePath));
            await RunAsync(
                "Release runtime renders Direct guider and autofocus pipe payloads to PNG",
                () => DirectPipeRendersCharts(runtimePath));
        }
        else
        {
            Console.WriteLine(
                "SKIP: Plugin runtime process integration (CHATSTRONOMY_RUNTIME_EXE is not set).");
        }

        var hubRuntimePath = Environment.GetEnvironmentVariable("CHATSTRONOMY_HUB_EXE");
        if (!string.IsNullOrWhiteSpace(hubRuntimePath) && File.Exists(hubRuntimePath))
        {
            await RunAsync(
                "Release hub pairs the N.I.N.A. plugin and renders remote charts",
                () => HostedPluginUsesRustHub(hubRuntimePath));
        }
        else
        {
            Console.WriteLine(
                "SKIP: Hosted hub process integration (CHATSTRONOMY_HUB_EXE is not set).");
        }

        if (failures == 0)
        {
            Console.WriteLine("All Chatstronomy N.I.N.A. configuration tests passed.");
            return 0;
        }

        Console.Error.WriteLine($"{failures} Chatstronomy N.I.N.A. configuration test(s) failed.");
        return 1;
    }

    private static async Task DevelopmentRepositoryManifestMatchesNinaContract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "registry", "plugins", "manifests");
        var entries = JArray.Parse(await File.ReadAllTextAsync(path));
        AssertEqual(1, entries.Count);

        var fetcher = new PluginFetcher("https://example.invalid");
        var method = typeof(PluginFetcher).GetMethod(
            "ValidateAndParseManifest",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("N.I.N.A. manifest parser was not found.");
        var parse = (Task<PluginManifest>?)method.Invoke(fetcher, new object[] { entries[0] })
            ?? throw new InvalidOperationException("N.I.N.A. manifest parser did not return a task.");
        var manifest = await parse
            ?? throw new InvalidOperationException("N.I.N.A. rejected the development manifest.");
        var version = entries[0]["Version"]
            ?? throw new InvalidOperationException("Development manifest is missing its version.");
        var expectedVersion = string.Join(
            ".",
            new[] { "Major", "Minor", "Patch", "Build" }.Select(part =>
                version[part]?.Value<string>()
                    ?? throw new InvalidOperationException(
                        $"Development manifest version is missing '{part}'.")));

        AssertEqual("Chatstronomy", manifest.Name);
        AssertEqual(expectedVersion, manifest.Version.ToString());
        AssertEqual("3.2.0.9001", manifest.MinimumApplicationVersion.ToString());
        AssertEqual(InstallerType.ARCHIVE, manifest.Installer.Type);
        AssertEqual(InstallerChecksum.SHA256, manifest.Installer.ChecksumType);
    }

    private static void PinnedDirectFixturesMatch(string contractsDirectory)
    {
        var directRoot = Path.Combine(contractsDirectory, "direct", "v1");
        var fixtures = Path.Combine(directRoot, "fixtures");

        using (var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directRoot, "schema.json"))))
        {
            AssertEqual(
                "Chatstronomy Direct protocol v1",
                schema.RootElement.GetProperty("title").GetString());
        }

        var guider = DirectProtocol.ParseQuery(
            File.ReadAllText(Path.Combine(fixtures, "query-guider-graph.json")));
        AssertEqual(DirectQueryKind.GuiderGraph, guider.Kind);
        AssertEqual<long?>(1_900_000_000, guider.ExpiresAt);

        var command = DirectProtocol.ParseQuery(
            File.ReadAllText(Path.Combine(fixtures, "query-command.json")));
        AssertEqual(DirectQueryKind.Command, command.Kind);
        AssertEqual(DirectRigCommandKind.StartSequence, command.Command?.Kind);
        AssertEqual<bool?>(true, command.Command?.SkipValidation);

        var error = DirectProtocol.ParseHubMessage(
            File.ReadAllText(Path.Combine(fixtures, "error.json")));
        AssertTrue(error is HubErrorMessage { Retryable: false });

        using var heartbeat = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixtures, "heartbeat.json")));
        var sequence = heartbeat.RootElement.GetProperty("payload").GetProperty("seq").GetUInt64();
        using var serializedHeartbeat = JsonDocument.Parse(DirectProtocol.SerializeHeartbeat(sequence));
        AssertEqual("heartbeat", serializedHeartbeat.RootElement.GetProperty("type").GetString());
        AssertEqual(
            sequence,
            serializedHeartbeat.RootElement.GetProperty("payload").GetProperty("seq").GetUInt64());

        using var pair = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "pair.json")));
        AssertEqual(
            DirectProtocol.CurrentVersion,
            pair.RootElement.GetProperty("payload").GetProperty("hello")
                .GetProperty("protocol_version").GetUInt16());
        AssertEqual(
            DirectProtocol.CurrentPayloadVersion,
            pair.RootElement.GetProperty("payload").GetProperty("hello")
                .GetProperty("payload_version").GetUInt16());

        using var legacyHello = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixtures, "client-hello-legacy.json")));
        AssertFalse(
            legacyHello.RootElement.GetProperty("payload").TryGetProperty(
                "payload_version",
                out _));

        var notReadyFixture = Path.Combine(
            fixtures,
            "query-result-resource-not-ready.json");
        if (File.Exists(notReadyFixture))
        {
            using var notReady = JsonDocument.Parse(File.ReadAllText(notReadyFixture));
            var notReadyPayload = notReady.RootElement.GetProperty("payload");
            AssertFalse(notReadyPayload.GetProperty("ok").GetBoolean());
            AssertEqual(
                "resource_not_ready",
                notReadyPayload.GetProperty("error_code").GetString());
        }
    }

    private static void MatrixAcceptsHttpsHomeserver()
    {
        var homeserver = ChatstronomyConfigurationValidator.RequireMatrixHomeserver(
            "https://matrix.example.test:8448/");

        AssertEqual(Uri.UriSchemeHttps, homeserver.Scheme);
        AssertEqual("matrix.example.test", homeserver.Host);
    }

    private static void MatrixRejectsHttpHomeserver() =>
        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.RequireMatrixHomeserver(
                "http://matrix.example.test/"));

    private static void DiscordAcceptsCompleteWebhookUrls()
    {
        ChatstronomyConfigurationValidator.RequireDiscordWebhook(
            "https://discord.com/api/webhooks/123456789012345678/token_value");
        ChatstronomyConfigurationValidator.RequireDiscordWebhook(
            "https://discord.com/api/v10/webhooks/123456789012345678/token_value");
    }

    private static void DiscordRejectsIncompleteWebhookUrls()
    {
        foreach (var value in new[]
        {
            "https://discord.com/api/webhooks/",
            "https://discord.com/api/webhooks/123456789012345678",
            "https://discord.com/api/webhooks/not-a-number/token_value",
            "https://discord.com:8443/api/webhooks/123456789012345678/token_value",
        })
        {
            AssertThrows<InvalidOperationException>(() =>
                ChatstronomyConfigurationValidator.RequireDiscordWebhook(value));
        }
    }

    private static void DiscordApplicationIdIsOptional()
    {
        AssertEqual<ulong?>(null,
            ChatstronomyConfigurationValidator.OptionalDiscordSnowflake(
                string.Empty,
                "Discord application ID"));
        AssertEqual<ulong?>(123456789012345678,
            ChatstronomyConfigurationValidator.OptionalDiscordSnowflake(
                "123456789012345678",
                "Discord application ID"));
        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.OptionalDiscordSnowflake(
                "not-a-number",
                "Discord application ID"));
    }

    private static void HostedHubUrlsAreSecure()
    {
        var https = ChatstronomyConfigurationValidator.RequireHostedUrl(
            "https://hub.example.test:8443/");
        var wss = ChatstronomyConfigurationValidator.RequireHostedUrl(
            "wss://hub.example.test/v1/direct");

        AssertEqual(
            "wss://hub.example.test:8443/v1/direct",
            HubConnectionConfiguration.BuildWebSocketUrl(https).AbsoluteUri);
        AssertEqual(
            "wss://hub.example.test/v1/direct",
            HubConnectionConfiguration.BuildWebSocketUrl(wss).AbsoluteUri);
        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.RequireHostedUrl(
                "http://hub.example.test/"));
        AssertThrows<InvalidOperationException>(() =>
            HubConnectionConfiguration.BuildWebSocketUrl(
                new Uri("https://user:secret@hub.example.test/")));
        AssertThrows<InvalidOperationException>(() =>
            HubConnectionConfiguration.BuildWebSocketUrl(
                new Uri("https://hub.example.test/unexpected")));
    }

    private static void HostedModeDefaultsToHub()
    {
        var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(
            ChatstronomySettings.DefaultHostedServiceUrl);

        AssertEqual("hub.chatstronomy.com", serviceUrl.Host);
        AssertEqual(
            "wss://hub.chatstronomy.com/v1/direct",
            HubConnectionConfiguration.BuildWebSocketUrl(serviceUrl).AbsoluteUri);
    }

    private static void CredentialDiagnosticsAreRedacted()
    {
        const string webhookSecret = "discord-webhook-sensitive-probe";
        const string botSecret = "discord-bot-sensitive-probe";
        const string matrixPassword = "matrix-password-sensitive-probe";
        const string matrixUrlUser = "matrix-url-user-sensitive-probe";
        const string matrixUrlPassword = "matrix-url-password-sensitive-probe";
        const string matrixUrlToken = "matrix-url-token-sensitive-probe";
        const string matrixFragment = "matrix-fragment-sensitive-probe";
        const string hubUrlPassword = "hub-url-password-sensitive-probe";
        const string hubUrlToken = "hub-url-token-sensitive-probe";
        const string hostedCredential = "csrc-hosted-sensitive-probe";
        const string hostedPairing = "cspt-hosted-sensitive-probe";
        const string directPairing = "cspt-direct-sensitive-probe";
        const string directCredential = "csrc-direct-sensitive-probe";
        const string pairedCredential = "csrc-result-sensitive-probe";

        var webhook = new DiscordWebhookDeliveryConfiguration(
            new Uri($"https://discord.com/api/webhooks/123/{webhookSecret}"));
        var bot = new DiscordBotDeliveryConfiguration(botSecret, ApplicationId: 42, DefaultChannelId: 84);
        var matrix = new MatrixDeliveryConfiguration(
            new Uri(
                $"https://{matrixUrlUser}:{matrixUrlPassword}@matrix.example.test/base"
                + $"?access_token={matrixUrlToken}#{matrixFragment}"),
            Username: "@astronomer:matrix.example.test",
            Password: matrixPassword,
            DefaultRoomId: "!observatory:matrix.example.test");
        var hubUrl = new Uri(
            $"https://hub-user:{hubUrlPassword}@hub.example.test/"
            + $"?access_token={hubUrlToken}");
        var hostedDelivery = new HostedDeliveryConfiguration(hubUrl);
        var hub = new HubConnectionConfiguration(
            hubUrl,
            hostedCredential,
            hostedPairing,
            Guid.NewGuid());
        var topLevelWebhook = new ChatstronomyConfiguration(
            webhook,
            matrix,
            new LocalRuntimeConfiguration("runtime.exe"));
        var topLevelBot = topLevelWebhook with { Delivery = bot };
        var hello = HostedHello();
        var pair = new PairRequestPayload(directPairing, hello);
        var auth = new AuthRequestPayload(directCredential, hello);
        var paired = new HubPairResultMessage(
            pairedCredential,
            new AgentHello(
                DirectProtocol.CurrentVersion,
                DirectProtocol.CurrentPayloadVersion,
                Guid.NewGuid(),
                hello.NodeId,
                hello.ProfileId));
        var pairWire = new DirectWireMessage<PairRequestPayload>("pair", pair);
        var authWire = new DirectWireMessage<AuthRequestPayload>("auth", auth);
        (string Name, object Value)[] diagnostics =
        [
            ("Discord webhook delivery", webhook),
            ("Discord bot delivery", bot),
            ("Matrix delivery", matrix),
            ("hosted delivery endpoint", hostedDelivery),
            ("hosted connection", hub),
            ("nested webhook configuration", topLevelWebhook),
            ("nested Discord bot configuration", topLevelBot),
            ("Direct pairing request", pair),
            ("Direct authentication request", auth),
            ("hosted pairing result", paired),
            ("nested Direct pairing message", pairWire),
            ("nested Direct authentication message", authWire),
        ];
        string[] privateValues =
        [
            webhookSecret,
            botSecret,
            matrixPassword,
            matrixUrlUser,
            matrixUrlPassword,
            matrixUrlToken,
            matrixFragment,
            hubUrlPassword,
            hubUrlToken,
            hostedCredential,
            hostedPairing,
            directPairing,
            directCredential,
            pairedCredential,
        ];

        foreach (var (name, value) in diagnostics)
        {
            var rendered = value.ToString()
                ?? throw new InvalidOperationException($"{name} did not render a diagnostic.");
            if (!rendered.Contains("[redacted]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{name} did not mark its credential as redacted.");
            }
            if (privateValues.Any(secret => rendered.Contains(secret, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"{name} exposed a private credential.");
            }
        }

        AssertTrue(webhook.ToString().Contains("discord.com", StringComparison.Ordinal));
        AssertTrue(bot.ToString().Contains("DefaultChannelId = 84", StringComparison.Ordinal));
        AssertTrue(matrix.ToString().Contains("matrix.example.test/base", StringComparison.Ordinal));
        AssertTrue(hub.ToString().Contains("hub.example.test", StringComparison.Ordinal));

        // Sanitizing diagnostics must never redact the secure, intentional
        // transport that the local runtime and hosted handshake require.
        using var serializedPair = JsonDocument.Parse(DirectProtocol.SerializePair(directPairing, hello));
        AssertEqual(
            directPairing,
            serializedPair.RootElement.GetProperty("payload").GetProperty("pairing_token").GetString());
        using var serializedAuth = JsonDocument.Parse(DirectProtocol.SerializeAuth(directCredential, hello));
        AssertEqual(
            directCredential,
            serializedAuth.RootElement.GetProperty("payload").GetProperty("credential").GetString());
        var bootstrap = PluginRuntimeBootstrap.Serialize(
            topLevelBot,
            new LocalRuntimeIdentity(Guid.NewGuid(), Guid.NewGuid(), "Private Rig"),
            directPipeName: "chatstronomy-private-test",
            directCapabilities: hello.Capabilities);
        using var serializedBootstrap = JsonDocument.Parse(bootstrap);
        AssertEqual(
            botSecret,
            serializedBootstrap.RootElement.GetProperty("delivery").GetProperty("bot_token").GetString());
        AssertEqual(
            matrixPassword,
            serializedBootstrap.RootElement.GetProperty("matrix").GetProperty("password").GetString());
        AssertEqual(webhook, new DiscordWebhookDeliveryConfiguration(webhook.WebhookUrl));
    }

    private static void HostedHubIsFirstDeliveryOption()
    {
        var optionsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml");
        var document = System.Xml.Linq.XDocument.Load(optionsPath);
        System.Xml.Linq.XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var deliveryOptions = document
            .Descendants(presentation + "RadioButton")
            .Where(element => (string?)element.Attribute("GroupName") == "ChatstronomyDelivery")
            .ToArray();

        AssertEqual(4, deliveryOptions.Length);
        AssertEqual(
            "{Binding UseHostedService}",
            (string?)deliveryOptions[0].Attribute("IsChecked"));
        AssertEqual(
            "Chatstronomy Hub — hosted service",
            (string?)deliveryOptions[0].Attribute("Content"));
    }

    private static void HostedAndLocalDeliveryAreClearlyDescribed()
    {
        var description = typeof(ChatstronomyPlugin).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "LongDescription")
            .Value;

        AssertTrue(description is not null);
        AssertTrue(description.Contains("hosted Discord Hub", StringComparison.Ordinal));
        AssertTrue(description.Contains("local Discord/Matrix runtime", StringComparison.Ordinal));
    }

    private static void HostedSetupLinksToHubPairingFlow()
    {
        var optionsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml");
        var document = System.Xml.Linq.XDocument.Load(optionsPath);
        System.Xml.Linq.XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var link = document
            .Descendants(presentation + "Hyperlink")
            .Single(element => element.Value.Trim() == "Open Chatstronomy Hub");

        AssertEqual("https://hub.chatstronomy.com/", (string?)link.Attribute("NavigateUri"));
        AssertEqual("Hyperlink_RequestNavigate", (string?)link.Attribute("RequestNavigate"));

        var hostedLinks = document
            .Descendants(presentation + "Hyperlink")
            .ToArray();
        AssertEqual(3, hostedLinks.Length);
        AssertEqual(
            "https://chatstronomy.com/hub-privacy.html",
            (string?)hostedLinks.Single(element =>
                element.Value.Trim() == "hosted privacy policy").Attribute("NavigateUri"));
        AssertEqual(
            "https://chatstronomy.com/hub-terms.html",
            (string?)hostedLinks.Single(element =>
                element.Value.Trim() == "hosted service terms").Attribute("NavigateUri"));
        foreach (var hostedLink in hostedLinks)
        {
            AssertEqual(
                "Hyperlink_RequestNavigate",
                (string?)hostedLink.Attribute("RequestNavigate"));
        }
    }

    private static void LocalSecurityOptionsAreVisible()
    {
        var optionsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml");
        var document = System.Xml.Linq.XDocument.Load(optionsPath);
        System.Xml.Linq.XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var labels = new Dictionary<string, string>
        {
            ["{Binding AllowRemoteControl}"] =
                "Allow remote telescope and camera control",
            ["{Binding ShareObservatoryLocation}"] =
                "Share exact observatory coordinates and location-derived mount position",
            ["{Binding AllowUnparkMount}"] = "Unpark mount (/unpark)",
            ["{Binding AllowHomeMount}"] = "Home mount (/home)",
            ["{Binding AllowParkMount}"] = "Park mount (/park)",
            ["{Binding AllowChangeFilter}"] = "Change filter (/change-filter)",
            ["{Binding AllowStartGuiding}"] = "Start guiding (/guider-start)",
            ["{Binding AllowStopGuiding}"] = "Stop guiding (/guider-stop)",
            ["{Binding AllowCoolCamera}"] = "Cool camera (/cool)",
            ["{Binding AllowWarmCamera}"] = "Warm camera (/warm)",
            ["{Binding AllowStartAutofocus}"] = "Start autofocus (/autofocus)",
            ["{Binding AllowCancelAutofocus}"] = "Cancel autofocus (/autofocus)",
            ["{Binding AllowAbortExposure}"] = "Abort exposure (/abort-capture)",
            ["{Binding AllowStopSequence}"] = "Stop sequence (/stop-sequence)",
            ["{Binding AllowStartSequence}"] = "Start sequence (/start-sequence)",
            ["{Binding AllowSkipSequenceValidation}"] =
                "Allow skipping sequence safety checks",
        };

        foreach (var (binding, expectedLabel) in labels)
        {
            var checkbox = document
                .Descendants(presentation + "CheckBox")
                .Single(element => (string?)element.Attribute("IsChecked") == binding);
            AssertEqual(
                expectedLabel,
                (string?)checkbox.Parent?
                    .Elements(presentation + "TextBlock")
                    .Single()
                    .Attribute("Text"));
        }

        var commandPermissions = document
            .Descendants(presentation + "UniformGrid")
            .Single(element =>
                (string?)element.Attribute("IsEnabled") == "{Binding AllowRemoteControl}");
        AssertEqual(14, commandPermissions.Elements(presentation + "Grid").Count());
        var validationBypass = commandPermissions
            .Descendants(presentation + "CheckBox")
            .Single(element =>
                (string?)element.Attribute("IsChecked")
                == "{Binding AllowSkipSequenceValidation}");
        AssertEqual(
            "{Binding AllowStartSequence}",
            (string?)validationBypass.Parent?.Attribute("IsEnabled"));

        var descriptions = document
            .Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text") ?? string.Empty)
            .ToArray();
        AssertTrue(descriptions.Any(value =>
            value.Equals(
                "Choose which controls and information to share with the Hub or local bots.",
                StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("master switch alone grants no command access", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("never sent to the Hub or local bot", StringComparison.Ordinal)
            && value.Contains("blocks image history and thumbnails", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("weather reports are opt-in", StringComparison.Ordinal)
            && value.Contains("never sent to the Hub or local bot", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("No N.I.N.A. logs are read or sent until you enable a log level", StringComparison.Ordinal)
            && value.Contains("only selected levels are forwarded", StringComparison.Ordinal)));
    }

    private static void EventDeliverySwitchesHaveVisibleLabels()
    {
        var labelsByBinding = new Dictionary<string, string>
        {
            ["{Binding SendImageEvents}"] = "Images and thumbnails",
            ["{Binding SendAutofocusEvents}"] = "Autofocus results",
            ["{Binding SendGuidingEvents}"] = "Guiding and dithering",
            ["{Binding SendMountEvents}"] = "Mount, slew, and center",
            ["{Binding SendSequenceEvents}"] = "Sequence, timed waits, and cooling",
            ["{Binding SendSafetyEvents}"] = "Safety monitor and safety waits",
            ["{Binding SendWeatherChangeEvents}"] = "Meaningful weather changes",
            ["{Binding SendHighWindAlerts}"] = "High-wind alerts",
            ["{Binding SendTargetSchedulerEvents}"] = "Targets and Target Scheduler",
            ["{Binding SendFilterFocuserRotatorEvents}"] = "Filter, focuser, and rotator",
            ["{Binding SendObservatoryAndFlatPanelEvents}"] = "Observatory and flat panel",
            ["{Binding SendEquipmentConnectionEvents}"] = "Equipment connections",
            ["{Binding SendOtherEvents}"] = "Other N.I.N.A. events",
            ["{Binding SendNinaNotifications}"] = "Popup notifications",
            ["{Binding SendNinaLogErrors}"] = "Errors",
            ["{Binding SendNinaLogWarnings}"] = "Warnings",
            ["{Binding SendNinaLogInformation}"] = "Information",
            ["{Binding SendNinaLogDebug}"] = "Debug",
            ["{Binding SendNinaLogTrace}"] = "Trace",
        };

        var optionsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml");
        var document = System.Xml.Linq.XDocument.Load(optionsPath);
        System.Xml.Linq.XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var checkboxes = document.Descendants(presentation + "CheckBox").ToArray();

        foreach (var (binding, label) in labelsByBinding)
        {
            var checkbox = checkboxes.Single(
                element => (string?)element.Attribute("IsChecked") == binding);
            AssertEqual(null, (string?)checkbox.Attribute("Content"));
            AssertEqual(
                label,
                (string?)checkbox.Parent?
                    .Elements(presentation + "TextBlock")
                    .Single()
                    .Attribute("Text"));
        }

        var threshold = document
            .Descendants(presentation + "TextBox")
            .Single(element =>
                (string?)element.Attribute("Text")
                == "{Binding HighWindThresholdMetersPerSecond, UpdateSourceTrigger=LostFocus}");
        // Let users stage the threshold before enabling alerts. Enabling the
        // switch evaluates the current wind reading immediately.
        AssertEqual(null, (string?)threshold.Parent?.Attribute("IsEnabled"));
        AssertEqual(
            "High-wind threshold (m/s)",
            (string?)threshold.Parent?
                .Elements(presentation + "TextBlock")
                .Single()
                .Attribute("Text"));
    }

    private static void NewProfilesDefaultToHostedDelivery()
    {
        AssertEqual(
            ChatDeliveryMode.HostedService,
            ChatstronomySettings.ParseDeliveryMode(null));
        AssertEqual(
            ChatDeliveryMode.HostedService,
            ChatstronomySettings.ParseDeliveryMode("not-a-mode"));
        AssertEqual(
            ChatDeliveryMode.DiscordWebhook,
            ChatstronomySettings.ParseDeliveryMode(nameof(ChatDeliveryMode.DiscordWebhook)));
    }

    private static void LegacyHostedDefaultsMigrateToHub()
    {
        AssertEqual(
            ChatstronomySettings.DefaultHostedServiceUrl,
            ChatstronomySettings.NormalizeHostedServiceUrl("https://chatstronomy.com/"));
        AssertEqual(
            ChatstronomySettings.DefaultHostedServiceUrl,
            ChatstronomySettings.NormalizeHostedServiceUrl(" HTTPS://CHATSTRONOMY.COM "));
        AssertEqual(
            "https://private.example.test/",
            ChatstronomySettings.NormalizeHostedServiceUrl("https://private.example.test/"));
    }

    private static void HostedHandshakeFramesMatchRust()
    {
        var hello = HostedHello();
        using var pair = JsonDocument.Parse(
            DirectProtocol.SerializePair("cspt_once", hello));
        AssertEqual("pair", pair.RootElement.GetProperty("type").GetString());
        AssertEqual(
            "cspt_once",
            pair.RootElement.GetProperty("payload").GetProperty("pairing_token").GetString());
        AssertEqual(
            hello.NodeId,
            pair.RootElement.GetProperty("payload").GetProperty("hello")
                .GetProperty("node_id").GetGuid());
        AssertEqual(
            DirectProtocol.CurrentPayloadVersion,
            pair.RootElement.GetProperty("payload").GetProperty("hello")
                .GetProperty("payload_version").GetUInt16());

        using var auth = JsonDocument.Parse(
            DirectProtocol.SerializeAuth("csrc_durable", hello));
        AssertEqual("auth", auth.RootElement.GetProperty("type").GetString());
        AssertEqual(
            "csrc_durable",
            auth.RootElement.GetProperty("payload").GetProperty("credential").GetString());
    }

    private static void HostedLegacyHubPayloadsAreAccepted()
    {
        var message = DirectProtocol.ParseHubMessage(
            """{"type":"agent_hello","payload":{"protocol_version":1,"connection_id":"6dd05107-5b90-4d46-99c8-eb9a17489e81","rig_id":{"node_id":"363db028-9d79-4fdc-8940-1b1ff52b9e8d","profile_id":"460a8c62-28ce-4781-92e5-ab2440982175"}}}""");
        AssertTrue(message is HubAgentHelloMessage
        {
            Hello.PayloadVersion: DirectProtocol.LegacyPayloadVersion,
        });
    }

    private static void HostedSecretsAreOriginScoped()
    {
        var profile = Guid.Parse("460a8c62-28ce-4781-92e5-ab2440982175");
        var https = ChatstronomySettings.HostedSecretTarget(
            profile,
            new Uri("https://hub.example.test/"),
            "hosted-rig-credential");
        var wss = ChatstronomySettings.HostedSecretTarget(
            profile,
            new Uri("wss://hub.example.test/v1/direct"),
            "hosted-rig-credential");
        var otherHub = ChatstronomySettings.HostedSecretTarget(
            profile,
            new Uri("https://other.example.test/"),
            "hosted-rig-credential");
        var otherProfile = ChatstronomySettings.HostedSecretTarget(
            Guid.NewGuid(),
            new Uri("https://hub.example.test/"),
            "hosted-rig-credential");

        AssertEqual(https, wss);
        AssertFalse(https == otherHub);
        AssertFalse(https == otherProfile);
        AssertFalse(https.Contains("hub.example.test", StringComparison.OrdinalIgnoreCase));
    }

    private static void DirectQueryDeadlinesMatchHub()
    {
        var id = Guid.NewGuid();
        var query = DirectProtocol.ParseQuery(
            QueryJson(id, "event_history", expiresAt: 100));
        AssertFalse(query.IsExpiredAt(100));
        AssertFalse(query.IsExpiredAt(220));
        AssertTrue(query.IsExpiredAt(221));

        var command = DirectProtocol.ParseQuery(
            CommandQueryJson(id, expiresAt: 100, commandKind: "unpark_mount"));
        AssertFalse(command.IsExpiredAt(100));
        AssertFalse(command.IsExpiredAt(105));
        AssertTrue(command.IsExpiredAt(106));

        var noDeadline = DirectProtocol.ParseQuery(
            QueryJson(id, "guider_graph"));
        AssertFalse(noDeadline.IsExpiredAt(long.MaxValue));

        var noCommandDeadline = DirectProtocol.ParseQuery(
            """{"type":"query","payload":{"id":"363db028-9d79-4fdc-8940-1b1ff52b9e8d","kind":"command","command":{"kind":"unpark_mount"}}}""");
        AssertFalse(noCommandDeadline.IsExpiredAt(long.MaxValue));
    }

    private static void LocalRuntimeRequiresExecutable()
    {
        var configuration = ChatstronomyConfigurationValidator.BuildLocalRuntime(
            Environment.ProcessPath ?? "test-runtime.exe");

        AssertTrue(File.Exists(configuration.ExecutablePath));
        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.BuildLocalRuntime(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    private static void DirectAccessDefaultsToReadOnly()
    {
        var access = new DirectAccessPolicy(DirectAccessOptions.Default);
        AssertFalse(access.Current.AllowRemoteControl);
        AssertFalse(access.Current.ShareObservatoryLocation);
        AssertEqual(DirectCommandPermissions.None, access.Current.AllowedCommands);
        AssertFalse(access.Current.AllowSkipSequenceValidation);
        AssertThrows<InvalidOperationException>(access.RequireRemoteControl);

        using var provider = CreateSecurityTestProvider(access);
        AssertFalse(provider.Capabilities.Commands);
        access.Update(access.Current with { AllowRemoteControl = true });
        AssertFalse(provider.Capabilities.Commands);
        AssertThrows<InvalidOperationException>(access.RequireRemoteControl);

        access.Update(access.Current with
        {
            AllowedCommands = DirectCommandPermissions.UnparkMount,
        });
        access.RequireRemoteControl();
        AssertTrue(provider.Capabilities.Commands);

        var hello = HostedHello() with { Capabilities = provider.Capabilities };
        using var authorized = JsonDocument.Parse(
            DirectProtocol.SerializeAuth("csrc_test", hello));
        AssertTrue(authorized.RootElement.GetProperty("payload")
            .GetProperty("hello")
            .GetProperty("capabilities")
            .GetProperty("commands")
            .GetBoolean());

        access.Update(DirectAccessOptions.Default);
        AssertFalse(provider.Capabilities.Commands);
        AssertThrows<InvalidOperationException>(access.RequireRemoteControl);

        access.Update(new DirectAccessOptions(
            AllowRemoteControl: false,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount));
        AssertFalse(provider.Capabilities.Commands);
        AssertThrows<InvalidOperationException>(() => access.RequireRemoteControl(
            new DirectRigCommand(DirectRigCommandKind.UnparkMount)));
    }

    private static void EveryCommandRequiresIndividualConsent()
    {
        var allKinds = Enum.GetValues<DirectRigCommandKind>();
        AssertEqual(13, allKinds.Length);
        var allPermissions = DirectCommandPermissions.None;

        foreach (var kind in allKinds)
        {
            var permission = DirectAccessPolicy.PermissionFor(kind);
            AssertFalse(allPermissions.HasFlag(permission));
            allPermissions |= permission;

            var allowed = new DirectAccessPolicy(new DirectAccessOptions(
                AllowRemoteControl: true,
                ShareObservatoryLocation: false,
                AllowedCommands: permission));
            allowed.RequireRemoteControl(new DirectRigCommand(kind));

            var sibling = allKinds.First(candidate => candidate != kind);
            AssertThrows<InvalidOperationException>(() =>
                allowed.RequireRemoteControl(new DirectRigCommand(sibling)));
        }

        AssertEqual(13, Enum.GetValues<DirectCommandPermissions>().Length - 1);
    }

    private static void SequenceValidationBypassRequiresConsent()
    {
        var normalStart = new DirectRigCommand(
            DirectRigCommandKind.StartSequence,
            SkipValidation: false);
        var unsafeStart = new DirectRigCommand(
            DirectRigCommandKind.StartSequence,
            SkipValidation: true);
        var access = new DirectAccessPolicy(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.StartSequence));

        access.RequireRemoteControl(normalStart);
        AssertThrows<InvalidOperationException>(() =>
            access.RequireRemoteControl(unsafeStart));

        access.Update(access.Current with { AllowSkipSequenceValidation = true });
        access.RequireRemoteControl(unsafeStart);

        access.Update(access.Current with { AllowRemoteControl = false });
        AssertThrows<InvalidOperationException>(() =>
            access.RequireRemoteControl(unsafeStart));

        access.Update(access.Current with
        {
            AllowRemoteControl = true,
            AllowedCommands = DirectCommandPermissions.ParkMount,
        });
        AssertThrows<InvalidOperationException>(() =>
            access.RequireRemoteControl(unsafeStart));
    }

    private static void ProfileChangesRevokeRemoteControl()
    {
        var access = new DirectAccessPolicy(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.CoolCamera));
        using var provider = new FakeDirectDataProvider(access);

        ChatstronomyPlugin.ApplyProfileAccessChange(
            access,
            provider,
            new DirectAccessOptions(
                AllowRemoteControl: true,
                ShareObservatoryLocation: false,
                AllowedCommands: DirectCommandPermissions.ParkMount));

        AssertEqual(1, provider.RevocationCount);
        AssertThrows<InvalidOperationException>(() => access.RequireRemoteControl(
            new DirectRigCommand(DirectRigCommandKind.CoolCamera)));
        access.RequireRemoteControl(new DirectRigCommand(DirectRigCommandKind.ParkMount));
        AssertTrue(provider.Capabilities.Commands);

        ChatstronomyPlugin.ApplyProfileAccessChange(access, provider, access.Current);
        AssertEqual(2, provider.RevocationCount);
    }

    private static void QueuedHardwareActionsRecheckConsent()
    {
        var access = new DirectAccessPolicy(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount));
        using var provider = CreateSecurityTestProvider(access);
        var touchedHardware = false;
        var query = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.Command,
            Command: new DirectRigCommand(DirectRigCommandKind.UnparkMount),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Func<bool> HardwareAction() => () => touchedHardware = true;

        var revokedWhileQueued = provider.GuardCommandAction(
            query,
            CancellationToken.None,
            HardwareAction());
        access.Update(DirectAccessOptions.Default);
        AssertThrows<InvalidOperationException>(() => revokedWhileQueued());
        AssertFalse(touchedHardware);

        access.Update(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount));
        var expiredWhileQueued = provider.GuardCommandAction(
            query with
            {
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    - DirectProtocol.CommandExpiryClockSkewGraceSeconds - 1,
            },
            CancellationToken.None,
            HardwareAction());
        AssertThrows<InvalidOperationException>(() => expiredWhileQueued());
        AssertFalse(touchedHardware);

        using var canceled = new CancellationTokenSource();
        var canceledWhileQueued = provider.GuardCommandAction(
            query,
            canceled.Token,
            HardwareAction());
        canceled.Cancel();
        AssertThrows<OperationCanceledException>(() => canceledWhileQueued());
        AssertFalse(touchedHardware);

        var authorized = provider.GuardCommandAction(
            query,
            CancellationToken.None,
            HardwareAction());
        AssertTrue(authorized());
        AssertTrue(touchedHardware);
    }

    private static void QueuedHardwareActionsCannotCrossProfiles()
    {
        var permittedInBothProfiles = new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount);
        var access = new DirectAccessPolicy(permittedInBothProfiles);
        using var provider = CreateSecurityTestProvider(access);
        var query = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.Command,
            Command: new DirectRigCommand(DirectRigCommandKind.UnparkMount),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        var touchedOldProfileHardware = false;
        var queuedBeforeProfileChange = provider.GuardCommandAction(
            query,
            CancellationToken.None,
            () => touchedOldProfileHardware = true);

        ChatstronomyPlugin.ApplyProfileAccessChange(
            access,
            provider,
            permittedInBothProfiles);

        AssertTrue(provider.Capabilities.Commands);
        access.RequireRemoteControl(query.Command!);
        AssertThrows<InvalidOperationException>(() => queuedBeforeProfileChange());
        AssertFalse(touchedOldProfileHardware);

        var touchedCurrentProfileHardware = false;
        var queuedAfterProfileChange = provider.GuardCommandAction(
            query,
            CancellationToken.None,
            () => touchedCurrentProfileHardware = true);
        AssertTrue(queuedAfterProfileChange());
        AssertTrue(touchedCurrentProfileHardware);
    }

    private static async Task HardwareCommandsRecheckConsentAfterDeviceReads()
    {
        var allowed = new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount);
        var access = new DirectAccessPolicy(allowed);
        var mediator = DispatchProxy.Create<ITelescopeMediator, GuardedTelescopeProxy>();
        var telescope = (GuardedTelescopeProxy)(object)mediator;
        using var provider = CreateSecurityTestProvider(access, telescope: mediator);
        var command = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.Command,
            Command: new DirectRigCommand(DirectRigCommandKind.UnparkMount),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());

        // GetInfo can block while the owner switches to another profile with
        // exactly the same permissions. A final generation check must still
        // prevent the old request from actuating its replacement's mount.
        telescope.BeforeGetInfo = () =>
            ChatstronomyPlugin.ApplyProfileAccessChange(access, provider, allowed);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(command, CancellationToken.None));
        AssertEqual(0, telescope.ActuationCount);

        telescope.BeforeGetInfo = () =>
            access.Update(allowed with { AllowedCommands = DirectCommandPermissions.None });
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(command, CancellationToken.None));
        AssertEqual(0, telescope.ActuationCount);

        access.Update(allowed);
        using var canceled = new CancellationTokenSource();
        telescope.BeforeGetInfo = canceled.Cancel;
        await AssertThrowsAsync<OperationCanceledException>(() =>
            provider.ExecuteAsync(command, canceled.Token));
        AssertEqual(0, telescope.ActuationCount);

        var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            - DirectProtocol.CommandExpiryClockSkewGraceSeconds;
        var expiringCommand = command with { Id = Guid.NewGuid(), ExpiresAt = expiresAt };
        telescope.BeforeGetInfo = () =>
        {
            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                <= expiresAt + DirectProtocol.CommandExpiryClockSkewGraceSeconds)
            {
                Thread.Sleep(10);
            }
        };
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(expiringCommand, CancellationToken.None));
        AssertEqual(0, telescope.ActuationCount);
    }

    private static async Task DirectCommandsRequireLocalConsent()
    {
        var access = new DirectAccessPolicy(DirectAccessOptions.Default);
        using var provider = CreateSecurityTestProvider(access);
        var command = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.Command,
            Command: new DirectRigCommand(DirectRigCommandKind.UnparkMount));

        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(command, CancellationToken.None));

        access.Update(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.ParkMount));
        AssertTrue(provider.Capabilities.Commands);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(command, CancellationToken.None));
    }

    private static async Task LocalDirectPipesEnforceConsent()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        var id = Guid.NewGuid();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "query",
            payload = new
            {
                id,
                kind = "command",
                command = new { kind = "unpark_mount" },
            },
        }));

        var line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Direct command returned no response.");
        using var response = JsonDocument.Parse(line);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertTrue(payload.GetProperty("error").GetString()!
            .Contains("disabled in this N.I.N.A. profile", StringComparison.Ordinal));
    }

    private static async Task HostedDirectConnectionsEnforceConsent()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(new DirectAccessOptions(
                AllowRemoteControl: true,
                ShareObservatoryLocation: false,
                AllowedCommands: DirectCommandPermissions.ParkMount)));
        var hello = HostedHello() with { Capabilities = provider.Capabilities };
        AssertTrue(hello.Capabilities.Commands);
        var queryId = Guid.NewGuid();
        var sockets = new ScriptedHubSocketFactory(
            AgentHelloJson(hello),
            CommandQueryJson(queryId, expiresAt: 4_102_444_800, "unpark_mount"));
        var client = new ChatstronomyHubClient(provider, sockets);

        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None));

        var responseJson = sockets.Socket.SentMessages.Single(message =>
        {
            using var candidate = JsonDocument.Parse(message);
            return candidate.RootElement.GetProperty("type").GetString() == "query_result";
        });
        using var response = JsonDocument.Parse(responseJson);
        var payload = response.RootElement.GetProperty("payload");
        AssertEqual(queryId, payload.GetProperty("id").GetGuid());
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertTrue(payload.GetProperty("error").GetString()!
            .Contains("individual permission", StringComparison.Ordinal));
    }

    private static async Task HostedDirectConnectionsRedactSynchronousFailures()
    {
        var hello = HostedHello();
        var queryId = Guid.NewGuid();
        using var provider = new FakeDirectDataProvider(
            executeFailure: new InvalidOperationException(
                "Cannot read C:\\Users\\astronomer\\secret.sequence"));
        var sockets = new ScriptedHubSocketFactory(
            AgentHelloJson(hello),
            QueryJson(queryId, "camera_info", expiresAt: 4_102_444_800));
        var client = new ChatstronomyHubClient(provider, sockets);

        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None));

        var resultJson = sockets.Socket.SentMessages.Single(message =>
        {
            using var candidate = JsonDocument.Parse(message);
            return candidate.RootElement.GetProperty("type").GetString() == "query_result";
        });
        using var response = JsonDocument.Parse(resultJson);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        var error = payload.GetProperty("error").GetString()!;
        AssertTrue(error.Contains("[local path redacted]", StringComparison.Ordinal));
        AssertFalse(error.Contains("astronomer", StringComparison.Ordinal));
    }

    private static async Task HostedDirectConnectionsMarkResourcesNotReady()
    {
        var hello = HostedHello();
        using var provider = new FakeDirectDataProvider(
            executeFailure: DirectQueryFailureException.ResourceNotReady(
                "The autofocus report is still being written."));
        var sockets = new ScriptedHubSocketFactory(
            AgentHelloJson(hello),
            QueryJson(Guid.NewGuid(), "last_autofocus", expiresAt: 4_102_444_800));
        var client = new ChatstronomyHubClient(provider, sockets);

        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None));

        var resultJson = sockets.Socket.SentMessages.Single(message =>
        {
            using var candidate = JsonDocument.Parse(message);
            return candidate.RootElement.GetProperty("type").GetString() == "query_result";
        });
        using var response = JsonDocument.Parse(resultJson);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertEqual("resource_not_ready", payload.GetProperty("error_code").GetString());
    }

    private static async Task LocalDirectPipesRejectExpiredCommands()
    {
        var access = new DirectAccessPolicy(new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount));
        using var provider = new FakeDirectDataProvider(access);
        AssertTrue(provider.Capabilities.Commands);
        access.RequireRemoteControl(new DirectRigCommand(DirectRigCommandKind.UnparkMount));

        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        var id = Guid.NewGuid();
        var expiredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            - DirectProtocol.CommandExpiryClockSkewGraceSeconds - 1;
        await writer.WriteLineAsync(CommandQueryJson(id, expiredAt, "unpark_mount"));

        var line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Expired Direct command returned no response.");
        using var response = JsonDocument.Parse(line);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertTrue(payload.GetProperty("error").GetString()!
            .Contains("expired", StringComparison.Ordinal));
        AssertEqual(0, provider.QueryCount);
    }

    private static async Task LocalDirectPipesCannotCrossProfiles()
    {
        var permittedInBothProfiles = new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount);
        var access = new DirectAccessPolicy(permittedInBothProfiles);
        using var provider = new FakeDirectDataProvider(access);
        var previousSession = provider.ProfileSessionToken;
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName, previousSession);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(QueryJson(
            Guid.NewGuid(),
            "camera_info",
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()));
        var initial = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("The original Direct session did not respond.");
        using var response = JsonDocument.Parse(initial);
        AssertTrue(response.RootElement.GetProperty("payload").GetProperty("ok").GetBoolean());
        AssertEqual(1, provider.QueryCount);
        // StreamWriter.Dispose flushes even when its buffer is empty; close it
        // while the session is healthy so the later intentional pipe abort
        // cannot turn test cleanup into an unrelated broken-pipe failure.
        writer.Dispose();

        var elapsed = Stopwatch.StartNew();
        ChatstronomyPlugin.ApplyProfileAccessChange(access, provider, permittedInBothProfiles);
        elapsed.Stop();
        AssertTrue(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        AssertTrue(previousSession.IsCancellationRequested);
        AssertFalse(provider.ProfileSessionToken.IsCancellationRequested);
        AssertTrue(provider.Capabilities.Commands);

        // EOF proves both fresh reads and an identically permitted hardware
        // command can no longer be sent through the previous profile's pipe.
        try
        {
            var disconnected = await reader.ReadLineAsync(timeout.Token);
            AssertTrue(disconnected is null);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        AssertEqual(1, provider.QueryCount);

        // The runtime's eventual lifecycle cleanup also disposes the server.
        server.Dispose();
        server.Dispose();

        // A start queued before profile invalidation cannot resurrect its pipe.
        using var alreadyExpired = new NinaDirectPipeServer(
            provider,
            NinaDirectPipeServer.CreatePipeName(),
            previousSession);
        alreadyExpired.Start();
        alreadyExpired.Dispose();
    }

    private static async Task LocalDirectPipesRedactSynchronousFailures()
    {
        using var provider = new FakeDirectDataProvider(
            executeFailure: new InvalidOperationException(
                "Cannot read C:\\Users\\astronomer\\secret.sequence"));
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(QueryJson(
            Guid.NewGuid(),
            "camera_info",
            expiresAt: 4_102_444_800));

        var line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Failed Direct query returned no response.");
        using var response = JsonDocument.Parse(line);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        var error = payload.GetProperty("error").GetString()!;
        AssertTrue(error.Contains("[local path redacted]", StringComparison.Ordinal));
        AssertFalse(error.Contains("astronomer", StringComparison.Ordinal));
    }

    private static async Task LocalDirectPipesMarkResourcesNotReady()
    {
        using var provider = new FakeDirectDataProvider(
            executeFailure: DirectQueryFailureException.ResourceNotReady(
                "The autofocus report is still being written."));
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(QueryJson(
            Guid.NewGuid(),
            "last_autofocus",
            expiresAt: 4_102_444_800));

        var line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Failed Direct query returned no response.");
        using var response = JsonDocument.Parse(line);
        var payload = response.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertEqual("resource_not_ready", payload.GetProperty("error_code").GetString());
    }

    private static async Task LocalDirectPipesDoNotTransmitDisabledEvents()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        RecordInternalEvent(provider, "GUIDER-DITHER", "private guider movement");
        RecordInternalEvent(provider, "NINA-NOTIFICATION", "private popup");
        RecordInternalEvent(provider, "IMAGE-SAVE", "private image");
        RecordInternalEvent(provider, "MOUNT-PARKED", "approved mount event");
        AddInternalImage(provider, chatEnabled: true, value: 77);
        delivery.Update(delivery.Current with
        {
            Guiding = false,
            NinaNotifications = false,
            Images = false,
        });
        RecordInternalEvent(provider, "GUIDER-START", "captured without consent");

        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(QueryJson(Guid.NewGuid(), "event_history"));
        var eventsJson = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Local event history was not returned.");
        using var events = JsonDocument.Parse(eventsJson);
        var eventPayload = events.RootElement.GetProperty("payload")
            .GetProperty("payload")
            .GetProperty("Response");
        AssertEqual(1, eventPayload.GetArrayLength());
        AssertEqual("MOUNT-PARKED", eventPayload[0].GetProperty("Event").GetString());
        AssertFalse(eventsJson.Contains("private", StringComparison.Ordinal));

        await writer.WriteLineAsync(QueryJson(Guid.NewGuid(), "image_history"));
        var imagesJson = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Local image history was not returned.");
        using var images = JsonDocument.Parse(imagesJson);
        AssertEqual(0, images.RootElement.GetProperty("payload")
            .GetProperty("payload")
            .GetProperty("Response")
            .GetArrayLength());

        await writer.WriteLineAsync(ThumbnailQueryJson(Guid.NewGuid(), 0));
        var thumbnailJson = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Local thumbnail denial was not returned.");
        using var thumbnail = JsonDocument.Parse(thumbnailJson);
        var thumbnailResult = thumbnail.RootElement.GetProperty("payload");
        AssertFalse(thumbnailResult.GetProperty("ok").GetBoolean());
        AssertTrue(thumbnailResult.GetProperty("error").GetString()!
            .Contains("Image sharing is disabled", StringComparison.Ordinal));

        await writer.WriteLineAsync(QueryJson(Guid.NewGuid(), "guider_graph"));
        var guiderJson = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Local guider graph denial was not returned.");
        using var guiderGraph = JsonDocument.Parse(guiderJson);
        var guiderResult = guiderGraph.RootElement.GetProperty("payload");
        AssertFalse(guiderResult.GetProperty("ok").GetBoolean());
        AssertTrue(guiderResult.GetProperty("error").GetString()!
            .Contains("Guiding graph sharing is disabled", StringComparison.Ordinal));
    }

    private static async Task HostedConnectionsDoNotTransmitDisabledEvents()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        RecordInternalEvent(provider, "AUTOFOCUS-FINISHED", "private autofocus");
        RecordInternalEvent(provider, "TS-TARGETSTART", "private target");
        RecordInternalEvent(provider, "IMAGE-SAVE", "private image");
        RecordInternalEvent(provider, "MOUNT-PARKED", "approved mount event");
        AddInternalImage(provider, chatEnabled: true, value: 88);
        delivery.Update(delivery.Current with
        {
            Autofocus = false,
            TargetScheduler = false,
            Images = false,
        });
        RecordInternalEvent(provider, "AUTOFOCUS-POINT-ADDED", "captured without consent");

        var hello = HostedHello() with { Capabilities = provider.Capabilities };
        var eventQuery = Guid.NewGuid();
        var imageQuery = Guid.NewGuid();
        var thumbnailQuery = Guid.NewGuid();
        var sockets = new ScriptedHubSocketFactory(
            AgentHelloJson(hello),
            QueryJson(eventQuery, "event_history"),
            QueryJson(imageQuery, "image_history"),
            ThumbnailQueryJson(thumbnailQuery, 0));
        var client = new ChatstronomyHubClient(provider, sockets);
        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None));

        var results = sockets.Socket.SentMessages
            .Select(message => JsonDocument.Parse(message))
            .Where(document => document.RootElement.GetProperty("type").GetString()
                == "query_result")
            .ToDictionary(document =>
                document.RootElement.GetProperty("payload").GetProperty("id").GetGuid());
        try
        {
            var eventPayload = results[eventQuery].RootElement.GetProperty("payload")
                .GetProperty("payload")
                .GetProperty("Response");
            AssertEqual(1, eventPayload.GetArrayLength());
            AssertEqual("MOUNT-PARKED", eventPayload[0].GetProperty("Event").GetString());
            AssertFalse(results[eventQuery].RootElement.GetRawText()
                .Contains("private", StringComparison.Ordinal));

            AssertEqual(0, results[imageQuery].RootElement.GetProperty("payload")
                .GetProperty("payload")
                .GetProperty("Response")
                .GetArrayLength());

            var denied = results[thumbnailQuery].RootElement.GetProperty("payload");
            AssertFalse(denied.GetProperty("ok").GetBoolean());
            AssertTrue(denied.GetProperty("error").GetString()!
                .Contains("Image sharing is disabled", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var document in results.Values)
            {
                document.Dispose();
            }
        }
    }

    private static void ObservatoryLocationIsRedacted()
    {
        static Dictionary<string, object?> Mount() => new()
        {
            ["SiteLatitude"] = 38.661,
            ["SiteLongitude"] = -121.166,
            ["SiteElevation"] = 100,
            ["SiderealTime"] = 20.46,
            ["SiderealTimeString"] = "20:27:39",
            ["Altitude"] = 84.14,
            ["AltitudeString"] = "84 degrees",
            ["Azimuth"] = 165.2,
            ["AzimuthString"] = "165 degrees",
            ["TimeToMeridianFlip"] = 1.25,
            ["TimeToMeridianFlipString"] = "01:15:00",
            ["HoursToMeridianString"] = "01:15:00",
            ["RightAscension"] = 20.045,
            ["Declination"] = 42.25,
            ["DeviceId"] = "ASCOM.private.serial",
        };

        var redacted = Mount();
        DirectPrivacyProjection.RedactMount(redacted, DirectAccessOptions.Default);
        AssertEqual(0d, redacted["SiteLatitude"]);
        AssertEqual(0d, redacted["SiteLongitude"]);
        AssertEqual(0, redacted["SiteElevation"]);
        AssertEqual(0d, redacted["SiderealTime"]);
        AssertEqual(string.Empty, redacted["SiderealTimeString"]);
        AssertEqual(0d, redacted["Altitude"]);
        AssertEqual(string.Empty, redacted["AltitudeString"]);
        AssertEqual(0d, redacted["Azimuth"]);
        AssertEqual(string.Empty, redacted["AzimuthString"]);
        AssertEqual(0d, redacted["TimeToMeridianFlip"]);
        AssertEqual(string.Empty, redacted["TimeToMeridianFlipString"]);
        AssertEqual(string.Empty, redacted["HoursToMeridianString"]);
        AssertEqual(string.Empty, redacted["DeviceId"]);
        AssertEqual(true, redacted["LocationRedacted"]);
        AssertEqual(20.045, redacted["RightAscension"]);
        AssertEqual(42.25, redacted["Declination"]);

        var shared = Mount();
        DirectPrivacyProjection.RedactMount(
            shared,
            DirectAccessOptions.Default with { ShareObservatoryLocation = true });
        AssertEqual(38.661, shared["SiteLatitude"]);
        AssertEqual(-121.166, shared["SiteLongitude"]);
        AssertEqual(100, shared["SiteElevation"]);
        AssertEqual(84.14, shared["Altitude"]);
        AssertEqual(20.46, shared["SiderealTime"]);
        AssertEqual(string.Empty, shared["DeviceId"]);
        AssertEqual(false, shared["LocationRedacted"]);
    }

    private static void NestedSensitiveDataIsRedacted()
    {
        Dictionary<string, object?> NestedSnapshot() => new()
        {
            ["Name"] = "Target",
            ["FilePath"] = "C:\\Users\\astronomer\\secret.fits",
            ["Script"] = "private-command --secret",
            ["DeviceId"] = "private-device",
            ["Coordinates"] = new Dictionary<string, object?>
            {
                ["RA"] = 12.5,
                ["Dec"] = 42.25,
                ["Altitude"] = 84d,
                ["Azimuth"] = 165d,
            },
            ["Items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["SiteLongitude"] = -121d,
                    ["DriverInfo"] = "private driver path",
                    ["TimeToFlip"] = 1.25,
                    ["ExposureTime"] = 300d,
                },
            },
        };

        var redacted = NestedSnapshot();
        DirectPrivacyProjection.Redact(redacted, DirectAccessOptions.Default);
        AssertFalse(redacted.ContainsKey("FilePath"));
        AssertFalse(redacted.ContainsKey("Script"));
        AssertFalse(redacted.ContainsKey("DeviceId"));
        var coordinates = (Dictionary<string, object?>)redacted["Coordinates"]!;
        AssertEqual(12.5, coordinates["RA"]);
        AssertEqual(42.25, coordinates["Dec"]);
        AssertFalse(coordinates.ContainsKey("Altitude"));
        AssertFalse(coordinates.ContainsKey("Azimuth"));
        var nested = ((Dictionary<string, object?>[])redacted["Items"]!)[0];
        AssertFalse(nested.ContainsKey("SiteLongitude"));
        AssertFalse(nested.ContainsKey("DriverInfo"));
        AssertFalse(nested.ContainsKey("TimeToFlip"));
        AssertEqual(300d, nested["ExposureTime"]);

        var shared = NestedSnapshot();
        DirectPrivacyProjection.Redact(
            shared,
            DirectAccessOptions.Default with { ShareObservatoryLocation = true });
        AssertFalse(shared.ContainsKey("FilePath"));
        AssertFalse(shared.ContainsKey("Script"));
        AssertFalse(shared.ContainsKey("DeviceId"));
        var sharedCoordinates = (Dictionary<string, object?>)shared["Coordinates"]!;
        AssertEqual(84d, sharedCoordinates["Altitude"]);
        AssertEqual(165d, sharedCoordinates["Azimuth"]);

        using var autofocus = JsonDocument.Parse(
            """{"Response":{"DeviceId":"private","FilePath":"C:\\\\Users\\\\private","Mount":{"SiteLatitude":38.6,"RA":12.5},"MeasurePoints":[{"Position":4000,"DriverInfo":"private"}]}}""");
        var safeAutofocus = DirectPrivacyProjection.Redact(
            autofocus.RootElement,
            DirectAccessOptions.Default);
        var response = safeAutofocus.GetProperty("Response");
        AssertFalse(response.TryGetProperty("DeviceId", out _));
        AssertFalse(response.TryGetProperty("FilePath", out _));
        AssertFalse(response.GetProperty("Mount").TryGetProperty("SiteLatitude", out _));
        AssertEqual(12.5, response.GetProperty("Mount").GetProperty("RA").GetDouble());
        AssertFalse(response.GetProperty("MeasurePoints")[0].TryGetProperty("DriverInfo", out _));
    }

    private static void EquipmentSnapshotsUseSafeProjections()
    {
        var focuser = new DirectFocuserInfo(
            Connected: true,
            Position: 3325,
            StepSize: 1,
            Temperature: 14.7,
            IsMoving: false,
            IsSettling: false,
            TempComp: false,
            TempCompAvailable: true);
        using var focusJson = JsonDocument.Parse(
            JsonSerializer.Serialize(focuser, DirectProtocol.JsonOptions));
        AssertEqual(3325, focusJson.RootElement.GetProperty("Position").GetInt32());
        AssertEqual(14.7, focusJson.RootElement.GetProperty("Temperature").GetDouble());
        AssertFalse(focusJson.RootElement.TryGetProperty("DeviceId", out _));
        AssertFalse(focusJson.RootElement.TryGetProperty("DriverInfo", out _));

        var rotator = new DirectRotatorInfo(
            Connected: true,
            CanReverse: false,
            Reverse: false,
            Position: 104.04,
            MechanicalPosition: 12,
            StepSize: 0.5,
            IsMoving: false,
            Synced: true);
        using var rotateJson = JsonDocument.Parse(
            JsonSerializer.Serialize(rotator, DirectProtocol.JsonOptions));
        AssertEqual(104.04, rotateJson.RootElement.GetProperty("Position").GetDouble());
        AssertTrue(rotateJson.RootElement.GetProperty("Synced").GetBoolean());
        AssertFalse(rotateJson.RootElement.TryGetProperty("DeviceId", out _));
    }

    private static async Task CachedEventHistoryHonorsLiveLocationConsent()
    {
        var access = new DirectAccessPolicy(DirectAccessOptions.Default with
        {
            ShareObservatoryLocation = true,
        });
        using var provider = CreateSecurityTestProvider(access);
        var observer = new Dictionary<string, object?>
        {
            ["SiteLatitude"] = 38.6,
            ["DeviceId"] = "private-mount-id",
            ["FilePath"] = "C:\\Users\\astronomer\\private.sequence",
            ["ExposureTime"] = 300d,
        };
        var coordinates = new Dictionary<string, object?>
        {
            ["RA"] = 12.5,
            ["Dec"] = 42.25,
            ["Altitude"] = 84d,
            ["Azimuth"] = 165d,
            ["Observers"] = new object[] { observer },
        };
        var addEvent = typeof(NinaDirectDataProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "AddEventCore"
                && method.GetParameters().Length == 4)
            ?? throw new InvalidOperationException("Event history recorder was not found.");
        addEvent.Invoke(
            provider,
            new object[]
            {
                DateTimeOffset.UtcNow,
                "TS-TARGETSTART",
                true,
                new (string Name, object? Value)[]
                {
                    ("TargetName", "M31"),
                    ("Coordinates", coordinates),
                    ("DeviceId", "top-level-private-id"),
                },
            });

        var initiallyShared = (await SnapshotEvents(provider)).Single();
        var sharedCoordinates = initiallyShared.GetProperty("Coordinates");
        AssertEqual(84d, sharedCoordinates.GetProperty("Altitude").GetDouble());
        AssertEqual(165d, sharedCoordinates.GetProperty("Azimuth").GetDouble());
        AssertEqual(38.6, sharedCoordinates
            .GetProperty("Observers")[0]
            .GetProperty("SiteLatitude")
            .GetDouble());
        AssertFalse(initiallyShared.TryGetProperty("DeviceId", out _));
        AssertFalse(sharedCoordinates
            .GetProperty("Observers")[0]
            .TryGetProperty("DeviceId", out _));
        AssertFalse(sharedCoordinates
            .GetProperty("Observers")[0]
            .TryGetProperty("FilePath", out _));

        access.Update(access.Current with { ShareObservatoryLocation = false });
        var withheld = (await SnapshotEvents(provider)).Single();
        var withheldCoordinates = withheld.GetProperty("Coordinates");
        AssertEqual(12.5, withheldCoordinates.GetProperty("RA").GetDouble());
        AssertFalse(withheldCoordinates.TryGetProperty("Altitude", out _));
        AssertFalse(withheldCoordinates.TryGetProperty("Azimuth", out _));
        AssertFalse(withheldCoordinates
            .GetProperty("Observers")[0]
            .TryGetProperty("SiteLatitude", out _));

        // The bounded history and its caller-owned nested objects remain
        // intact, so a later explicit opt-in can share position again.
        AssertEqual(84d, coordinates["Altitude"]);
        AssertEqual(38.6, observer["SiteLatitude"]);
        AssertEqual("private-mount-id", observer["DeviceId"]);
        access.Update(access.Current with { ShareObservatoryLocation = true });
        var sharedAgain = (await SnapshotEvents(provider)).Single()
            .GetProperty("Coordinates");
        AssertEqual(84d, sharedAgain.GetProperty("Altitude").GetDouble());
        AssertEqual(38.6, sharedAgain
            .GetProperty("Observers")[0]
            .GetProperty("SiteLatitude")
            .GetDouble());
        AssertFalse(sharedAgain.GetProperty("Observers")[0]
            .TryGetProperty("DeviceId", out _));
    }

    private static async Task CachedLogHistoryHonorsLiveLevelConsent()
    {
        var initial = DirectEventDeliveryOptions.Default with
        {
            NinaLogErrors = true,
            NinaLogWarnings = true,
        };
        var delivery = new DirectEventDeliveryPolicy(initial);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var recordLog = typeof(NinaDirectDataProvider).GetMethod(
            "RecordLog",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Log history recorder was not found.");
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.UtcNow,
                "WARNING",
                "Telescope",
                "Move",
                12,
                "Original private warning"),
            GetHistoryGeneration(provider),
        });
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.UtcNow.AddSeconds(1),
                "ERROR",
                "Camera",
                "Cool",
                34,
                "Original private error"),
            GetHistoryGeneration(provider),
        });
        AssertEqual(2, (await SnapshotEvents(provider)).Length);

        delivery.Update(initial with { NinaLogWarnings = false });
        var errorsOnly = await SnapshotEvents(provider);
        AssertEqual(1, errorsOnly.Length);
        AssertEqual("ERROR", errorsOnly[0].GetProperty("Level").GetString());
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.UtcNow.AddSeconds(2),
                "WARNING",
                "Telescope",
                "Move",
                99,
                "Warning captured without consent"),
            GetHistoryGeneration(provider),
        });

        delivery.Update(DirectEventDeliveryOptions.Default);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        delivery.Update(initial with { NinaLogErrors = false });
        var warningsRestored = await SnapshotEvents(provider);
        AssertEqual(1, warningsRestored.Length);
        AssertEqual("WARNING", warningsRestored[0].GetProperty("Level").GetString());
        AssertEqual(
            "Original private warning",
            warningsRestored[0].GetProperty("Message").GetString());

        delivery.Update(initial);
        AssertEqual(2, (await SnapshotEvents(provider)).Length);
    }

    private static async Task EveryEventFamilyRequiresCaptureAndTransmissionConsent()
    {
        var cases = new (
            string Category,
            string[] Events,
            Func<DirectEventDeliveryOptions, DirectEventDeliveryOptions> Disable)[]
        {
            ("images", new[]
                { "IMAGE-SAVE", "IMAGE-SAVE-FAILED", "API-CAPTURE-FINISHED", "CAMERA-DOWNLOAD-TIMEOUT" },
                options => options with { Images = false }),
            ("autofocus", new[] { "AUTOFOCUS-FINISHED", "ERROR-AF" },
                options => options with { Autofocus = false }),
            ("guiding", new[] { "GUIDER-START", "GUIDER-DITHER" },
                options => options with { Guiding = false }),
            ("mount", new[] { "MOUNT-PARKED", "MOUNT-CENTER", "ERROR-PLATESOLVE" },
                options => options with { Mount = false }),
            ("sequence", new[] { "SEQUENCE-STARTING", "SEQUENCE-FINISHED" },
                options => options with { Sequence = false }),
            ("safety", new[] { "SAFETY-CONNECTED", "SAFETY-CHANGED", "SAFETY-DISCONNECTED" },
                options => options with { Safety = false }),
            ("weather changes", new[] { "WEATHER-CHANGED" },
                options => options with { WeatherChanges = false }),
            ("high-wind alerts", new[] { "WEATHER-HIGH-WIND" },
                options => options with { HighWindAlerts = false }),
            ("targets", new[] { "TS-TARGETSTART", "TS-NEWTARGETSTART", "TS-WAITSTART" },
                options => options with { TargetScheduler = false }),
            ("filter, focuser, and rotator", new[]
                { "FILTERWHEEL-CHANGED", "FOCUSER-MOVED", "FOCUSER-USER-FOCUSED", "ROTATOR-MOVED" },
                options => options with { FilterFocuserRotator = false }),
            ("observatory and flat panel", new[]
                { "DOME-SHUTTER-OPENED", "DOME-SLEWED", "FLAT-COVER-CLOSED", "FLAT-LIGHT-TOGGLED" },
                options => options with { ObservatoryAndFlatPanel = false }),
            ("connections", new[]
                { "MOUNT-CONNECTED", "GUIDER-DISCONNECTED", "DOME-CONNECTED", "FLAT-DISCONNECTED", "WEATHER-CONNECTED", "SWITCH-DISCONNECTED" },
                options => options with { EquipmentConnections = false }),
            ("other", new[] { "UNKNOWN-NINA-EVENT" },
                options => options with { OtherEvents = false }),
            ("popup notifications", new[] { "NINA-NOTIFICATION" },
                options => options with { NinaNotifications = false }),
        };

        foreach (var (category, eventNames, disable) in cases)
        {
            var initial = category switch
            {
                "weather changes" => DirectEventDeliveryOptions.Default with
                    { WeatherChanges = true },
                "high-wind alerts" => DirectEventDeliveryOptions.Default with
                    { HighWindAlerts = true },
                _ => DirectEventDeliveryOptions.Default,
            };
            var delivery = new DirectEventDeliveryPolicy(initial);
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                deliveryPolicy: delivery);

            foreach (var eventName in eventNames)
            {
                RecordInternalEvent(provider, eventName, $"{category}: consented");
            }
            var allowed = await SnapshotEvents(provider);
            AssertEqual(eventNames.Length, allowed.Length);

            delivery.Update(disable(initial));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);
            foreach (var eventName in eventNames)
            {
                RecordInternalEvent(provider, eventName, $"{category}: captured while off");
            }
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            delivery.Update(initial);
            var restored = await SnapshotEvents(provider);
            AssertEqual(eventNames.Length, restored.Length);
            AssertTrue(restored.All(item =>
                item.GetProperty("Marker").GetString() == $"{category}: consented"));
        }
    }

    private static async Task ImageDataRequiresCaptureAndTransmissionConsent()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var approvedImage = AddInternalImage(provider, chatEnabled: true, value: 11);
        var history = await SnapshotImageHistory(provider);
        AssertEqual(1, history.Length);
        AssertEqual(11, history[0].GetProperty("Gain").GetInt32());
        var originalThumbnail = AssertType<DirectThumbnail>((await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.Thumbnail, Index: 0),
            CancellationToken.None))!);
        AssertEqual(approvedImage.ThumbnailData![0], originalThumbnail.Data[0]);

        delivery.Update(delivery.Current with { Images = false });
        AssertEqual(0, (await SnapshotImageHistory(provider)).Length);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(
                new DirectQuery(Guid.NewGuid(), DirectQueryKind.Thumbnail, Index: 0),
                CancellationToken.None));
        AddInternalImage(provider, chatEnabled: false, value: 22);

        delivery.Update(delivery.Current with { Images = true });
        var laterApprovedImage = AddInternalImage(provider, chatEnabled: true, value: 33);
        var restored = await SnapshotImageHistory(provider);
        AssertEqual(2, restored.Length);
        AssertEqual(11, restored[0].GetProperty("Gain").GetInt32());
        AssertEqual(33, restored[1].GetProperty("Gain").GetInt32());
        var restoredThumbnail = AssertType<DirectThumbnail>((await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.Thumbnail, Index: 0),
            CancellationToken.None))!);
        AssertEqual(approvedImage.ThumbnailData![0], restoredThumbnail.Data[0]);
        var laterApprovedThumbnail = AssertType<DirectThumbnail>((await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.Thumbnail, Index: 1),
            CancellationToken.None))!);
        AssertEqual(laterApprovedImage.ThumbnailData![0], laterApprovedThumbnail.Data[0]);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(
                new DirectQuery(Guid.NewGuid(), DirectQueryKind.Thumbnail, Index: 2),
                CancellationToken.None));
    }

    private static void RecordInternalEvent(
        NinaDirectDataProvider provider,
        string eventName,
        string marker)
    {
        var addEvent = typeof(NinaDirectDataProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "AddEvent"
                && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == typeof(string))
            ?? throw new InvalidOperationException("N.I.N.A. event recorder was not found.");
        addEvent.Invoke(provider, new object[]
        {
            eventName,
            new (string Name, object? Value)[] { ("Marker", marker) },
        });
    }

    private static void RecordInternalLog(
        NinaDirectDataProvider provider,
        string message)
    {
        var recordLog = typeof(NinaDirectDataProvider).GetMethod(
            "RecordLog",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("N.I.N.A. log recorder was not found.");
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.UtcNow,
                "INFO",
                "ReplayTest",
                "Record",
                1,
                message),
            GetHistoryGeneration(provider),
        });
    }

    private static DirectSavedImage AddInternalImage(
        NinaDirectDataProvider provider,
        bool chatEnabled,
        int value)
    {
        var history = typeof(NinaDirectDataProvider).GetField(
            "images",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            as BoundedHistory<DirectSavedImage>
            ?? throw new InvalidOperationException("Native image history was not found.");
        var image = new DirectSavedImage(new DirectImageMetadata(
            ExposureTime: 120,
            ImageType: "LIGHT",
            Filter: "Luminance",
            RmsText: "0.7",
            Temperature: -10,
            CameraName: "Test camera",
            Gain: value,
            Offset: 50,
            Date: DateTime.UtcNow,
            TelescopeName: "Test telescope",
            FocalLength: 500,
            StDev: 1,
            Mean: 2,
            Median: 3,
            Stars: 20,
            HFR: 1.5,
            IsBayered: false,
            ChatEnabled: chatEnabled))
        {
            ThumbnailData = new byte[] { (byte)value, 0xff },
        };
        history.Add(image);
        return image;
    }

    private static async Task<JsonElement[]> SnapshotImageHistory(NinaDirectDataProvider provider)
    {
        var history = await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.ImageHistory),
            CancellationToken.None);
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(history, DirectProtocol.JsonOptions));
        return response.RootElement.GetProperty("Response")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
    }

    private static async Task<JsonElement[]> SnapshotEvents(NinaDirectDataProvider provider)
    {
        var history = await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.EventHistory),
            CancellationToken.None);
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(history, DirectProtocol.JsonOptions));
        return response.RootElement.GetProperty("Response")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
    }

    private static void AsyncCommandsUseAcceptedEnvelopes()
    {
        var accepted = DirectApiEnvelope<string>.Accepted("Sequence start requested");
        using var result = JsonDocument.Parse(DirectProtocol.SerializeSuccess(
            Guid.NewGuid(),
            accepted));
        var envelope = result.RootElement
            .GetProperty("payload")
            .GetProperty("payload");
        AssertTrue(envelope.GetProperty("Success").GetBoolean());
        AssertEqual(202, envelope.GetProperty("StatusCode").GetInt32());
        AssertEqual(
            "Sequence start requested",
            envelope.GetProperty("Response").GetString());
        AssertEqual(200, DirectApiEnvelope<string>.Ok("Already parked").StatusCode);
    }

    private static async Task CommandFailuresAreVisibleAndRedacted()
    {
        var delivery = DirectEventDeliveryOptions.Default with { OtherEvents = true };
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            delivery);
        var addFailure = typeof(NinaDirectDataProvider).GetMethod(
            "AddCommandFailure",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Command failure projector was not found.");
        addFailure.Invoke(
            provider,
            new object[] { "Start sequence", "Cannot read C:\\Users\\astronomer\\secret.sequence" });

        var history = await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.EventHistory),
            CancellationToken.None);
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(history, DirectProtocol.JsonOptions));
        var failure = response.RootElement.GetProperty("Response")[0];
        AssertEqual("CHATSTRONOMY-COMMAND-FAILED", failure.GetProperty("Event").GetString());
        AssertTrue(failure.GetProperty("ChatEnabled").GetBoolean());
        AssertEqual("Start sequence", failure.GetProperty("Command").GetString());
        var error = failure.GetProperty("Error").GetString()!;
        AssertTrue(error.Contains("[local path redacted]", StringComparison.Ordinal));
        AssertFalse(error.Contains("astronomer", StringComparison.Ordinal));

        var unixError = NinaDirectDataProvider.RedactCommandError(
            "Cannot read /home/astronomer/secret.sequence");
        AssertFalse(unixError.Contains("astronomer", StringComparison.Ordinal));
    }

    private static async Task CommandCancellationsAreVisible()
    {
        var delivery = DirectEventDeliveryOptions.Default with { OtherEvents = true };
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            delivery);
        var observeCommand = typeof(NinaDirectDataProvider).GetMethod(
            "ObserveCommand",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Command observer was not found.");
        var completion = new TaskCompletionSource<object?>();
        var observation = (Task)observeCommand.Invoke(
            provider,
            new object[] { completion.Task, "Cool camera", GetCommandGeneration(provider) })!;
        completion.SetCanceled();
        await observation;

        var history = await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.EventHistory),
            CancellationToken.None);
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(history, DirectProtocol.JsonOptions));
        var failure = response.RootElement.GetProperty("Response")[0];
        AssertEqual("CHATSTRONOMY-COMMAND-FAILED", failure.GetProperty("Event").GetString());
        AssertTrue(failure.GetProperty("ChatEnabled").GetBoolean());
        AssertEqual("Cool camera", failure.GetProperty("Command").GetString());
        AssertEqual(
            "Command canceled before completion",
            failure.GetProperty("Error").GetString());
    }

    private static async Task CommandFailuresRemainVisibleAcrossEventToggles()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { OtherEvents = false });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var addFailure = typeof(NinaDirectDataProvider).GetMethod(
            "AddCommandFailure",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Command failure projector was not found.");

        addFailure.Invoke(provider, new object[] { "Cool camera", "Private observatory failure" });
        var initiallyVisible = await SnapshotEvents(provider);
        AssertEqual(1, initiallyVisible.Length);
        AssertEqual("Cool camera", initiallyVisible[0].GetProperty("Command").GetString());

        delivery.Update(delivery.Current with { OtherEvents = true });
        AssertEqual(1, (await SnapshotEvents(provider)).Length);
        addFailure.Invoke(provider, new object[] { "Warm camera", "Explicitly shared failure" });
        var allowed = await SnapshotEvents(provider);
        AssertEqual(2, allowed.Length);
        AssertEqual("Warm camera", allowed[1].GetProperty("Command").GetString());

        delivery.Update(delivery.Current with
        {
            OtherEvents = false,
            Sequence = false,
            Images = false,
            Mount = false,
        });
        AssertEqual(2, (await SnapshotEvents(provider)).Length);
    }

    private static async Task StaleCommandCompletionsDoNotCrossProfiles()
    {
        var delivery = DirectEventDeliveryOptions.Default with { OtherEvents = true };
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            delivery);
        var observeCommand = typeof(NinaDirectDataProvider).GetMethod(
            "ObserveCommand",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Command observer was not found.");
        var staleFault = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCancel = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleGeneration = GetCommandGeneration(provider);
        var staleFaultObservation = (Task)observeCommand.Invoke(
            provider,
            new object[] { staleFault.Task, "Park mount", staleGeneration })!;
        var staleCancelObservation = (Task)observeCommand.Invoke(
            provider,
            new object[] { staleCancel.Task, "Cool camera", staleGeneration })!;

        provider.RevokeProfileAccess();
        provider.Reset();
        staleFault.SetException(new InvalidOperationException("private predecessor failure"));
        staleCancel.SetCanceled();
        await Task.WhenAll(staleFaultObservation, staleCancelObservation);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        var currentFailure = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var currentObservation = (Task)observeCommand.Invoke(
            provider,
            new object[]
            {
                currentFailure.Task,
                "Home mount",
                GetCommandGeneration(provider),
            })!;
        currentFailure.SetException(new InvalidOperationException("current failure"));
        await currentObservation;
        var currentEvents = await SnapshotEvents(provider);
        AssertEqual(1, currentEvents.Length);
        AssertEqual("Home mount", currentEvents[0].GetProperty("Command").GetString());
    }

    private static NinaDirectDataProvider CreateSecurityTestProvider(
        DirectAccessPolicy access,
        DirectEventDeliveryOptions? delivery = null,
        DirectEventDeliveryPolicy? deliveryPolicy = null,
        global::NINA.Profile.Interfaces.IProfileService? profileService = null,
        ITelescopeMediator? telescope = null,
        ISafetyMonitorMediator? safetyMonitor = null,
        string? autofocusReportDirectory = null,
        Func<System.Windows.Media.Imaging.BitmapSource, byte[]>? thumbnailEncoder = null,
        Func<DateTimeOffset>? utcNow = null) => new(
            profileService: profileService!,
            telescope: telescope!,
            camera: null!,
            filterWheel: null!,
            guider: null!,
            rotator: null!,
            focuser: null!,
            sequence: null!,
            safetyMonitor: safetyMonitor!,
            dome: null!,
            flatDevice: null!,
            weatherData: null!,
            switchMediator: null!,
            imageSave: null!,
            applicationStatus: null!,
            autoFocusFactory: null!,
            imageHistory: null!,
            windowFactory: null!,
            messageBroker: null!,
            eventDelivery: deliveryPolicy ?? new DirectEventDeliveryPolicy(
                delivery ?? DirectEventDeliveryOptions.Default),
            accessPolicy: access,
            autofocusReportDirectory: autofocusReportDirectory,
            thumbnailEncoder: thumbnailEncoder,
            utcNow: utcNow);

    private static void DirectCommandsUseSemanticWireNames()
    {
        var cases = new (string Json, DirectRigCommandKind Kind)[]
        {
            ("""{"kind":"unpark_mount"}""", DirectRigCommandKind.UnparkMount),
            ("""{"kind":"home_mount"}""", DirectRigCommandKind.HomeMount),
            ("""{"kind":"change_filter","filter_id":3}""", DirectRigCommandKind.ChangeFilter),
            ("""{"kind":"start_guiding","calibrate":true}""", DirectRigCommandKind.StartGuiding),
            ("""{"kind":"stop_guiding"}""", DirectRigCommandKind.StopGuiding),
            ("""{"kind":"cool_camera","temperature":-10.0,"minutes":15.0}""", DirectRigCommandKind.CoolCamera),
            ("""{"kind":"warm_camera","minutes":10.0}""", DirectRigCommandKind.WarmCamera),
            ("""{"kind":"start_autofocus"}""", DirectRigCommandKind.StartAutofocus),
            ("""{"kind":"cancel_autofocus"}""", DirectRigCommandKind.CancelAutofocus),
            ("""{"kind":"park_mount"}""", DirectRigCommandKind.ParkMount),
            ("""{"kind":"abort_exposure"}""", DirectRigCommandKind.AbortExposure),
            ("""{"kind":"stop_sequence"}""", DirectRigCommandKind.StopSequence),
            ("""{"kind":"start_sequence","skip_validation":false}""", DirectRigCommandKind.StartSequence),
        };

        foreach (var (commandJson, expectedKind) in cases)
        {
            var command = ParseDirectCommand(commandJson);
            AssertEqual(expectedKind, command.Kind);
            AssertFalse(commandJson.Contains("/equipment/", StringComparison.Ordinal));
        }

        var filter = ParseDirectCommand(cases[2].Json);
        AssertEqual<int?>(3, filter.FilterId);
        var guiding = ParseDirectCommand(cases[3].Json);
        AssertEqual<bool?>(true, guiding.Calibrate);
        var cooling = ParseDirectCommand(cases[5].Json);
        AssertEqual<double?>(-10.0, cooling.Temperature);
        AssertEqual<double?>(15.0, cooling.Minutes);
        var warming = ParseDirectCommand(cases[6].Json);
        AssertEqual<double?>(10.0, warming.Minutes);
        var sequence = ParseDirectCommand(cases[12].Json);
        AssertEqual<bool?>(false, sequence.SkipValidation);

        AssertThrows<DirectProtocolException>(() => ParseDirectCommand(
            """{"kind":"/equipment/camera/cool"}"""));
    }

    private static DirectRigCommand ParseDirectCommand(string commandJson)
    {
        var json = $$"""
            {
              "type": "query",
              "payload": {
                "id": "7afcde18-b5a8-46fd-ad1f-ed54cf3bbc4e",
                "kind": "command",
                "command": {{commandJson}}
              }
            }
            """;
        var query = DirectProtocol.ParseQuery(json);
        AssertEqual(DirectQueryKind.Command, query.Kind);
        return query.Command ?? throw new InvalidOperationException("Command was not parsed.");
    }

    private static void DirectCameraQueryUsesSharedContract()
    {
        var query = DirectProtocol.ParseQuery(
            """{"type":"query","payload":{"id":"7afcde18-b5a8-46fd-ad1f-ed54cf3bbc4e","kind":"camera_info"}}""");
        AssertEqual(DirectQueryKind.CameraInfo, query.Kind);

        var envelope = DirectApiEnvelope<DirectCameraInfo>.Ok(new DirectCameraInfo(
            Connected: true,
            CanSetTemperature: true,
            CoolerOn: true,
            CoolerPower: 72.5,
            Temperature: -6.4,
            TemperatureSetPoint: -10,
            AtTargetTemp: false,
            Name: "ASI2600MM",
            DisplayName: "ASI2600MM"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope, DirectProtocol.JsonOptions));
        var camera = json.RootElement.GetProperty("Response");
        AssertEqual(-6.4, camera.GetProperty("Temperature").GetDouble());
        AssertEqual(-10.0, camera.GetProperty("TemperatureSetPoint").GetDouble());
        AssertTrue(camera.GetProperty("CoolerOn").GetBoolean());
        AssertFalse(camera.GetProperty("AtTargetTemp").GetBoolean());
    }

    private static void DirectEventDeliveryIsConfigurable()
    {
        var options = DirectEventDeliveryOptions.Default with
        {
            Guiding = false,
            Safety = false,
            EquipmentConnections = false,
            TargetScheduler = false,
            NinaLogWarnings = true,
        };

        AssertFalse(options.ShouldSendEvent("GUIDER-DITHER"));
        AssertFalse(options.ShouldSendEvent("GUIDER-CONNECTED"));
        AssertFalse(options.ShouldSendEvent("SAFETY-CONNECTED"));
        AssertFalse(options.ShouldSendEvent("SAFETY-CHANGED"));
        AssertFalse(options.ShouldSendEvent("WEATHER-CHANGED"));
        AssertFalse(options.ShouldSendEvent("WEATHER-HIGH-WIND"));
        AssertFalse(options.ShouldSendEvent("TS-TARGETSTART"));
        AssertTrue(options.ShouldSendEvent("MOUNT-CENTER"));
        AssertTrue(options.ShouldSendEvent("CHATSTRONOMY-COMMAND-FAILED"));
        AssertTrue(options.ShouldSendEvent("CAMERA-DOWNLOAD-TIMEOUT"));
        AssertTrue(options.ShouldSendEvent("FOCUSER-USER-FOCUSED"));
        AssertTrue(options.ShouldSendLogLevel("WARNING"));
        AssertFalse(options.ShouldSendLogLevel("INFO"));
    }

    private static void UnknownLogLevelsStaySilent()
    {
        // Log forwarding is opt-in: an unrecognised level must not ride in on
        // the OtherEvents default, which is true.
        var allLogsOff = DirectEventDeliveryOptions.Default;
        AssertTrue(allLogsOff.OtherEvents);
        AssertFalse(allLogsOff.AnyLogLevelEnabled);
        AssertFalse(allLogsOff.ShouldSendLogLevel("NOTICE"));
        AssertFalse(allLogsOff.ShouldSendLogLevel(string.Empty));
        AssertFalse(allLogsOff.ShouldSendLogLevel("ERROR"));

        var errorsOn = allLogsOff with { NinaLogErrors = true };
        AssertTrue(errorsOn.AnyLogLevelEnabled);
        AssertTrue(errorsOn.ShouldSendLogLevel("ERROR"));
        AssertFalse(errorsOn.ShouldSendLogLevel("NOTICE"));
    }

    private static void OversizedLogMessagesAreTruncated()
    {
        // One absurd line must not sit in the ring being re-sent every poll.
        var giant = new string('x', 50_000);
        AssertTrue(NinaLogWatcher.TryParseLine(
            $"2026-08-16 21:04:05.123|Error|A.cs|M|1|{giant}",
            out var record));
        AssertTrue(record.Message.Length < 2_100);
    }

    private static void ExistingWebhookProfilesKeepLocalDelivery()
    {
        // A profile that accepted the old webhook default never persisted a
        // DeliveryMode, and must not be read as hosted on upgrade.
        AssertEqual(
            ChatDeliveryMode.DiscordWebhook,
            ChatstronomySettings.ParseDeliveryMode(
                nameof(ChatDeliveryMode.DiscordWebhook)));
        AssertEqual(
            ChatDeliveryMode.HostedService,
            ChatstronomySettings.ParseDeliveryMode(null));
    }

    private static void NinaLogLinesAreStructured()
    {
        AssertTrue(NinaLogWatcher.TryParseLine(
            "2026-08-16 21:04:05.123|Warning|CameraVM.cs|Connect|42|Camera | response delayed",
            out var record));
        AssertEqual("WARNING", record.Level);
        AssertEqual("CameraVM.cs", record.Source);
        AssertEqual("Connect", record.Member);
        AssertEqual(42, record.Line);
        AssertEqual("Camera | response delayed", record.Message);
    }

    private static void NinaPopupColorsMapToSeverity()
    {
        AssertEqual("ERROR", NinaNotificationWatcher.Classify(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)));
        AssertEqual("WARNING", NinaNotificationWatcher.Classify(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gold)));
        AssertEqual("SUCCESS", NinaNotificationWatcher.Classify(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Lime)));
        AssertEqual("INFORMATION", NinaNotificationWatcher.Classify(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Blue)));
    }

    private static void NinaPopupWatcherSupportsBothNotificationImplementations()
    {
        AssertFalse(typeof(NinaNotificationWatcher).Assembly
            .GetReferencedAssemblies()
            .Any(assembly => string.Equals(
                assembly.Name,
                "ToastNotifications",
                StringComparison.OrdinalIgnoreCase)));

        var nativeRecords = new List<(NinaNotificationRecord Record, long Generation)>();
        using (var watcher = new NinaNotificationWatcher(
            (record, generation) => nativeRecords.Add((record, generation))))
        {
            var manager = new NativeNotificationManagerStub();
            AssertTrue(watcher.TryObserveNativeManager(manager, captureGeneration: 101));
            manager.Notifications.Add(new NotificationStub(
                new DateTime(2026, 8, 25, 20, 1, 2),
                " Warning ",
                " Native popup ",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gold)));
            AssertEqual(1, nativeRecords.Count);
            AssertEqual(101L, nativeRecords[0].Generation);
            AssertEqual("WARNING", nativeRecords[0].Record.Level);
            AssertEqual("Warning", nativeRecords[0].Record.Header);
            AssertEqual("Native popup", nativeRecords[0].Record.Message);

            watcher.Stop();
            manager.Notifications.Add(new NotificationStub(
                DateTime.Now,
                "Ignored",
                "Unsubscribed",
                null));
            AssertEqual(1, nativeRecords.Count);
        }

        var legacyRecords = new List<(NinaNotificationRecord Record, long Generation)>();
        using (var watcher = new NinaNotificationWatcher(
            (record, generation) => legacyRecords.Add((record, generation))))
        {
            var notifier = new LegacyNotifierStub();
            AssertTrue(watcher.TryObserveLegacyNotifier(notifier, captureGeneration: 202));
            AssertTrue(notifier.ConfigureCalled);
            notifier.Show(new NotificationStub(
                new DateTime(2026, 8, 25, 20, 3, 4),
                "Error",
                "Legacy popup",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)));
            AssertEqual(1, legacyRecords.Count);
            AssertEqual(202L, legacyRecords[0].Generation);
            AssertEqual("ERROR", legacyRecords[0].Record.Level);
            AssertEqual("Legacy popup", legacyRecords[0].Record.Message);

            watcher.Stop();
            notifier.Show(new NotificationStub(
                DateTime.Now,
                "Ignored",
                "Unsubscribed",
                null));
            AssertEqual(1, legacyRecords.Count);
        }
    }

    private sealed class NativeNotificationManagerStub
    {
        public System.Collections.ObjectModel.ObservableCollection<object> Notifications { get; } = new();
    }

    private sealed record NotificationStub(
        DateTime DateTime,
        string Header,
        string Message,
        System.Windows.Media.Brush? Color);

    private sealed class LegacyNotifierStub
    {
        private LegacyLifetimeSupervisorStub? _lifetimeSupervisor;

        internal bool ConfigureCalled { get; private set; }

        internal void Show(object notification) => _lifetimeSupervisor!.Show(notification);

        private void Configure()
        {
            ConfigureCalled = true;
            _lifetimeSupervisor = new LegacyLifetimeSupervisorStub();
        }
    }

    private sealed class LegacyLifetimeSupervisorStub
    {
        public event EventHandler<LegacyShowNotificationEventArgsStub>? ShowNotificationRequested;

        internal void Show(object notification) =>
            ShowNotificationRequested?.Invoke(
                this,
                new LegacyShowNotificationEventArgsStub(notification));
    }

    private sealed class LegacyShowNotificationEventArgsStub : EventArgs
    {
        internal LegacyShowNotificationEventArgsStub(object notification)
        {
            Notification = notification;
        }

        public object Notification { get; }
    }

    private static void DirectSequenceMarksChatVisibleOperations()
    {
        var method = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "AddItemDetails",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence detail projector was not found.");

        var wait = new global::NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 90 };
        var waitDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object?[]
            { wait, waitDetails, null, DirectEventDeliveryOptions.Default });
        AssertEqual("time_wait", waitDetails["OperationKind"] as string);
        AssertEqual(90.0, Convert.ToDouble(waitDetails["Delay"]));
        AssertEqual(TimeSpan.FromSeconds(90), (TimeSpan)waitDetails["CalculatedWaitDuration"]!);

        var cooling = new global::NINA.Sequencer.SequenceItem.Camera.CoolCamera(null!)
        {
            Temperature = -10,
            Duration = 15,
        };
        var coolingDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object?[]
            { cooling, coolingDetails, null, DirectEventDeliveryOptions.Default });
        AssertEqual("camera_cooling", coolingDetails["OperationKind"] as string);
        AssertEqual(-10.0, Convert.ToDouble(coolingDetails["Temperature"]));
        AssertEqual(15.0, Convert.ToDouble(coolingDetails["MinCoolingTime"]));

        var slew = new global::NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec(null!, null!);
        var slewDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object?[]
            { slew, slewDetails, null, DirectEventDeliveryOptions.Default });
        AssertEqual("mount_slew", slewDetails["OperationKind"] as string);

        var center = new global::NINA.Sequencer.SequenceItem.Platesolving.Center(
            null!, null!, null!, null!, null!, null!, null!, null!, null!);
        center.PlateSolveStatusVM.PlateSolveResult = new global::NINA.PlateSolving.PlateSolveResult(
            new DateTime(2026, 8, 15, 23, 45, 0, DateTimeKind.Utc))
        {
            Success = true,
            Coordinates = new global::NINA.Astrometry.Coordinates(
                12.5,
                42.25,
                global::NINA.Astrometry.Epoch.J2000,
                global::NINA.Astrometry.Coordinates.RAType.Hours),
            PositionAngle = 91.5,
            Pixscale = 1.25,
            Radius = 1.75,
            Separation = new global::NINA.Astrometry.Separation
            {
                Distance = global::NINA.Astrometry.Angle.ByDegree(1d / 60d),
            },
        };
        center.PlateSolveStatusVM.Thumbnail = System.Windows.Media.Imaging.BitmapSource.Create(
            1,
            1,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            new byte[] { 0, 0, 0, 255 },
            4);
        var centerDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object?[]
            { center, centerDetails, null, DirectEventDeliveryOptions.Default });
        AssertEqual("mount_center", centerDetails["OperationKind"] as string);
        var output = (Dictionary<string, object?>)centerDetails["PlateSolveOutput"]!;
        AssertEqual(true, Convert.ToBoolean(output["Success"]));
        AssertEqual(91.5, Convert.ToDouble(output["PositionAngle"]));
        AssertEqual(60.0, Convert.ToDouble(output["SeparationArcseconds"]));
        AssertFalse(output.ContainsKey("ThumbnailBase64"));
        AssertFalse(output.ContainsKey("ThumbnailMediaType"));
        var wire = JsonSerializer.Serialize(centerDetails, DirectProtocol.JsonOptions);
        AssertFalse(wire.Contains("Thumbnail", StringComparison.Ordinal));
    }

    private static void DirectSequenceSnapshotsEnforceEveryDeliveryScope()
    {
        var buildItem = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");
        var buildCondition = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildCondition",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence condition projector was not found.");
        var buildTrigger = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildTrigger",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence trigger projector was not found.");

        Dictionary<string, object?> ProjectItem(
            global::NINA.Sequencer.SequenceItem.ISequenceItem item,
            DirectEventDeliveryOptions delivery) =>
            (Dictionary<string, object?>)buildItem.Invoke(
                null,
                new object?[] { item, delivery, false })!;

        var cases = new (
            global::NINA.Sequencer.SequenceItem.ISequenceItem Item,
            Func<DirectEventDeliveryOptions, DirectEventDeliveryOptions> Disable)[]
        {
            (new global::NINA.Plugin.SequencerPlus.TakeExposure(),
                options => options with { Images = false }),
            (new global::NINA.Plugin.SequencerPlus.RunAutofocus(),
                options => options with { Autofocus = false }),
            (new global::NINA.Plugin.SequencerPlus.StartGuiding(),
                options => options with { Guiding = false }),
            (new global::NINA.Plugin.SequencerPlus.SlewToRADec(),
                options => options with { Mount = false }),
            (new global::NINA.Plugin.SequencerPlus.WarmCamera(),
                options => options with { Sequence = false }),
            (new global::NINA.Plugin.SequencerPlus.SwitchFilter(),
                options => options with { FilterFocuserRotator = false }),
            (new global::NINA.Plugin.SequencerPlus.OpenDomeShutter(),
                options => options with { ObservatoryAndFlatPanel = false }),
            (new global::NINA.Plugin.SequencerPlus.ConnectAllEquipment(),
                options => options with { EquipmentConnections = false }),
            (new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                options => options with { OtherEvents = false }),
            (new global::ThirdParty.Unrecognized.TakeExposure(),
                options => options with { OtherEvents = false }),
        };

        foreach (var (item, disable) in cases)
        {
            var shared = ProjectItem(item, DirectEventDeliveryOptions.Default);
            AssertTrue(Convert.ToBoolean(shared["ChatEnabled"]));
            AssertFalse(shared.ContainsKey("Suppressed"));

            var withheld = ProjectItem(item, disable(DirectEventDeliveryOptions.Default));
            AssertEqual(true, Convert.ToBoolean(withheld["Suppressed"]));
            AssertEqual(false, Convert.ToBoolean(withheld["ChatEnabled"]));
            AssertEqual("SUPPRESSED", withheld["Status"] as string);
        }

        var safetyCondition = new global::NINA.Plugin.SequencerPlus.SafetyMonitorCondition
        {
            Name = "Safety condition",
            IsSafe = true,
        };
        var sharedCondition = (Dictionary<string, object?>)buildCondition.Invoke(
            null,
            new object?[] { safetyCondition, DirectEventDeliveryOptions.Default })!;
        AssertEqual("safety_condition", sharedCondition["OperationKind"] as string);
        AssertEqual(true, Convert.ToBoolean(sharedCondition["IsSafe"]));
        foreach (var delivery in new[]
        {
            DirectEventDeliveryOptions.Default with { Safety = false },
            DirectEventDeliveryOptions.Default with { Sequence = false },
        })
        {
            var withheld = (Dictionary<string, object?>)buildCondition.Invoke(
                null,
                new object?[] { safetyCondition, delivery })!;
            AssertEqual(true, Convert.ToBoolean(withheld["Suppressed"]));
        }

        var safetyTrigger = new global::NINA.Plugin.SequencerPlus.TriggerOnUnsafe
        {
            Name = "Unsafe trigger",
        };
        var sharedTrigger = (Dictionary<string, object?>)buildTrigger.Invoke(
            null,
            new object?[] { safetyTrigger, DirectEventDeliveryOptions.Default })!;
        AssertEqual("safety_trigger", sharedTrigger["OperationKind"] as string);
        var withheldTrigger = (Dictionary<string, object?>)buildTrigger.Invoke(
            null,
            new object?[]
            {
                safetyTrigger,
                DirectEventDeliveryOptions.Default with { Safety = false },
            })!;
        AssertEqual(true, Convert.ToBoolean(withheldTrigger["Suppressed"]));
    }

    private static void DirectSequenceProjectsAdditionalOperations()
    {
        var buildItem = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");

        Dictionary<string, object?> Project(
            global::NINA.Sequencer.SequenceItem.ISequenceItem item) =>
            (Dictionary<string, object?>)buildItem.Invoke(
                null,
                new object?[] { item, DirectEventDeliveryOptions.Default, null })!;

        var warming = Project(new global::NINA.Plugin.SequencerPlus.WarmCamera
        {
            Duration = 17,
        });
        AssertEqual("camera_warming", warming["OperationKind"] as string);
        AssertEqual(17d, Convert.ToDouble(warming["MinWarmingTime"]));

        var solve = Project(new global::NINA.Plugin.SequencerPlus.SolveAndSync());
        AssertEqual("plate_solve", solve["OperationKind"] as string);
        var solveOutput = AssertType<Dictionary<string, object?>>(solve["PlateSolveOutput"]!);
        AssertEqual(true, Convert.ToBoolean(solveOutput["Success"]));
        AssertEqual(31.5d, Convert.ToDouble(solveOutput["PositionAngle"]));

        var wait = Project(new global::NINA.Plugin.SequencerPlus.WaitForSunAltitude());
        AssertEqual("astronomical_wait", wait["OperationKind"] as string);
        AssertEqual(-12d, Convert.ToDouble(wait["TargetAltitude"]));
        AssertEqual("LESS_THAN_OR_EQUAL", wait["Comparator"] as string);
        var expected = AssertType<DateTimeOffset>(wait["ExpectedDateTime"]!);
        AssertEqual(
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 26, 21, 30, 0)),
            expected.Offset);

        var timedWait = Project(new global::NINA.Plugin.SequencerPlus.WaitForTime
        {
            EstimatedDuration = TimeSpan.FromMinutes(25),
        });
        var targetTime = AssertType<DateTimeOffset>(timedWait["TargetTime"]!);
        var expectedTargetTime = new DateTimeOffset(
            new DateTime(2026, 8, 26, 20, 25, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 26, 20, 25, 0)));
        AssertEqual(expectedTargetTime, targetTime);
        AssertEqual(
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 26, 20, 25, 0)),
            targetTime.Offset);

        var setSwitch = Project(new global::NINA.Plugin.SequencerPlus.SetSwitchValue());
        AssertFalse(setSwitch.ContainsKey("Value"));
        AssertFalse(setSwitch.ContainsKey("Index"));
    }

    private static void DirectSequenceProjectsWaitUntilSafe()
    {
        var method = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");

        var builtIn = new global::NINA.Sequencer.SequenceItem.SafetyMonitor.WaitUntilSafe(null!)
        {
            WaitInterval = TimeSpan.FromSeconds(3),
        };
        var builtInProjection = (Dictionary<string, object?>)method.Invoke(
            null,
            new object?[] { builtIn, DirectEventDeliveryOptions.Default, true })!;
        AssertEqual("safety_wait", builtInProjection["OperationKind"] as string);
        AssertEqual(true, Convert.ToBoolean(builtInProjection["IsSafe"]));
        AssertEqual(TimeSpan.FromSeconds(3), (TimeSpan)builtInProjection["WaitInterval"]!);
        AssertEqual(true, Convert.ToBoolean(builtInProjection["ChatEnabled"]));

        var sequencerPlus = new global::NINA.Plugin.SequencerPlus.WaitUntilSafe
        {
            WaitInterval = TimeSpan.FromSeconds(7),
        };
        var sequencerPlusProjection = (Dictionary<string, object?>)method.Invoke(
            null,
            new object?[] { sequencerPlus, DirectEventDeliveryOptions.Default, false })!;
        AssertEqual("safety_wait", sequencerPlusProjection["OperationKind"] as string);
        AssertEqual(false, Convert.ToBoolean(sequencerPlusProjection["IsSafe"]));
        AssertEqual(TimeSpan.FromSeconds(7), (TimeSpan)sequencerPlusProjection["WaitInterval"]!);
        AssertEqual(true, Convert.ToBoolean(sequencerPlusProjection["ChatEnabled"]));

        foreach (var delivery in new[]
        {
            DirectEventDeliveryOptions.Default with { Sequence = false },
            DirectEventDeliveryOptions.Default with { Safety = false },
            DirectEventDeliveryOptions.Default with { Sequence = false, Safety = false },
        })
        {
            var gatedProjection = (Dictionary<string, object?>)method.Invoke(
                null,
                new object?[] { sequencerPlus, delivery, false })!;
            AssertEqual(false, Convert.ToBoolean(gatedProjection["ChatEnabled"]));
            AssertEqual(true, Convert.ToBoolean(gatedProjection["Suppressed"]));
            AssertEqual("Suppressed_SequenceItem", gatedProjection["Name"] as string);
            AssertEqual("SUPPRESSED", gatedProjection["Status"] as string);
            AssertFalse(gatedProjection.ContainsKey("OperationKind"));
            AssertFalse(gatedProjection.ContainsKey("IsSafe"));
            AssertFalse(gatedProjection.ContainsKey("WaitInterval"));
        }
    }

    private static void SequencePrivacyChangesRotateOnlyDirectSession()
    {
        var provider = new FakeDirectDataProvider();
        var policy = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        var oldDirectSession = provider.DirectSessionToken;
        var profileSession = provider.ProfileSessionToken;
        var updateObservedCancelledSession = false;
        var updateObservedSuspendedCapture = true;
        var selected = policy.Current with { Safety = false };

        ChatstronomyPlugin.ApplyEventDeliveryChange(
            provider,
            policy,
            rollDirectSession: true,
            update: () =>
            {
                updateObservedCancelledSession = oldDirectSession.IsCancellationRequested;
                updateObservedSuspendedCapture = provider.EventCaptureSuspended;
            },
            readOptions: () => selected);

        AssertTrue(updateObservedCancelledSession);
        AssertFalse(updateObservedSuspendedCapture);
        AssertFalse(provider.EventCaptureSuspended);
        AssertTrue(oldDirectSession.IsCancellationRequested);
        AssertFalse(profileSession.IsCancellationRequested);
        AssertEqual(1, provider.DirectSessionRotationCount);
        AssertEqual(0, provider.RevocationCount);
        AssertFalse(policy.Current.Safety);

        ChatstronomyPlugin.ApplyEventDeliveryChange(
            provider,
            policy,
            rollDirectSession: false,
            update: () => selected = selected with { Autofocus = false },
            readOptions: () => selected);
        AssertEqual(1, provider.DirectSessionRotationCount);
        AssertEqual(0, provider.RevocationCount);
        AssertFalse(policy.Current.Autofocus);
        AssertEqual(0, provider.EventCaptureSuspendCount);
        AssertEqual(0, provider.EventCaptureResumeCount);
    }

    private static async Task UnrelatedDeliveryChangesPreserveInflightEvents()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var type = typeof(NinaDirectDataProvider);
        var captureHistory = type.GetMethod(
            "CaptureHistoryGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History generation reader was not found.");
        var historyGeneration = (long)captureHistory.Invoke(provider, null)!;
        var completion = new DirectAutofocusCompletion(
            "Ha",
            Position: 4_200,
            Temperature: -5,
            Timestamp: new DateTime(2026, 8, 26, 4, 0, 0, DateTimeKind.Utc),
            ProfileId: null,
            ChatEnabled: true);
        SetPendingAutofocusCompletion(provider, completion);
        (type.GetField(
            "pendingAutofocusHistoryGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pending autofocus history generation was not found."))
            .SetValue(provider, historyGeneration);

        var selected = delivery.Current with { Images = false };
        ChatstronomyPlugin.ApplyEventDeliveryChange(
            provider,
            delivery,
            rollDirectSession: true,
            update: () => { },
            readOptions: () => selected);

        var recordSafety = type.GetMethod(
            "RecordSafetyState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Safety state recorder was not found.");
        recordSafety.Invoke(provider, new object[] { historyGeneration, true, false });

        var addAutofocus = type.GetMethod(
            "AddAutofocusFinishedEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Autofocus event writer was not found.");
        var started = type.GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Provider started flag was not found.");
        started.SetValue(provider, true);
        try
        {
            addAutofocus.Invoke(provider, new object[]
            {
                completion,
                GetAutofocusCaptureGeneration(provider),
            });
        }
        finally
        {
            started.SetValue(provider, false);
        }

        var events = await SnapshotEvents(provider);
        AssertTrue(events.Any(item =>
            item.GetProperty("Event").GetString() == "SAFETY-CHANGED"));
        AssertTrue(events.Any(item =>
            item.GetProperty("Event").GetString() == "AUTOFOCUS-FINISHED"));
    }

    private static void LocationPrivacyChangesRotateBeforePublication()
    {
        using var provider = new FakeDirectDataProvider();
        var policy = new DirectAccessPolicy(
            DirectAccessOptions.Default with { ShareObservatoryLocation = true });
        var oldSession = provider.DirectSessionToken;
        var updateObservedCancellation = false;

        ChatstronomyPlugin.ApplyLocationPrivacyChange(
            provider,
            policy,
            update: () => updateObservedCancellation = oldSession.IsCancellationRequested,
            readOptions: () => policy.Current with { ShareObservatoryLocation = false });

        AssertTrue(updateObservedCancellation);
        AssertTrue(oldSession.IsCancellationRequested);
        AssertFalse(policy.Current.ShareObservatoryLocation);
    }

    private static async Task QueuedConfiguredStartsCannotCrossDirectSessions()
    {
        using var provider = new FakeDirectDataProvider();
        using var gate = new SemaphoreSlim(0, 1);
        var startCount = 0;
        var queued = ChatstronomyPlugin.RunForCurrentDirectSessionAsync(
            provider,
            gate,
            _ =>
            {
                Interlocked.Increment(ref startCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        provider.RotateDirectSession();
        gate.Release();
        await AssertThrowsAsync<OperationCanceledException>(() => queued);
        AssertEqual(0, startCount);

        await ChatstronomyPlugin.RunForCurrentDirectSessionAsync(
            provider,
            gate,
            _ =>
            {
                Interlocked.Increment(ref startCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        AssertEqual(1, startCount);
    }

    private static async Task ProfileChangeRestartsCannotOutliveTeardown()
    {
        using var provider = new FakeDirectDataProvider();

        // A callback queued behind another lifecycle operation must become a
        // no-op when teardown marks the plugin uninitialized and rotates the
        // session before releasing the gate.
        using (var gate = new SemaphoreSlim(0, 1))
        {
            var initialized = true;
            var stopCount = 0;
            var startCount = 0;
            var profileSession = provider.DirectSessionToken;
            var restart = ChatstronomyPlugin.RunProfileChangeRestartAsync(
                provider,
                gate,
                () => initialized,
                () =>
                {
                    Interlocked.Increment(ref stopCount);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    Interlocked.Increment(ref startCount);
                    return Task.CompletedTask;
                });

            initialized = false;
            provider.RotateDirectSession();
            gate.Release();
            await restart;

            AssertTrue(profileSession.IsCancellationRequested);
            AssertEqual(0, stopCount);
            AssertEqual(0, startCount);
        }

        // Teardown can also begin while a profile callback is already
        // stopping the predecessor transport. The second stale guard must
        // prevent the callback from starting its replacement.
        using (var gate = new SemaphoreSlim(1, 1))
        {
            var initialized = true;
            var stopEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStop = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var startCount = 0;
            var restart = ChatstronomyPlugin.RunProfileChangeRestartAsync(
                provider,
                gate,
                () => initialized,
                async () =>
                {
                    stopEntered.SetResult();
                    await releaseStop.Task;
                },
                _ =>
                {
                    Interlocked.Increment(ref startCount);
                    return Task.CompletedTask;
                });

            await stopEntered.Task;
            initialized = false;
            provider.RotateDirectSession();
            releaseStop.SetResult();
            await restart;

            AssertEqual(0, startCount);
            AssertEqual(1, gate.CurrentCount);
        }
    }

    private static void InitializationResynchronizesActiveProfile()
    {
        var gate = new object();
        var activeProfile = "old-profile";
        string? synchronizedProfile = null;
        var initialized = false;

        // Model ProfileChanged firing while PluginBase.Initialize is awaited:
        // the active profile has changed before the completion boundary runs.
        activeProfile = "new-profile";
        ChatstronomyPlugin.CompleteInitializationProfileBoundary(
            gate,
            synchronizeActiveProfile: () => synchronizedProfile = activeProfile,
            markInitialized: () => initialized = true);

        AssertTrue(initialized);
        AssertEqual("new-profile", synchronizedProfile);
    }

    private static async Task DirectSessionRotationPreservesProfileWork()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-transport-rotation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                autofocusReportDirectory: reportDirectory);
            var profileSession = provider.ProfileSessionToken;
            var directSession = provider.DirectSessionToken;
            var directSessionId = provider.DirectSessionId;
            var autofocusGeneration = GetAutofocusCaptureGeneration(provider);
            var commandGeneration = GetCommandGeneration(provider);
            var timestamp = new DateTime(2026, 8, 26, 4, 0, 0, DateTimeKind.Utc);
            var profileId = Guid.NewGuid();
            var completion = new DirectAutofocusCompletion(
                "Ha",
                Position: 4_200,
                Temperature: -5,
                timestamp,
                profileId,
                ChatEnabled: true);
            SetPendingAutofocusCompletion(provider, completion);
            var capture = typeof(NinaDirectDataProvider).GetMethod(
                "CaptureCompletedAutofocusAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Autofocus capture task was not found.");
            var pending = (Task)capture.Invoke(
                provider,
                new object[]
                {
                    completion,
                    autofocusGeneration,
                    CancellationToken.None,
                    profileSession,
                })!;

            await Task.Delay(150);
            provider.RotateDirectSession();
            var reportPath = Path.Combine(
                reportDirectory,
                $"{timestamp:yyyy-MM-dd--HH-mm-ss}--{profileId:D}.json");
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "Ha",
                    CalculatedFocusPoint = new { Position = 4_200 },
                }));

            var completed = await Task.WhenAny(pending, Task.Delay(1_500));
            AssertTrue(ReferenceEquals(pending, completed));
            await pending;

            AssertTrue(directSession.IsCancellationRequested);
            AssertFalse(provider.DirectSessionId == directSessionId);
            AssertFalse(profileSession.IsCancellationRequested);
            AssertEqual(autofocusGeneration, GetAutofocusCaptureGeneration(provider));
            AssertEqual(commandGeneration, GetCommandGeneration(provider));
            _ = await provider.ExecuteAsync(
                new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
                CancellationToken.None);
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task ProfileHistoryGenerationRejectsStaleWrites()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        var capture = typeof(NinaDirectDataProvider).GetMethod(
            "CaptureHistoryGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History generation reader was not found.");
        var add = typeof(NinaDirectDataProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "AddEvent"
                && method.GetParameters().Length == 3
                && method.GetParameters()[0].ParameterType == typeof(long));
        long Generation() => (long)capture.Invoke(provider, null)!;
        void Add(long generation, string marker) => add.Invoke(
            provider,
            new object[]
            {
                generation,
                "SAFETY-CHANGED",
                new (string Name, object? Value)[] { ("Marker", marker) },
            });

        var predecessor = Generation();
        provider.RevokeProfileAccess();
        Add(predecessor, "PRIVATE_PREDECESSOR_CALLBACK");
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        provider.Reset();
        var successor = Generation();
        Add(successor, "successor-callback");
        var events = await SnapshotEvents(provider);
        AssertEqual(1, events.Length);
        AssertEqual("successor-callback", events[0].GetProperty("Marker").GetString());
    }

    private static async Task LateBackgroundRecordsCannotCrossProfiles()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { NinaLogInformation = true });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var type = typeof(NinaDirectDataProvider);
        var capture = type.GetMethod(
            "CaptureHistoryGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History generation reader was not found.");
        var recordLog = type.GetMethod(
            "RecordLog",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Log recorder was not found.");
        var recordNotification = type.GetMethod(
            "RecordNotification",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Notification recorder was not found.");
        var predecessorGeneration = (long)capture.Invoke(provider, null)!;

        provider.RevokeProfileAccess();
        provider.Reset();
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.Now,
                "INFORMATION",
                "OldProfile",
                "Tail",
                1,
                "private predecessor line"),
            predecessorGeneration,
        });
        recordNotification.Invoke(provider, new object[]
        {
            new NinaNotificationRecord(
                DateTime.Now,
                "WARNING",
                "Old profile",
                "private predecessor popup"),
            predecessorGeneration,
        });
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        var successorGeneration = (long)capture.Invoke(provider, null)!;
        recordLog.Invoke(provider, new object[]
        {
            new NinaLogRecord(
                DateTime.Now,
                "INFORMATION",
                "NewProfile",
                "Tail",
                2,
                "successor line"),
            successorGeneration,
        });
        recordNotification.Invoke(provider, new object[]
        {
            new NinaNotificationRecord(
                DateTime.Now,
                "WARNING",
                "New profile",
                "successor popup"),
            successorGeneration,
        });
        AssertEqual(2, (await SnapshotEvents(provider)).Length);
    }

    private static async Task DirectSessionRotationsReplayUnseenHistory()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { NinaLogInformation = true });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);

        RecordInternalEvent(provider, "AUTOFOCUS-FINISHED", "autofocus-before");
        RecordInternalEvent(provider, "SAFETY-CHANGED", "safety-before");
        RecordInternalLog(provider, "log-before");
        AddInternalImage(provider, chatEnabled: true, value: 101);

        var firstSession = provider.DirectSessionToken;
        var originalEvents = await SnapshotTrackedEvents(provider, firstSession);
        var originalImages = await SnapshotTrackedImages(provider, firstSession);
        AssertEqual(3, originalEvents.Items.Length);
        AssertEqual(1, originalImages.Items.Length);

        RecordInternalEvent(provider, "AUTOFOCUS-FINISHED", "autofocus-during");
        RecordInternalEvent(provider, "SAFETY-CHANGED", "safety-during");
        RecordInternalLog(provider, "log-during");
        AddInternalImage(provider, chatEnabled: true, value: 202);

        // The first replacement updater must receive only the predecessor's
        // confirmed baseline. Rotate it again before its second poll to prove
        // the inherited cursor remains per-session rather than being reset to
        // the current history tail.
        provider.RotateDirectSession();
        var secondSession = provider.DirectSessionToken;
        var secondBaselineEvents = await SnapshotTrackedEvents(provider, secondSession);
        var secondBaselineImages = await SnapshotTrackedImages(provider, secondSession);
        AssertHistoryContainsOnlyBeforeReconnect(secondBaselineEvents.Items);
        AssertEqual(101, secondBaselineImages.Items.Single().GetProperty("Gain").GetInt32());

        provider.RotateDirectSession();
        var thirdSession = provider.DirectSessionToken;
        var thirdBaselineEvents = await SnapshotTrackedEvents(provider, thirdSession);
        var thirdBaselineImages = await SnapshotTrackedImages(provider, thirdSession);
        AssertHistoryContainsOnlyBeforeReconnect(thirdBaselineEvents.Items);
        AssertEqual(101, thirdBaselineImages.Items.Single().GetProperty("Gain").GetInt32());

        var replayedEvents = await SnapshotTrackedEvents(provider, thirdSession);
        var replayedImages = await SnapshotTrackedImages(provider, thirdSession);
        AssertEqual(6, replayedEvents.Items.Length);
        AssertTrue(replayedEvents.Items.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString() == "autofocus-during"));
        AssertTrue(replayedEvents.Items.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString() == "safety-during"));
        AssertTrue(replayedEvents.Items.Any(item =>
            item.TryGetProperty("Message", out var message)
            && message.GetString() == "log-during"));
        AssertEqual(2, replayedImages.Items.Length);
        AssertEqual(202, replayedImages.Items[1].GetProperty("Gain").GetInt32());

        foreach (var wire in new[]
        {
            secondBaselineEvents.Wire,
            secondBaselineImages.Wire,
            replayedEvents.Wire,
            replayedImages.Wire,
        })
        {
            AssertFalse(wire.Contains("cursor", StringComparison.OrdinalIgnoreCase));
            AssertFalse(wire.Contains("watermark", StringComparison.OrdinalIgnoreCase));
            AssertFalse(wire.Contains("historysequence", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task DirectSessionRotationsRetainPendingAutofocusDelivery()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        var firstSession = provider.DirectSessionToken;
        AssertEqual(0, (await SnapshotTrackedEvents(provider, firstSession)).Items.Length);

        RecordInternalEvent(provider, "AUTOFOCUS-FINISHED", "pending-report");
        AssertEqual(1, (await SnapshotTrackedEvents(provider, firstSession)).Items.Length);

        // The history response has reached the old transport, but its updater
        // has not yet obtained LastAutofocus. A replacement baseline must hide
        // the event once and the next poll must expose it for a fresh retry.
        provider.RotateDirectSession();
        var secondSession = provider.DirectSessionToken;
        AssertEqual(0, (await SnapshotTrackedEvents(provider, secondSession)).Items.Length);
        AssertEqual(1, (await SnapshotTrackedEvents(provider, secondSession)).Items.Length);

        var failedReportQuery = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.LastAutofocus);
        await AssertThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(
            failedReportQuery,
            CancellationToken.None,
            secondSession));

        // A failed report fetch is not an acknowledgement. The pending event
        // survives another complete Direct generation.
        provider.RotateDirectSession();
        var thirdSession = provider.DirectSessionToken;
        AssertEqual(0, (await SnapshotTrackedEvents(provider, thirdSession)).Items.Length);
        AssertEqual(1, (await SnapshotTrackedEvents(provider, thirdSession)).Items.Length);

        var timestamp = new DateTime(2026, 8, 26, 5, 0, 0, DateTimeKind.Utc);
        var completion = new DirectAutofocusCompletion(
            "L",
            Position: 3_500,
            Temperature: -4,
            timestamp,
            ProfileId: null,
            ChatEnabled: true);
        SetPendingAutofocusCompletion(provider, completion);
        AssertTrue(provider.TryCacheObservedAutofocusReport(
            JsonSerializer.SerializeToElement(new
            {
                Timestamp = timestamp,
                Filter = "L",
                CalculatedFocusPoint = new { Position = 3_500 },
            }),
            GetAutofocusCaptureGeneration(provider),
            chatEnabledAtCompletion: true));
        var successfulReportQuery = new DirectQuery(
            Guid.NewGuid(),
            DirectQueryKind.LastAutofocus);
        _ = await provider.ExecuteAsync(
            successfulReportQuery,
            CancellationToken.None,
            thirdSession);
        provider.ConfirmDirectQueryResponse(successfulReportQuery, thirdSession);

        // A successful report read is still not proof that chart rendering or
        // the chat upload completed. Keep the latest AF completion at-least-
        // once across a later forced rotation too.
        provider.RotateDirectSession();
        var acknowledgedSession = provider.DirectSessionToken;
        AssertEqual(
            0,
            (await SnapshotTrackedEvents(provider, acknowledgedSession)).Items.Length);
        AssertEqual(
            1,
            (await SnapshotTrackedEvents(provider, acknowledgedSession)).Items.Length);
    }

    private static async Task DirectSessionRotationsReplayLastWrittenDelta()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { NinaLogInformation = true });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var firstSession = provider.DirectSessionToken;
        AssertEqual(0, (await SnapshotTrackedEvents(provider, firstSession)).Items.Length);
        AssertEqual(0, (await SnapshotTrackedImages(provider, firstSession)).Items.Length);

        RecordInternalEvent(provider, "SAFETY-CHANGED", "last-written-safety");
        RecordInternalLog(provider, "last-written-log");
        AddInternalImage(provider, chatEnabled: true, value: 77);
        AssertEqual(2, (await SnapshotTrackedEvents(provider, firstSession)).Items.Length);
        AssertEqual(1, (await SnapshotTrackedImages(provider, firstSession)).Items.Length);

        // The response write can win a race with transport cancellation while
        // the peer is still parsing or uploading. Rewind one poll so the new
        // updater gets an empty baseline and treats that last delta as new.
        provider.RotateDirectSession();
        var replacement = provider.DirectSessionToken;
        AssertEqual(0, (await SnapshotTrackedEvents(provider, replacement)).Items.Length);
        AssertEqual(0, (await SnapshotTrackedImages(provider, replacement)).Items.Length);
        var events = await SnapshotTrackedEvents(provider, replacement);
        var images = await SnapshotTrackedImages(provider, replacement);
        AssertEqual(2, events.Items.Length);
        AssertTrue(events.Items.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString() == "last-written-safety"));
        AssertTrue(events.Items.Any(item =>
            item.TryGetProperty("Message", out var message)
            && message.GetString() == "last-written-log"));
        AssertEqual(77, images.Items.Single().GetProperty("Gain").GetInt32());
    }

    private static async Task PhysicalDirectReconnectsReplayLastDelta()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        var sessionId = provider.DirectSessionId;
        var session = provider.DirectSessionToken;
        provider.BeginDirectTransport(session);
        AssertEqual(0, (await SnapshotTrackedEvents(provider, session)).Items.Length);

        RecordInternalEvent(provider, "AUTOFOCUS-FINISHED", "restart-pending-report");
        AssertEqual(1, (await SnapshotTrackedEvents(provider, session)).Items.Length);

        // Simulate a Hub process disappearing after it received EventHistory
        // but before LastAutofocus/chart delivery, then authenticating again.
        provider.BeginDirectTransport(session);
        AssertEqual(sessionId, provider.DirectSessionId);
        AssertEqual(0, (await SnapshotTrackedEvents(provider, session)).Items.Length);
        var replayed = await SnapshotTrackedEvents(provider, session);
        AssertEqual(1, replayed.Items.Length);
        AssertEqual(
            "restart-pending-report",
            replayed.Items[0].GetProperty("Marker").GetString());
    }

    private static void AssertHistoryContainsOnlyBeforeReconnect(JsonElement[] events)
    {
        AssertEqual(3, events.Length);
        AssertTrue(events.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString() == "autofocus-before"));
        AssertTrue(events.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString() == "safety-before"));
        AssertTrue(events.Any(item =>
            item.TryGetProperty("Message", out var message)
            && message.GetString() == "log-before"));
        AssertFalse(events.Any(item =>
            item.TryGetProperty("Marker", out var marker)
            && marker.GetString()?.EndsWith("-during", StringComparison.Ordinal) == true));
    }

    private static async Task<TrackedHistory> SnapshotTrackedEvents(
        NinaDirectDataProvider provider,
        CancellationToken directSessionToken)
    {
        var query = new DirectQuery(Guid.NewGuid(), DirectQueryKind.EventHistory);
        var history = await provider.ExecuteAsync(
            query,
            CancellationToken.None,
            directSessionToken);
        var wire = JsonSerializer.Serialize(history, DirectProtocol.JsonOptions);
        provider.ConfirmDirectQueryResponse(query, directSessionToken);
        using var response = JsonDocument.Parse(wire);
        return new TrackedHistory(
            response.RootElement.GetProperty("Response")
                .EnumerateArray()
                .Select(item => item.Clone())
                .ToArray(),
            wire);
    }

    private static async Task<TrackedHistory> SnapshotTrackedImages(
        NinaDirectDataProvider provider,
        CancellationToken directSessionToken)
    {
        var query = new DirectQuery(Guid.NewGuid(), DirectQueryKind.ImageHistory);
        var history = await provider.ExecuteAsync(
            query,
            CancellationToken.None,
            directSessionToken);
        var wire = JsonSerializer.Serialize(history, DirectProtocol.JsonOptions);
        provider.ConfirmDirectQueryResponse(query, directSessionToken);
        using var response = JsonDocument.Parse(wire);
        return new TrackedHistory(
            response.RootElement.GetProperty("Response")
                .EnumerateArray()
                .Select(item => item.Clone())
                .ToArray(),
            wire);
    }

    private sealed record TrackedHistory(JsonElement[] Items, string Wire);

    private static void DirectSequenceHidesDisabledTargetContainers()
    {
        var method = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");
        var target = CreateUninitializedNinaTarget(
            "PRIVATE_CUSTOM_CONTAINER_IDENTITY",
            "PRIVATE_TARGET_NAME");
        target.Items.Add(new global::NINA.Plugin.SequencerPlus.WaitUntil
        {
            WaitInterval = TimeSpan.FromSeconds(13),
        });

        var shared = (Dictionary<string, object?>)method.Invoke(
            null,
            new object?[] { target, DirectEventDeliveryOptions.Default, null })!;
        AssertEqual("PRIVATE_TARGET_NAME", shared["TargetName"] as string);
        AssertTrue((shared["Name"] as string)!
            .Contains("PRIVATE_CUSTOM_CONTAINER_IDENTITY", StringComparison.Ordinal));

        var projected = (Dictionary<string, object?>)method.Invoke(
            null,
            new object?[]
            {
                target,
                DirectEventDeliveryOptions.Default with { TargetScheduler = false },
                null,
            })!;
        AssertEqual("_Container", projected["Name"] as string);
        AssertEqual("SUPPRESSED", projected["Status"] as string);
        AssertEqual(false, Convert.ToBoolean(projected["ChatEnabled"]));
        AssertFalse(projected.ContainsKey("TargetName"));
        AssertEqual(true, Convert.ToBoolean(projected["IsTargetContainer"]));
        AssertFalse(projected.ContainsKey("Conditions"));
        AssertFalse(projected.ContainsKey("Triggers"));

        var children = AssertType<Dictionary<string, object?>[]>(projected["Items"]!);
        AssertEqual(1, children.Length);
        AssertEqual("condition_wait", children[0]["OperationKind"] as string);
        AssertEqual(TimeSpan.FromSeconds(13), (TimeSpan)children[0]["WaitInterval"]!);
        var wire = JsonSerializer.Serialize(projected, DirectProtocol.JsonOptions);
        AssertFalse(wire.Contains("PRIVATE_CUSTOM_CONTAINER_IDENTITY", StringComparison.Ordinal));
        AssertFalse(wire.Contains("PRIVATE_TARGET_NAME", StringComparison.Ordinal));
    }

    private static global::NINA.Sequencer.Container.DeepSkyObjectContainer
        CreateUninitializedNinaTarget(string name, string targetName)
    {
        var target = (global::NINA.Sequencer.Container.DeepSkyObjectContainer)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(global::NINA.Sequencer.Container.DeepSkyObjectContainer));
        var containerType = typeof(global::NINA.Sequencer.Container.SequenceContainer);
        containerType.GetProperty("Items")!.SetValue(
            target,
            new System.Collections.ObjectModel.ObservableCollection<
                global::NINA.Sequencer.SequenceItem.ISequenceItem>());
        containerType.GetProperty("Conditions")!.SetValue(
            target,
            new System.Collections.ObjectModel.ObservableCollection<
                global::NINA.Sequencer.Conditions.ISequenceCondition>());
        containerType.GetProperty("Triggers")!.SetValue(
            target,
            new System.Collections.ObjectModel.ObservableCollection<
                global::NINA.Sequencer.Trigger.ISequenceTrigger>());
        target.Name = name;
        target.Target = new global::NINA.Astrometry.InputTarget(
            global::NINA.Astrometry.Angle.Zero,
            global::NINA.Astrometry.Angle.Zero,
            null!)
        {
            TargetName = targetName,
        };
        return target;
    }

    private static void SequencerPlusProxyContainersAreNotTargets()
    {
        var isTarget = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "IsActualTargetContainer",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Target classifier was not found.");
        var buildItem = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");

        var actualTarget = CreateUninitializedNinaTarget("Actual", "M42");
        AssertEqual(true, Convert.ToBoolean(isTarget.Invoke(null, new object[] { actualTarget })));

        var proxy = new global::NINA.Plugin.SequencerPlus.IfContainer();
        proxy.Add(new global::NINA.Plugin.SequencerPlus.WaitUntil
        {
            WaitInterval = TimeSpan.FromSeconds(9),
        });
        AssertEqual(false, Convert.ToBoolean(isTarget.Invoke(null, new object[] { proxy })));

        var projection = (Dictionary<string, object?>)buildItem.Invoke(
            null,
            new object?[]
            {
                proxy,
                DirectEventDeliveryOptions.Default with { TargetScheduler = false },
                null,
            })!;
        AssertFalse(projection.ContainsKey("IsTargetContainer"));
        AssertFalse(projection.ContainsKey("TargetName"));
        AssertTrue((projection["Name"] as string)!.Contains("Sequencer+ if", StringComparison.Ordinal));

        var withheld = (Dictionary<string, object?>)buildItem.Invoke(
            null,
            new object?[]
            {
                proxy,
                DirectEventDeliveryOptions.Default with
                {
                    TargetScheduler = false,
                    OtherEvents = false,
                },
                null,
            })!;
        AssertEqual(true, Convert.ToBoolean(withheld["Suppressed"]));
        var children = AssertType<Dictionary<string, object?>[]>(withheld["Items"]!);
        AssertEqual(1, children.Length);
        AssertEqual("condition_wait", children[0]["OperationKind"] as string);
    }

    private static async Task NativeLifecycleEventsUseStableConsentedPayloads()
    {
        var access = new DirectAccessPolicy(DirectAccessOptions.Default);
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            access,
            deliveryPolicy: delivery);

        var from = new global::NINA.Astrometry.Coordinates(
            4.5,
            -12.25,
            global::NINA.Astrometry.Epoch.J2000,
            global::NINA.Astrometry.Coordinates.RAType.Hours);
        var to = new global::NINA.Astrometry.Coordinates(
            5.25,
            -10.5,
            global::NINA.Astrometry.Epoch.J2000,
            global::NINA.Astrometry.Coordinates.RAType.Hours);
        await InvokeProviderCallbackAsync(
            provider,
            "TelescopeSlewed",
            new global::NINA.Equipment.Interfaces.Mediator.MountSlewedEventArgs(from, to));
        await InvokeProviderCallbackAsync(
            provider,
            "DomeSlewed",
            new global::NINA.Equipment.Interfaces.Mediator.DomeEventArgs(121.5, 128.75));
        await InvokeProviderCallbackAsync(provider, "DomeOpened", EventArgs.Empty);
        await InvokeProviderCallbackAsync(provider, "DomeConnected", EventArgs.Empty);
        await InvokeProviderCallbackAsync(
            provider,
            "FlatBrightnessChanged",
            new global::NINA.Equipment.Interfaces.Mediator.FlatDeviceBrightnessChangedEventArgs(
                17,
                23));
        await InvokeProviderCallbackAsync(provider, "WeatherConnected", EventArgs.Empty);

        var redacted = await SnapshotEvents(provider);
        AssertEqual(6, redacted.Length);
        var mount = redacted.Single(item =>
            item.GetProperty("Event").GetString() == "MOUNT-SLEWED");
        AssertEqual(4.5d, mount.GetProperty("From").GetProperty("RA").GetDouble());
        AssertEqual(5.25d, mount.GetProperty("To").GetProperty("RA").GetDouble());
        var dome = redacted.Single(item =>
            item.GetProperty("Event").GetString() == "DOME-SLEWED");
        AssertFalse(dome.TryGetProperty("FromAzimuth", out _));
        AssertFalse(dome.TryGetProperty("ToAzimuth", out _));
        AssertTrue(redacted.Any(item =>
            item.GetProperty("Event").GetString() == "DOME-SHUTTER-OPENED"));
        var flat = redacted.Single(item =>
            item.GetProperty("Event").GetString() == "FLAT-BRIGHTNESS-CHANGED");
        AssertEqual(17, flat.GetProperty("Previous").GetInt32());
        AssertEqual(23, flat.GetProperty("New").GetInt32());

        access.Update(access.Current with { ShareObservatoryLocation = true });
        var shared = await SnapshotEvents(provider);
        dome = shared.Single(item =>
            item.GetProperty("Event").GetString() == "DOME-SLEWED");
        AssertEqual(121.5d, dome.GetProperty("FromAzimuth").GetDouble());
        AssertEqual(128.75d, dome.GetProperty("ToAzimuth").GetDouble());

        delivery.Update(delivery.Current with { ObservatoryAndFlatPanel = false });
        var observatoryWithheld = await SnapshotEvents(provider);
        AssertFalse(observatoryWithheld.Any(item =>
            item.GetProperty("Event").GetString()!.StartsWith("DOME-", StringComparison.Ordinal)
            && !item.GetProperty("Event").GetString()!.EndsWith("-CONNECTED", StringComparison.Ordinal)));
        AssertFalse(observatoryWithheld.Any(item =>
            item.GetProperty("Event").GetString()!.StartsWith("FLAT-", StringComparison.Ordinal)));
        AssertTrue(observatoryWithheld.Any(item =>
            item.GetProperty("Event").GetString() == "DOME-CONNECTED"));
        AssertTrue(observatoryWithheld.Any(item =>
            item.GetProperty("Event").GetString() == "WEATHER-CONNECTED"));

        await InvokeProviderCallbackAsync(provider, "FlatClosed", EventArgs.Empty);
        delivery.Update(delivery.Current with { ObservatoryAndFlatPanel = true });
        var restored = await SnapshotEvents(provider);
        AssertFalse(restored.Any(item =>
            item.GetProperty("Event").GetString() == "FLAT-COVER-CLOSED"));

        delivery.Update(delivery.Current with { EquipmentConnections = false });
        var connectionsWithheld = await SnapshotEvents(provider);
        AssertFalse(connectionsWithheld.Any(item =>
            item.GetProperty("Event").GetString()!.EndsWith("-CONNECTED", StringComparison.Ordinal)));
        AssertTrue(connectionsWithheld.Any(item =>
            item.GetProperty("Event").GetString() == "DOME-SHUTTER-OPENED"));
    }

    private static async Task SequenceFailuresAreSafeAndTruthful()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        SetProviderStarted(provider, true);
        try
        {
            var bindRoot = typeof(NinaDirectDataProvider).GetMethod(
                "BindSequenceFailureRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Sequence failure binder was not found.");
            var root = new global::NINA.Sequencer.Container.SequenceRootContainer();
            bindRoot.Invoke(provider, new object?[] { root });

        var failedItem = new global::NINA.Plugin.SequencerPlus.UnknownPluginItem
        {
            Name = "Exposure\r\nC:\\Users\\private\\sequence.json",
        };
        var exception = new InvalidOperationException(
            "Failed writing C:\\Users\\private\\frames\\image.fit\r\nretry exhausted");
        await root.RaiseFailureEvent(failedItem, exception);
        await root.RaiseFailureEvent(failedItem, exception);
        var first = await SnapshotEvents(provider);
        AssertEqual(1, first.Length);
        AssertEqual("SEQUENCE-ENTITY-FAILED", first[0].GetProperty("Event").GetString());
        AssertEqual("UnknownPluginItem", first[0].GetProperty("EntityType").GetString());
        AssertFalse(first[0].GetRawText().Contains("Users", StringComparison.OrdinalIgnoreCase));
        AssertFalse(first[0].GetProperty("Entity").GetString()!.Contains('\n'));
        AssertFalse(first[0].GetProperty("Error").GetString()!.Contains('\n'));

        provider.Reset();
        bindRoot.Invoke(provider, new object?[] { root });
        await root.RaiseFailureEvent(failedItem, new InvalidOperationException("new generation"));
        var rebound = await SnapshotEvents(provider);
        AssertEqual(1, rebound.Length);
        AssertEqual("new generation", rebound[0].GetProperty("Error").GetString());

        provider.Reset();
        bindRoot.Invoke(provider, new object?[] { root });
        await InvokeProviderCallbackAsync(provider, "SequenceStarting", EventArgs.Empty);
        root.Status = global::NINA.Core.Enum.SequenceEntityStatus.CREATED;
        await InvokeProviderCallbackAsync(provider, "SequenceFinished", EventArgs.Empty);
        var cancelled = await SnapshotEvents(provider);
        var cancelledFinish = cancelled.Single(item =>
            item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
        AssertEqual(
            "cancelled_or_not_started",
            cancelledFinish.GetProperty("Outcome").GetString());
        AssertFalse(cancelledFinish.GetProperty("HadFailures").GetBoolean());

        provider.Reset();
        bindRoot.Invoke(provider, new object?[] { root });
        await InvokeProviderCallbackAsync(provider, "SequenceStarting", EventArgs.Empty);
        await root.RaiseFailureEvent(failedItem, new InvalidOperationException("recoverable failure"));
        root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
        await InvokeProviderCallbackAsync(provider, "SequenceFinished", EventArgs.Empty);
        var withFailure = await SnapshotEvents(provider);
        var failureFinish = withFailure.Single(item =>
            item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
        AssertEqual(
            "completed_with_failures",
            failureFinish.GetProperty("Outcome").GetString());
        AssertTrue(failureFinish.GetProperty("HadFailures").GetBoolean());

        provider.Reset();
        bindRoot.Invoke(provider, new object?[] { root });
        await InvokeProviderCallbackAsync(provider, "SequenceStarting", EventArgs.Empty);
        root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
        await InvokeProviderCallbackAsync(provider, "SequenceFinished", EventArgs.Empty);
            var successful = await SnapshotEvents(provider);
            var successfulFinish = successful.Single(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
            AssertEqual("completed", successfulFinish.GetProperty("Outcome").GetString());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task SequenceFailuresHonorEntityScopesAndConsentGaps()
    {
        var realTarget = CreateUninitializedNinaTarget("Private target", "M42");
        var scopeCases = new (
            global::NINA.Sequencer.ISequenceEntity Entity,
            DirectEventDeliveryOptions Withheld)[]
        {
            (new global::NINA.Plugin.SequencerPlus.TakeExposure(),
                DirectEventDeliveryOptions.Default with { Images = false }),
            (new global::NINA.Plugin.SequencerPlus.RunAutofocus(),
                DirectEventDeliveryOptions.Default with { Autofocus = false }),
            (new global::NINA.Plugin.SequencerPlus.StartGuiding(),
                DirectEventDeliveryOptions.Default with { Guiding = false }),
            (new global::NINA.Plugin.SequencerPlus.SlewToRADec(),
                DirectEventDeliveryOptions.Default with { Mount = false }),
            (new global::NINA.Plugin.SequencerPlus.WaitUntilSafe(),
                DirectEventDeliveryOptions.Default with { Safety = false }),
            (new global::NINA.Plugin.SequencerPlus.SwitchFilter(),
                DirectEventDeliveryOptions.Default with { FilterFocuserRotator = false }),
            (new global::NINA.Plugin.SequencerPlus.OpenDomeShutter(),
                DirectEventDeliveryOptions.Default with { ObservatoryAndFlatPanel = false }),
            (new global::NINA.Plugin.SequencerPlus.ConnectAllEquipment(),
                DirectEventDeliveryOptions.Default with { EquipmentConnections = false }),
            (new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                DirectEventDeliveryOptions.Default with { OtherEvents = false }),
            (realTarget,
                DirectEventDeliveryOptions.Default with { TargetScheduler = false }),
        };
        foreach (var (entity, withheld) in scopeCases)
        {
            AssertTrue(NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(
                entity,
                DirectEventDeliveryOptions.Default));
            AssertFalse(NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(entity, withheld));
            AssertFalse(NinaDirectSequenceSnapshot.ShouldSendSequenceFailure(
                entity,
                DirectEventDeliveryOptions.Default with { Sequence = false }));
        }

        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { Images = false });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            var bindRoot = typeof(NinaDirectDataProvider).GetMethod(
                "BindSequenceFailureRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Sequence failure binder was not found.");
            var root = new global::NINA.Sequencer.Container.SequenceRootContainer();
            bindRoot.Invoke(provider, new object?[] { root });

            // A run that begins while any failure-bearing category is hidden
            // cannot prove success even when no visible failure was observed.
            await InvokeProviderCallbackAsync(provider, "SequenceStarting", EventArgs.Empty);
            root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
            await InvokeProviderCallbackAsync(provider, "SequenceFinished", EventArgs.Empty);
            var initiallyIncomplete = await SnapshotEvents(provider);
            var initiallyIncompleteFinish = initiallyIncomplete.Single(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
            AssertEqual(
                "incomplete_provenance",
                initiallyIncompleteFinish.GetProperty("Outcome").GetString());
            AssertEqual("UNKNOWN", initiallyIncompleteFinish.GetProperty("Status").GetString());

            provider.Reset();
            bindRoot.Invoke(provider, new object?[] { root });
        var exposure = new global::NINA.Plugin.SequencerPlus.TakeExposure
        {
            Name = "Private image operation",
        };
        var failure = new InvalidOperationException("private image failure");

        await root.RaiseFailureEvent(exposure, failure);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Images = true });
        await root.RaiseFailureEvent(exposure, failure);
        var newlyConsented = await SnapshotEvents(provider);
        AssertEqual(1, newlyConsented.Length);
        AssertEqual(
            "SEQUENCE-ENTITY-FAILED",
            newlyConsented[0].GetProperty("Event").GetString());
        AssertFalse(newlyConsented[0].TryGetProperty(
            "ChatstronomyRequiredDeliveryScopes",
            out _));

        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Images = false });
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Images = true });
        AssertEqual(1, (await SnapshotEvents(provider)).Length);

        provider.Reset();
        bindRoot.Invoke(provider, new object?[] { root });
        await InvokeProviderCallbackAsync(provider, "SequenceStarting", EventArgs.Empty);

        var previous = delivery.Current;
        var disabled = previous with { Sequence = false };
        provider.EventDeliveryPolicyChanging(previous, disabled);
        delivery.Update(disabled);
        provider.EventDeliveryPolicyChanged(previous, disabled);
        await root.RaiseFailureEvent(exposure, new InvalidOperationException("withheld during gap"));
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        var enabled = disabled with { Sequence = true };
        provider.EventDeliveryPolicyChanging(disabled, enabled);
        delivery.Update(enabled);
        provider.EventDeliveryPolicyChanged(disabled, enabled);
        root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
        await InvokeProviderCallbackAsync(provider, "SequenceFinished", EventArgs.Empty);

        var afterGap = await SnapshotEvents(provider);
        AssertFalse(afterGap.Any(item =>
            item.GetProperty("Event").GetString() == "SEQUENCE-ENTITY-FAILED"));
        var finish = afterGap.Single(item =>
            item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
        AssertEqual("incomplete_provenance", finish.GetProperty("Outcome").GetString());
        AssertEqual("UNKNOWN", finish.GetProperty("Status").GetString());
            AssertFalse(finish.GetProperty("HadFailures").GetBoolean());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task SequenceLifecycleCallbacksCannotCrossProfileGenerations()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        SetProviderStarted(provider, true);
        try
        {
            var bindRoot = typeof(NinaDirectDataProvider).GetMethod(
                "BindSequenceFailureRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Sequence failure binder was not found.");
            var oldRoot = new global::NINA.Sequencer.Container.SequenceRootContainer();
            bindRoot.Invoke(provider, new object?[] { oldRoot });
            var oldFailureHandler = GetPrivateField<
                Func<object, global::NINA.Sequencer.Utility.SequenceEntityFailureEventArgs, Task>>(
                    provider,
                    "sequenceFailureHandler");
            var oldLifecycleVersion = GetPrivateField<long>(provider, "sequenceLifecycleVersion");
            var oldHistoryGeneration = GetHistoryGeneration(provider);
            var oldOwner = new SequenceOwnerStub(oldRoot);

            provider.RevokeProfileAccess();
            provider.Reset();

            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceStarting",
                oldOwner,
                oldLifecycleVersion,
                oldHistoryGeneration);
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceFinished",
                oldOwner,
                oldLifecycleVersion,
                oldHistoryGeneration);
            await oldFailureHandler(
                oldRoot,
                new global::NINA.Sequencer.Utility.SequenceEntityFailureEventArgs(
                    new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                    new InvalidOperationException("late predecessor failure")));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            var currentHistoryGeneration = GetHistoryGeneration(provider);
            var currentLifecycleVersion = GetPrivateField<long>(
                provider,
                "sequenceLifecycleVersion");
            AssertFalse(currentHistoryGeneration == oldHistoryGeneration);
            AssertFalse(currentLifecycleVersion == oldLifecycleVersion);

            var newRoot = new global::NINA.Sequencer.Container.SequenceRootContainer();
            var newOwner = new SequenceOwnerStub(newRoot);
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceStarting",
                newOwner,
                currentLifecycleVersion,
                currentHistoryGeneration);
            await newRoot.RaiseFailureEvent(
                new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                new InvalidOperationException("current generation failure"));
            newRoot.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceFinished",
                newOwner,
                currentLifecycleVersion,
                currentHistoryGeneration);

            var current = await SnapshotEvents(provider);
            AssertTrue(current.Any(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-STARTING"));
            AssertTrue(current.Any(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-ENTITY-FAILED"));
            var finish = current.Single(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
            AssertEqual("completed_with_failures", finish.GetProperty("Outcome").GetString());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task SequenceRootRefreshCannotCrossProfileGenerations()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        SetProviderStarted(provider, true);
        var releaseRead = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var refresh = typeof(NinaDirectDataProvider).GetMethod(
                "RefreshSequenceFailureRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Sequence root refresher was not found.");
            var bindRoot = typeof(NinaDirectDataProvider).GetMethod(
                "BindSequenceFailureRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Sequence failure binder was not found.");
            var readStarted = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRoot = new global::NINA.Sequencer.Container.SequenceRootContainer();
            var refreshTask = Task.Run(() => refresh.Invoke(
                provider,
                new object?[]
                {
                    new Func<global::NINA.Sequencer.Container.ISequenceRootContainer?>(() =>
                    {
                        readStarted.TrySetResult(null);
                        releaseRead.Task.GetAwaiter().GetResult();
                        return oldRoot;
                    }),
                }));
            await readStarted.Task;

            provider.RevokeProfileAccess();
            provider.Reset();
            var newRoot = new global::NINA.Sequencer.Container.SequenceRootContainer();
            bindRoot.Invoke(provider, new object?[] { newRoot });
            releaseRead.TrySetResult(null);
            await refreshTask;

            AssertTrue(ReferenceEquals(
                newRoot,
                GetPrivateField<global::NINA.Sequencer.Container.ISequenceRootContainer>(
                    provider,
                    "sequenceFailureRoot")));
            await oldRoot.RaiseFailureEvent(
                new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                new InvalidOperationException("stale root failure"));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);
            await newRoot.RaiseFailureEvent(
                new global::NINA.Plugin.SequencerPlus.UnknownPluginItem(),
                new InvalidOperationException("current root failure"));
            var current = await SnapshotEvents(provider);
            AssertEqual(1, current.Length);
            AssertEqual("current root failure", current[0].GetProperty("Error").GetString());
        }
        finally
        {
            releaseRead.TrySetResult(null);
            SetProviderStarted(provider, false);
        }
    }

    private static async Task SequenceProvenanceIsConservativeAcrossPolicyPublication()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            var root = new global::NINA.Sequencer.Container.SequenceRootContainer();
            var owner = new SequenceOwnerStub(root);
            var lifecycleVersion = GetPrivateField<long>(provider, "sequenceLifecycleVersion");
            var historyGeneration = GetHistoryGeneration(provider);
            var previous = delivery.Current;
            var disabled = previous with { Images = false };

            // Match production order: the provider sees the pending change
            // before DirectEventDeliveryPolicy publishes it. A start in this
            // exact window must already be conservative.
            provider.EventDeliveryPolicyChanging(previous, disabled);
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceStarting",
                owner,
                lifecycleVersion,
                historyGeneration);
            delivery.Update(disabled);
            provider.EventDeliveryPolicyChanged(previous, disabled);
            ApplyEventDeliveryChange(provider, delivery, previous);

            root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceFinished",
                owner,
                lifecycleVersion,
                historyGeneration);
            var incomplete = (await SnapshotEvents(provider)).Single(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
            AssertEqual("incomplete_provenance", incomplete.GetProperty("Outcome").GetString());
            AssertEqual("UNKNOWN", incomplete.GetProperty("Status").GetString());

            // A profile change publishes its policy directly, without the
            // per-setting Changed callback. Reset must rebuild the barrier
            // before rebinding so the new all-enabled profile is not stuck
            // with the predecessor's blocked state.
            ApplyEventDeliveryChange(provider, delivery, disabled);
            provider.RevokeProfileAccess();
            delivery.Update(previous);
            provider.Reset();
            lifecycleVersion = GetPrivateField<long>(provider, "sequenceLifecycleVersion");
            historyGeneration = GetHistoryGeneration(provider);
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceStarting",
                owner,
                lifecycleVersion,
                historyGeneration);
            root.Status = global::NINA.Core.Enum.SequenceEntityStatus.FINISHED;
            await InvokeSequenceLifecycleHandlerAsync(
                provider,
                "HandleSequenceFinished",
                owner,
                lifecycleVersion,
                historyGeneration);
            var complete = (await SnapshotEvents(provider)).Single(item =>
                item.GetProperty("Event").GetString() == "SEQUENCE-FINISHED");
            AssertEqual("completed", complete.GetProperty("Outcome").GetString());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task OptionalImageSaveFailuresAreObservedSafely()
    {
        var observed = new List<NinaImageSaveFailureRecord>();
        var source = new ImageSaveFailureSourceStub();
        using var watcher = new NinaImageSaveFailureWatcher(observed.Add);
        AssertTrue(watcher.Start(source));
        await source.RaiseAsync(new ImageSaveFailureArgsStub(
            ImageSaveFailureStageStub.SaveToDisk,
            true,
            new IOException("disk full"),
            "C:\\Users\\private\\image.fit"));
        AssertEqual(1, observed.Count);
        AssertEqual("SaveToDisk", observed[0].Stage);
        AssertTrue(observed[0].DiskFull);
        AssertEqual("disk full", observed[0].Error);

        watcher.Stop();
        await source.RaiseRemovedAsync(new ImageSaveFailureArgsStub(
            ImageSaveFailureStageStub.PrepareImage,
            false,
            new IOException("late callback"),
            "C:\\Users\\private\\late.fit"));
        AssertEqual(1, observed.Count);
        AssertFalse(watcher.Start(new object()));

        var throwingSource = new ImageSaveFailureSourceStub();
        using var throwingWatcher = new NinaImageSaveFailureWatcher(
            _ => throw new InvalidOperationException("observer failed"));
        AssertTrue(throwingWatcher.Start(throwingSource));
        await throwingSource.RaiseAsync(new ImageSaveFailureArgsStub(
            ImageSaveFailureStageStub.BeforeImageSaved,
            false,
            new IOException("original failure"),
            "C:\\Users\\private\\original.fit"));

        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var recordFailure = typeof(NinaDirectDataProvider).GetMethod(
            "RecordImageSaveFailure",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Image-save failure recorder was not found.");
        recordFailure.Invoke(provider, new object[]
        {
            new NinaImageSaveFailureRecord(
                "SaveToDisk",
                true,
                "Disk full at C:\\Users\\private\\frames\\image.fit"),
        });
        var shared = await SnapshotEvents(provider);
        AssertEqual(1, shared.Length);
        AssertEqual("IMAGE-SAVE-FAILED", shared[0].GetProperty("Event").GetString());
        AssertEqual("SaveToDisk", shared[0].GetProperty("Stage").GetString());
        AssertTrue(shared[0].GetProperty("DiskFull").GetBoolean());
        AssertFalse(shared[0].GetRawText().Contains("Users", StringComparison.OrdinalIgnoreCase));
        AssertFalse(shared[0].TryGetProperty("FilePath", out _));

        delivery.Update(delivery.Current with { Images = false });
        recordFailure.Invoke(provider, new object[]
        {
            new NinaImageSaveFailureRecord("PrepareImage", false, "withheld"),
        });
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        delivery.Update(delivery.Current with { Images = true });
        var restored = await SnapshotEvents(provider);
        AssertEqual(1, restored.Length);
        AssertEqual("SaveToDisk", restored[0].GetProperty("Stage").GetString());
    }

    private static async Task InvokeProviderCallbackAsync(
        NinaDirectDataProvider provider,
        string methodName,
        EventArgs args)
    {
        var method = typeof(NinaDirectDataProvider).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Provider callback '{methodName}' was not found.");
        if (method.Invoke(provider, new object[] { provider, args }) is Task task)
        {
            await task;
        }
    }

    private static async Task InvokeSequenceLifecycleHandlerAsync(
        NinaDirectDataProvider provider,
        string methodName,
        object sender,
        long lifecycleVersion,
        long historyGeneration)
    {
        var method = typeof(NinaDirectDataProvider).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Sequence lifecycle handler '{methodName}' was not found.");
        if (method.Invoke(
            provider,
            new object[]
            {
                sender,
                EventArgs.Empty,
                lifecycleVersion,
                historyGeneration,
            }) is Task task)
        {
            await task;
        }
    }

    private static void ApplyEventDeliveryChange(
        NinaDirectDataProvider provider,
        DirectEventDeliveryPolicy policy,
        DirectEventDeliveryOptions current)
    {
        var previous = policy.Current;
        provider.EventDeliveryPolicyChanging(previous, current);
        policy.Update(current);
        provider.EventDeliveryPolicyChanged(previous, current);
    }

    private static void SetProviderStarted(
        NinaDirectDataProvider provider,
        bool value) =>
        (typeof(NinaDirectDataProvider).GetField(
            "started",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Provider started state was not found."))
        .SetValue(provider, value);

    private static T GetPrivateField<T>(
        NinaDirectDataProvider provider,
        string name) =>
        (T)(typeof(NinaDirectDataProvider).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException($"Provider field '{name}' was not found."));

    private enum ImageSaveFailureStageStub
    {
        BeforeImageSaved,
        PrepareImage,
        SaveToDisk,
    }

    private sealed record ImageSaveFailureArgsStub(
        ImageSaveFailureStageStub FailureStage,
        bool IsDiskFull,
        Exception Exception,
        string FilePath);

    private sealed class ImageSaveFailureSourceStub
    {
        private Func<object, ImageSaveFailureArgsStub, Task>? handlers;
        private Func<object, ImageSaveFailureArgsStub, Task>? removedHandler;

        public event Func<object, ImageSaveFailureArgsStub, Task> ImageSaveFailed
        {
            add => handlers += value;
            remove
            {
                removedHandler = value;
                handlers -= value;
            }
        }

        internal Task RaiseAsync(ImageSaveFailureArgsStub args) =>
            handlers?.Invoke(this, args) ?? Task.CompletedTask;

        internal Task RaiseRemovedAsync(ImageSaveFailureArgsStub args) =>
            removedHandler?.Invoke(this, args) ?? Task.CompletedTask;
    }

    private sealed class SequenceOwnerStub(
        global::NINA.Sequencer.Container.ISequenceRootContainer root)
    {
        public SequenceMediatorOwnerStub Sequencer { get; } = new(root);
    }

    private sealed class SequenceMediatorOwnerStub(
        global::NINA.Sequencer.Container.ISequenceRootContainer root)
    {
        public global::NINA.Sequencer.Container.ISequenceRootContainer MainContainer { get; } = root;
    }

    private static void DirectSequenceProjectsKnownSequencerPlusOperations()
    {
        var method = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "BuildItem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence item projector was not found.");

        Dictionary<string, object?> Project(
            global::NINA.Sequencer.SequenceItem.ISequenceItem item,
            DirectEventDeliveryOptions? delivery = null) =>
            (Dictionary<string, object?>)method.Invoke(
                null,
                new object?[] { item, delivery ?? DirectEventDeliveryOptions.Default, null })!;

        foreach (var slew in new global::NINA.Sequencer.SequenceItem.ISequenceItem[]
        {
            new global::NINA.Plugin.SequencerPlus.SlewToRADec(),
            new global::NINA.Plugin.SequencerPlus.SlewToAltAz(),
        })
        {
            var projection = Project(slew);
            AssertEqual("mount_slew", projection["OperationKind"] as string);
            AssertTrue(Convert.ToBoolean(projection["ChatEnabled"]));

            var disabled = Project(
                slew,
                DirectEventDeliveryOptions.Default with { Mount = false });
            AssertFalse(Convert.ToBoolean(disabled["ChatEnabled"]));
            AssertTrue(Convert.ToBoolean(disabled["Suppressed"]));
            AssertFalse(disabled.ContainsKey("OperationKind"));
        }

        var condition = new global::NINA.Plugin.SequencerPlus.WaitUntil
        {
            WaitInterval = TimeSpan.FromSeconds(11),
        };
        var conditionProjection = Project(condition);
        AssertEqual("condition_wait", conditionProjection["OperationKind"] as string);
        AssertEqual(TimeSpan.FromSeconds(11), (TimeSpan)conditionProjection["WaitInterval"]!);
        AssertTrue(Convert.ToBoolean(conditionProjection["ChatEnabled"]));
        AssertFalse(conditionProjection.ContainsKey("Delay"));
        AssertFalse(conditionProjection.ContainsKey("Predicate"));
        AssertFalse(conditionProjection.ContainsKey("PredicateExpr"));
        AssertFalse(JsonSerializer.Serialize(conditionProjection, DirectProtocol.JsonOptions)
            .Contains("PRIVATE_", StringComparison.Ordinal));

        foreach (var manualWait in new global::NINA.Sequencer.SequenceItem.ISequenceItem[]
        {
            new global::NINA.Plugin.SequencerPlus.WaitIndefinitely(),
            new global::NINA.Plugin.SequencerPlus.Break(),
        })
        {
            var projection = Project(manualWait);
            AssertEqual("manual_wait", projection["OperationKind"] as string);
            AssertTrue(Convert.ToBoolean(projection["ChatEnabled"]));
            AssertFalse(projection.ContainsKey("Delay"));
            AssertFalse(projection.ContainsKey("Reason"));
            AssertFalse(JsonSerializer.Serialize(projection, DirectProtocol.JsonOptions)
                .Contains("PRIVATE_", StringComparison.Ordinal));

            var disabled = Project(
                manualWait,
                DirectEventDeliveryOptions.Default with { Sequence = false });
            AssertFalse(Convert.ToBoolean(disabled["ChatEnabled"]));
            AssertTrue(Convert.ToBoolean(disabled["Suppressed"]));
            AssertFalse(disabled.ContainsKey("OperationKind"));
        }

        var disabledCondition = Project(
            condition,
            DirectEventDeliveryOptions.Default with { Sequence = false });
        AssertFalse(Convert.ToBoolean(disabledCondition["ChatEnabled"]));
        AssertTrue(Convert.ToBoolean(disabledCondition["Suppressed"]));
        AssertFalse(disabledCondition.ContainsKey("OperationKind"));
        AssertFalse(disabledCondition.ContainsKey("WaitInterval"));
    }

    private static async Task SafetyMonitorTransitionsAreNormalized()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var record = typeof(NinaDirectDataProvider).GetMethod(
            "RecordSafetyState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Safety state recorder was not found.");
        var captureGeneration = typeof(NinaDirectDataProvider).GetMethod(
            "CaptureHistoryGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("History generation reader was not found.");
        void Record(bool connected, bool isSafe) =>
            record.Invoke(provider, new object[]
            {
                (long)captureGeneration.Invoke(provider, null)!,
                connected,
                isSafe,
            });

        Record(connected: false, isSafe: false); // Initial unused monitor stays quiet.
        Record(connected: true, isSafe: false);
        Record(connected: true, isSafe: false); // Duplicate polling observation.
        Record(connected: true, isSafe: true);
        Record(connected: false, isSafe: false);
        Record(connected: false, isSafe: false); // Duplicate disconnect callback.

        var events = await SnapshotEvents(provider);
        AssertEqual(4, events.Length);
        AssertEqual("SAFETY-CONNECTED", events[0].GetProperty("Event").GetString());
        AssertEqual("SAFETY-CHANGED", events[1].GetProperty("Event").GetString());
        AssertFalse(events[1].GetProperty("IsSafe").GetBoolean());
        AssertEqual("SAFETY-CHANGED", events[2].GetProperty("Event").GetString());
        AssertTrue(events[2].GetProperty("IsSafe").GetBoolean());
        AssertEqual("SAFETY-DISCONNECTED", events[3].GetProperty("Event").GetString());

        delivery.Update(delivery.Current with { Safety = false });
        Record(connected: true, isSafe: false);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        delivery.Update(DirectEventDeliveryOptions.Default);
        AssertEqual(4, (await SnapshotEvents(provider)).Length);
    }

    private static async Task ReenabledSafetySharingPublishesFreshBaseline()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { Safety = false });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var record = typeof(NinaDirectDataProvider).GetMethod(
            "RecordSafetyState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Safety state recorder was not found.");
        void Record(bool connected, bool isSafe) => record.Invoke(
            provider,
            new object[] { GetHistoryGeneration(provider), connected, isSafe });

        Record(connected: true, isSafe: false);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Safety = true });
        var unsafeBaseline = await SnapshotEvents(provider);
        AssertEqual(2, unsafeBaseline.Length);
        AssertEqual("SAFETY-CONNECTED", unsafeBaseline[0].GetProperty("Event").GetString());
        AssertEqual("SAFETY-CHANGED", unsafeBaseline[1].GetProperty("Event").GetString());
        AssertFalse(unsafeBaseline[1].GetProperty("IsSafe").GetBoolean());

        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Safety = false });
        Record(connected: true, isSafe: true);
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        ApplyEventDeliveryChange(
            provider,
            delivery,
            delivery.Current with { Safety = true });
        var safeBaseline = await SnapshotEvents(provider);
        AssertEqual(4, safeBaseline.Length);
        AssertEqual("SAFETY-CONNECTED", safeBaseline[2].GetProperty("Event").GetString());
        AssertEqual("SAFETY-CHANGED", safeBaseline[3].GetProperty("Event").GetString());
        AssertTrue(safeBaseline[3].GetProperty("IsSafe").GetBoolean());
    }

    private static async Task SafetyBaselinesCannotRaceNewerStateOrConsent()
    {
        static MethodInfo SafetyRecorder() =>
            typeof(NinaDirectDataProvider).GetMethod(
                "RecordSafetyState",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Safety state recorder was not found.");

        var transitionMediator = DispatchProxy.Create<
            ISafetyMonitorMediator,
            BlockingSafetyMonitorProxy>();
        var transitionControl = (BlockingSafetyMonitorProxy)(object)transitionMediator;
        transitionControl.ConnectedState = true;
        transitionControl.SafeState = false;
        var transitionDelivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { Safety = false });
        using (var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: transitionDelivery,
            safetyMonitor: transitionMediator))
        {
            var record = SafetyRecorder();
            void Record(bool isSafe) => record.Invoke(
                provider,
                new object[] { GetHistoryGeneration(provider), true, isSafe });

            // This hidden state is what the deliberately stale mediator read
            // will return when sharing is re-enabled.
            Record(isSafe: false);
            var enable = Task.Run(() => ApplyEventDeliveryChange(
                provider,
                transitionDelivery,
                transitionDelivery.Current with { Safety = true }));
            try
            {
                await transitionControl.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Record(isSafe: true);
            }
            finally
            {
                transitionControl.ReleaseRead.Set();
            }
            await enable;

            var events = await SnapshotEvents(provider);
            AssertTrue(events.Length >= 1);
            AssertFalse(events
                .Where(item => item.GetProperty("Event").GetString() == "SAFETY-CHANGED")
                .Any(item => !item.GetProperty("IsSafe").GetBoolean()));
            AssertTrue(events
                .Last(item => item.GetProperty("Event").GetString() == "SAFETY-CHANGED")
                .GetProperty("IsSafe")
                .GetBoolean());
        }

        var consentMediator = DispatchProxy.Create<
            ISafetyMonitorMediator,
            BlockingSafetyMonitorProxy>();
        var consentControl = (BlockingSafetyMonitorProxy)(object)consentMediator;
        consentControl.ConnectedState = true;
        consentControl.SafeState = false;
        var consentDelivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { Safety = false });
        using (var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: consentDelivery,
            safetyMonitor: consentMediator))
        {
            var enable = Task.Run(() => ApplyEventDeliveryChange(
                provider,
                consentDelivery,
                consentDelivery.Current with { Safety = true }));
            try
            {
                await consentControl.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                ApplyEventDeliveryChange(
                    provider,
                    consentDelivery,
                    consentDelivery.Current with { Safety = false });
            }
            finally
            {
                consentControl.ReleaseRead.Set();
            }
            await enable;
            AssertEqual(0, (await SnapshotEvents(provider)).Length);
        }
    }

    private static void WeatherReportingDefaultsAndRoutingAreIndependent()
    {
        var defaults = DirectEventDeliveryOptions.Default;
        AssertFalse(defaults.WeatherChanges);
        AssertFalse(defaults.HighWindAlerts);
        AssertEqual(10.0, defaults.HighWindThresholdMetersPerSecond);
        AssertFalse(defaults.ShouldSendEvent("WEATHER-CHANGED"));
        AssertFalse(defaults.ShouldSendEvent("WEATHER-HIGH-WIND"));
        AssertTrue(defaults.ShouldSendEvent("WEATHER-CONNECTED"));
        AssertTrue(defaults.OtherEvents);
        AssertTrue(typeof(IWeatherDataConsumer).IsAssignableFrom(
            typeof(NinaDirectDataProvider)));

        var changesOnly = defaults with { WeatherChanges = true };
        AssertTrue(changesOnly.ShouldSendEvent("WEATHER-CHANGED"));
        AssertFalse(changesOnly.ShouldSendEvent("WEATHER-HIGH-WIND"));
        var alertsOnly = defaults with { HighWindAlerts = true };
        AssertFalse(alertsOnly.ShouldSendEvent("WEATHER-CHANGED"));
        AssertTrue(alertsOnly.ShouldSendEvent("WEATHER-HIGH-WIND"));

        AssertEqual(
            ChatstronomySettings.DefaultHighWindThresholdMetersPerSecond,
            ChatstronomySettings.NormalizeHighWindThreshold(double.NaN));
        AssertEqual(
            ChatstronomySettings.DefaultHighWindThresholdMetersPerSecond,
            ChatstronomySettings.NormalizeHighWindThreshold(-1));
        AssertEqual(17.5, ChatstronomySettings.NormalizeHighWindThreshold(17.5));
    }

    private static async Task MeaningfulWeatherChangesAreBoundedAndSanitized()
    {
        var now = new DateTimeOffset(2026, 8, 26, 4, 0, 0, TimeSpan.Zero);
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { WeatherChanges = true });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery,
            utcNow: () => now);
        SetProviderStarted(provider, true);
        try
        {
            provider.UpdateDeviceInfo(Weather(
                temperature: 10.0,
                humidity: 40,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                temperature: 10.4,
                humidity: 40,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350));
            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                temperature: 10.8,
                humidity: 40,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            now = now.AddMinutes(1);
            var changed = Weather(
                temperature: 11.1,
                humidity: 40,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350,
                skyBrightness: double.PositiveInfinity,
                starFwhm: -1);
            changed.Name = "private weather station";
            changed.DeviceId = "private-weather-id";
            provider.UpdateDeviceInfo(changed);
            var first = (await SnapshotEvents(provider)).Single();
            AssertEqual("WEATHER-CHANGED", first.GetProperty("Event").GetString());
            AssertEqual("temperature", first.GetProperty("ChangedFields").GetString());
            AssertEqual(11.1, first.GetProperty("TemperatureCelsius").GetDouble());
            AssertFalse(first.TryGetProperty("SkyBrightnessLux", out _));
            AssertFalse(first.TryGetProperty("StarFwhmArcseconds", out _));
            AssertFalse(first.TryGetProperty("Name", out _));
            AssertFalse(first.TryGetProperty("DeviceId", out _));
            AssertFalse(JsonSerializer.Serialize(first).Contains("private", StringComparison.Ordinal));

            // A second ordinary change inside five minutes remains pending.
            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                temperature: 11.1,
                humidity: 46,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350));
            AssertEqual(1, (await SnapshotEvents(provider)).Length);

            // Rain onset bypasses the ordinary cooldown and coalesces the
            // pending humidity change into the same report.
            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                temperature: 11.1,
                humidity: 46,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0.2,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 350));
            var afterRain = await SnapshotEvents(provider);
            AssertEqual(2, afterRain.Length);
            var rainFields = afterRain[1].GetProperty("ChangedFields").GetString()!;
            AssertTrue(rainFields.Contains("humidity", StringComparison.Ordinal));
            AssertTrue(rainFields.Contains("rain rate", StringComparison.Ordinal));

            // Circular direction deltas use the shortest path: 350 -> 30 is
            // 40 degrees and is reported once wind is at least 2 m/s.
            now = now.AddMinutes(6);
            provider.UpdateDeviceInfo(Weather(
                temperature: 11.1,
                humidity: 46,
                pressure: 1_000,
                cloudCover: 20,
                rainRate: 0.2,
                windSpeed: 2,
                windGust: 2.5,
                windDirection: 30));
            var direction = (await SnapshotEvents(provider))[2];
            AssertEqual("wind direction", direction.GetProperty("ChangedFields").GetString());

            provider.Reset();
            now = now.AddMinutes(6);
            provider.UpdateDeviceInfo(Weather(
                skyBrightness: 0.001,
                starFwhm: 2.0));
            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                skyBrightness: 0.0011,
                starFwhm: 2.4));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            now = now.AddMinutes(1);
            provider.UpdateDeviceInfo(Weather(
                skyBrightness: 0.00125,
                starFwhm: 2.6));
            var skyAndSeeing = (await SnapshotEvents(provider)).Single();
            var skyAndSeeingFields = skyAndSeeing.GetProperty("ChangedFields").GetString()!;
            AssertTrue(skyAndSeeingFields.Contains("sky brightness", StringComparison.Ordinal));
            AssertTrue(skyAndSeeingFields.Contains("star FWHM", StringComparison.Ordinal));

            // A system-clock correction backwards must not extend the
            // five-minute cooldown until wall time catches up again.
            provider.Reset();
            now = new DateTimeOffset(2026, 8, 26, 6, 0, 0, TimeSpan.Zero);
            provider.UpdateDeviceInfo(Weather(temperature: 10.0));
            now = now.AddMinutes(6);
            provider.UpdateDeviceInfo(Weather(temperature: 11.1));
            now = now.AddMinutes(-10);
            provider.UpdateDeviceInfo(Weather(temperature: 12.2));
            var afterClockCorrection = await SnapshotEvents(provider);
            AssertEqual(2, afterClockCorrection.Length);
            AssertEqual(12.2, afterClockCorrection[^1]
                .GetProperty("TemperatureCelsius")
                .GetDouble());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task HighWindAlertsAreDeduplicatedAndHysteretic()
    {
        var now = new DateTimeOffset(2026, 8, 26, 5, 0, 0, TimeSpan.Zero);
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with
            {
                HighWindAlerts = true,
                HighWindThresholdMetersPerSecond = 10,
            });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery,
            utcNow: () => now);
        SetProviderStarted(provider, true);
        try
        {
            provider.UpdateDeviceInfo(Weather(windSpeed: 9));
            provider.UpdateDeviceInfo(Weather(windSpeed: 10));
            provider.UpdateDeviceInfo(Weather(windSpeed: 12));
            var alert = (await SnapshotEvents(provider)).Single();
            AssertTrue(alert.GetProperty("IsHighWind").GetBoolean());
            AssertEqual(10.0, alert.GetProperty("ThresholdMetersPerSecond").GetDouble());
            AssertEqual(10.0, alert.GetProperty("WindSpeedMetersPerSecond").GetDouble());

            // Missing the wind-speed source cannot manufacture recovery,
            // and the deadband prevents a 9.5 m/s reading from flapping.
            provider.UpdateDeviceInfo(Weather(windSpeed: double.NaN, windGust: 0));
            provider.UpdateDeviceInfo(Weather(windSpeed: 9.5));
            AssertEqual(1, (await SnapshotEvents(provider)).Length);
            provider.UpdateDeviceInfo(Weather(windSpeed: 9));
            var recovered = await SnapshotEvents(provider);
            AssertEqual(2, recovered.Length);
            AssertFalse(recovered[1].GetProperty("IsHighWind").GetBoolean());

            provider.Reset();
            provider.UpdateDeviceInfo(Weather(windSpeed: double.NaN, windGust: 11));
            AssertTrue((await SnapshotEvents(provider)).Single()
                .GetProperty("IsHighWind").GetBoolean());
            provider.UpdateDeviceInfo(Weather(windSpeed: 0, windGust: double.NaN));
            AssertEqual(1, (await SnapshotEvents(provider)).Length);
            provider.UpdateDeviceInfo(Weather(windSpeed: 0, windGust: 9));
            AssertFalse((await SnapshotEvents(provider))[1]
                .GetProperty("IsHighWind").GetBoolean());

            // A witnessed-safe reading discharges that sensor even if it is
            // unavailable later. This lets wind speed and gust alternate
            // without either a false recovery or a permanently latched alert.
            provider.Reset();
            provider.UpdateDeviceInfo(Weather(windSpeed: 20, windGust: 0));
            provider.UpdateDeviceInfo(Weather(windSpeed: 0, windGust: 20));
            provider.UpdateDeviceInfo(Weather(windSpeed: double.NaN, windGust: 0));
            var alternatingSources = await SnapshotEvents(provider);
            AssertEqual(2, alternatingSources.Length);
            AssertFalse(alternatingSources[^1].GetProperty("IsHighWind").GetBoolean());

            // Raising a threshold while high sends an explicit recovery so a
            // Hub cannot retain stale durable alert state.
            provider.Reset();
            provider.UpdateDeviceInfo(Weather(windSpeed: 12));
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindThresholdMetersPerSecond = 15 });
            provider.UpdateDeviceInfo(Weather(windSpeed: 12));
            var raised = await SnapshotEvents(provider);
            AssertEqual(2, raised.Length);
            AssertFalse(raised[1].GetProperty("IsHighWind").GetBoolean());
            AssertEqual(15.0, raised[1].GetProperty("ThresholdMetersPerSecond").GetDouble());

            // Lowering it below the same reading alerts, and a later
            // threshold-only edit while still high refreshes the durable
            // threshold without requiring another crossing.
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindThresholdMetersPerSecond = 5 });
            provider.UpdateDeviceInfo(Weather(windSpeed: 12));
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindThresholdMetersPerSecond = 10 });
            provider.UpdateDeviceInfo(Weather(windSpeed: 12));
            var refreshed = await SnapshotEvents(provider);
            AssertTrue(refreshed[^1].GetProperty("IsHighWind").GetBoolean());
            AssertEqual(10.0, refreshed[^1]
                .GetProperty("ThresholdMetersPerSecond")
                .GetDouble());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task WeatherCallbacksCannotCrossConsentBoundaries()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { EquipmentConnections = false });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            provider.UpdateDeviceInfo(Weather(
                connected: false,
                temperature: 99,
                windSpeed: 99));
            await InvokeProviderCallbackAsync(provider, "WeatherDisconnected", EventArgs.Empty);
            provider.UpdateDeviceInfo(Weather(temperature: 98, windSpeed: 98));
            await InvokeProviderCallbackAsync(provider, "WeatherConnected", EventArgs.Empty);
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            provider.UpdateDeviceInfo(Weather(temperature: 5, windSpeed: 20));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            // Enabling after a high reading captured while off starts from a
            // fresh live callback and immediately alerts; the off callback
            // itself was not retained.
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindAlerts = true });
            provider.UpdateDeviceInfo(Weather(temperature: 5, windSpeed: 20));
            var highOnly = (await SnapshotEvents(provider)).Single();
            AssertEqual("WEATHER-HIGH-WIND", highOnly.GetProperty("Event").GetString());
            AssertFalse(highOnly.TryGetProperty("TemperatureCelsius", out _));

            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with
                {
                    HighWindAlerts = false,
                    WeatherChanges = true,
                });
            provider.UpdateDeviceInfo(Weather(temperature: 5, windSpeed: 20));
            provider.UpdateDeviceInfo(Weather(temperature: 6.2, windSpeed: 20));
            var changesOnly = await SnapshotEvents(provider);
            AssertEqual(1, changesOnly.Length);
            AssertEqual("WEATHER-CHANGED", changesOnly[0].GetProperty("Event").GetString());
            AssertEqual(6.2, changesOnly[0].GetProperty("TemperatureCelsius").GetDouble());

            // The pre-publication hook closes capture before the policy value
            // changes. A callback in that window is neither sent now nor
            // released by a later re-enable.
            var previous = delivery.Current;
            var disabled = previous with { WeatherChanges = false };
            provider.EventDeliveryPolicyChanging(previous, disabled);
            provider.UpdateDeviceInfo(Weather(temperature: 20, windSpeed: 20));
            delivery.Update(disabled);
            provider.EventDeliveryPolicyChanged(previous, disabled);
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            ApplyEventDeliveryChange(provider, delivery, previous);
            provider.UpdateDeviceInfo(Weather(temperature: 20, windSpeed: 20));
            provider.UpdateDeviceInfo(Weather(temperature: 21.2, windSpeed: 20));
            var reenabled = await SnapshotEvents(provider);
            AssertEqual(1, reenabled.Length);
            AssertEqual(21.2, reenabled[0].GetProperty("TemperatureCelsius").GetDouble());

            // An earlier high-wind alert is also removed at the local consent
            // boundary. Re-enabling while wind is now low must not let a new
            // Hub session reconstruct the stale alert from bounded history.
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with
                {
                    WeatherChanges = false,
                    HighWindAlerts = true,
                });
            provider.UpdateDeviceInfo(Weather(windSpeed: 20));
            AssertTrue((await SnapshotEvents(provider)).Single()
                .GetProperty("IsHighWind")
                .GetBoolean());
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindAlerts = false });
            provider.UpdateDeviceInfo(Weather(windSpeed: 0));
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindAlerts = true });
            provider.UpdateDeviceInfo(Weather(windSpeed: 0));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);

            // A station disconnect does not claim recovery. Keeping the
            // high-wind latch independent of EquipmentConnections lets the
            // first complete low reading after reconnect clear it.
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with
                {
                    WeatherChanges = false,
                    HighWindAlerts = true,
                    EquipmentConnections = true,
            });
            provider.UpdateDeviceInfo(Weather(windSpeed: 20));
            provider.UpdateDeviceInfo(Weather(connected: false));
            await InvokeProviderCallbackAsync(provider, "WeatherDisconnected", EventArgs.Empty);
            var disconnected = await SnapshotEvents(provider);
            AssertEqual("WEATHER-DISCONNECTED", disconnected[^1]
                .GetProperty("Event")
                .GetString());
            var countBeforeReconnect = disconnected.Length;

            // N.I.N.A. broadcasts a connected snapshot before raising its
            // Connected event. The consumer records that lifecycle edge first,
            // then publishes exactly one reconciliation from the same sample.
            provider.UpdateDeviceInfo(Weather(
                windSpeed: double.NaN,
                windGust: 20));
            var reconnectedHigh = await SnapshotEvents(provider);
            AssertEqual(countBeforeReconnect + 2, reconnectedHigh.Length);
            AssertEqual("WEATHER-CONNECTED", reconnectedHigh[^2]
                .GetProperty("Event")
                .GetString());
            AssertEqual("WEATHER-HIGH-WIND", reconnectedHigh[^1]
                .GetProperty("Event")
                .GetString());
            AssertTrue(reconnectedHigh[^1]
                .GetProperty("IsHighWind")
                .GetBoolean());

            await InvokeProviderCallbackAsync(provider, "WeatherConnected", EventArgs.Empty);
            AssertEqual(reconnectedHigh.Length, (await SnapshotEvents(provider)).Length);

            // A different current sensor can authoritatively confirm the
            // still-high state. A reading inside the hysteresis band remains
            // high, and only the recovery boundary clears it.
            var countBeforeHysteresis = reconnectedHigh.Length;
            provider.UpdateDeviceInfo(Weather(
                windSpeed: double.NaN,
                windGust: 20));
            AssertEqual(countBeforeHysteresis, (await SnapshotEvents(provider)).Length);
            provider.UpdateDeviceInfo(Weather(
                windSpeed: double.NaN,
                windGust: 9.5));
            AssertEqual(countBeforeHysteresis, (await SnapshotEvents(provider)).Length);
            provider.UpdateDeviceInfo(Weather(
                windSpeed: double.NaN,
                windGust: 9));
            AssertEqual(countBeforeHysteresis, (await SnapshotEvents(provider)).Length);
            provider.UpdateDeviceInfo(Weather(
                windSpeed: 9,
                windGust: 9));
            AssertFalse((await SnapshotEvents(provider))[^1]
                .GetProperty("IsHighWind")
                .GetBoolean());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static async Task WeatherHistoryPurgesPreserveDirectReplayCursors()
    {
        var delivery = new DirectEventDeliveryPolicy(
            DirectEventDeliveryOptions.Default with { HighWindAlerts = true });
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        SetProviderStarted(provider, true);
        try
        {
            provider.UpdateDeviceInfo(Weather(windSpeed: 20));
            var firstSession = provider.DirectSessionToken;
            AssertEqual(1, (await SnapshotTrackedEvents(provider, firstSession)).Items.Length);

            // Production rotates first, then closes/purges the category. The
            // successor's inherited sequence cursor must tolerate the gap.
            provider.RotateDirectSession();
            var disabledSession = provider.DirectSessionToken;
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindAlerts = false });
            AssertEqual(
                0,
                (await SnapshotTrackedEvents(provider, disabledSession)).Items.Length);

            provider.RotateDirectSession();
            var reenabledSession = provider.DirectSessionToken;
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { HighWindAlerts = true });
            provider.UpdateDeviceInfo(Weather(windSpeed: 0));
            provider.UpdateDeviceInfo(Weather(windSpeed: 20));

            // The first successor response is the replay-safe baseline; its
            // next delta must expose the new post-gap alert exactly once.
            AssertEqual(
                0,
                (await SnapshotTrackedEvents(provider, reenabledSession)).Items.Length);
            var newAlert = (await SnapshotTrackedEvents(provider, reenabledSession)).Items;
            AssertEqual(1, newAlert.Length);
            AssertEqual("WEATHER-HIGH-WIND", newAlert[0].GetProperty("Event").GetString());
            AssertTrue(newAlert[0].GetProperty("IsHighWind").GetBoolean());
        }
        finally
        {
            SetProviderStarted(provider, false);
        }
    }

    private static WeatherDataInfo Weather(
        bool connected = true,
        double temperature = double.NaN,
        double dewPoint = double.NaN,
        double humidity = double.NaN,
        double pressure = double.NaN,
        double cloudCover = double.NaN,
        double rainRate = double.NaN,
        double windSpeed = double.NaN,
        double windGust = double.NaN,
        double windDirection = double.NaN,
        double skyTemperature = double.NaN,
        double skyBrightness = double.NaN,
        double skyQuality = double.NaN,
        double starFwhm = double.NaN) => new()
    {
        Connected = connected,
        Temperature = temperature,
        DewPoint = dewPoint,
        Humidity = humidity,
        Pressure = pressure,
        CloudCover = cloudCover,
        RainRate = rainRate,
        WindSpeed = windSpeed,
        WindGust = windGust,
        WindDirection = windDirection,
        SkyTemperature = skyTemperature,
        SkyBrightness = skyBrightness,
        SkyQuality = skyQuality,
        StarFWHM = starFwhm,
    };

    private static async Task AutofocusCompletionWaitsForMatchingReport()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var timestamp = new DateTime(2026, 8, 25, 4, 15, 0, DateTimeKind.Utc);
            var profileId = Guid.NewGuid();
            var completion = new DirectAutofocusCompletion(
                "L",
                Position: 4_068,
                Temperature: -8.5,
                timestamp,
                profileId,
                ChatEnabled: true);
            var stalePath = Path.Combine(
                reportDirectory,
                $"2026-08-25--04-13-30--{profileId:D}.json");
            await File.WriteAllTextAsync(
                stalePath,
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp.AddSeconds(-90),
                    Filter = "L",
                    CalculatedFocusPoint = new { Position = 4_068 },
                }));

            // A report from another simultaneously active N.I.N.A. profile
            // can otherwise match every payload field. It must never satisfy
            // this profile's completion.
            var otherProfilePath = Path.Combine(
                reportDirectory,
                $"2026-08-25--04-15-00--{Guid.NewGuid():D}.json");
            await File.WriteAllTextAsync(
                otherProfilePath,
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "L",
                    Temperature = -8.5,
                    CalculatedFocusPoint = new { Position = 4_068 },
                    MeasurePoints = new[] { new { Position = 1, Value = 99.0 } },
                }));

            var pending = NinaDirectDataProvider.ReadCompletedAutofocusReportAsync(
                reportDirectory,
                completion,
                CancellationToken.None);
            await Task.Delay(150);
            var matchingPath = Path.Combine(
                reportDirectory,
                $"2026-08-25--04-15-01--{profileId:D}.json");
            await File.WriteAllTextAsync(
                matchingPath,
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "L",
                    Temperature = -8.5,
                    CalculatedFocusPoint = new { Position = 4_068 },
                    MeasurePoints = new[]
                    {
                        new { Position = 4_050, Value = 2.8, Error = 0.1 },
                        new { Position = 4_068, Value = 2.4, Error = 0.1 },
                    },
                }));

            var report = await pending;
            AssertEqual(4_068.0, report
                .GetProperty("CalculatedFocusPoint")
                .GetProperty("Position")
                .GetDouble());
            AssertEqual(2, report.GetProperty("MeasurePoints").GetArrayLength());
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task HocusFocusReportsAreProjectedSafely()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-hocus-focus-report-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var timestamp = DateTimeOffset.Parse(
                "2026-08-26T22:15:30.1250000-07:00").DateTime;
            var completion = new DirectAutofocusCompletion(
                "L",
                Position: 24_980.376175368903,
                Temperature: -0.92,
                timestamp,
                Guid.NewGuid(),
                ChatEnabled: true);
            var fixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "hocus_focus_v4_report.json");
            var profilelessPath = Path.Combine(
                reportDirectory,
                $"{timestamp:yyyy-MM-dd--HH-mm-ss}.json");
            File.Copy(fixturePath, profilelessPath);

            var report = await NinaDirectDataProvider.ReadCompletedAutofocusReportAsync(
                reportDirectory,
                completion,
                CancellationToken.None);

            AssertTrue(Math.Abs(
                report.GetProperty("CalculatedFocusPoint").GetProperty("Position").GetDouble()
                    - 24_980.376175368903) < 1e-9);
            AssertEqual("Hocus Focus", report.GetProperty("AutoFocuserName").GetString());
            AssertEqual(
                "2026-08-26T22:15:30.1250000-07:00",
                report.GetProperty("Timestamp").GetString());
            AssertEqual(1.9076325878204998, report.GetProperty("FinalHFR").GetDouble());
            AssertEqual(
                0.700941335504908,
                report.GetProperty("HyperbolicMinimumStdError").GetDouble());
            AssertEqual(
                0.00017285645568865563,
                report.GetProperty("HyperbolicReducedChiSquared").GetDouble());
            AssertEqual(
                0.5661868927592,
                report.GetProperty("HyperbolicLeaveOneOutStdError").GetDouble());
            AssertEqual(83, report.GetProperty("AcceptedStarCountMin").GetInt32());
            AssertEqual(119, report.GetProperty("AcceptedStarCountMax").GetInt32());
            AssertEqual(
                "Symmetric",
                report.GetProperty("HyperbolicFitModelChosen").GetString());
            var region = report.GetProperty("Region");
            AssertEqual(2, region.GetProperty("Index").GetInt32());
            AssertEqual(
                0.5,
                region.GetProperty("OuterBoundary").GetProperty("Width").GetDouble());
            AssertEqual(
                0.3,
                region.GetProperty("InnerCropBoundary").GetProperty("Width").GetDouble());
            AssertTrue(report.GetProperty("InitialHFRMeasured").GetBoolean());
            AssertEqual(
                "measured_validation",
                report.GetProperty("FinalHFRSource").GetString());
            var algorithm = report.GetProperty("HocusFocusAlgorithm");
            AssertEqual(
                "Hybrid (Best Fit)",
                algorithm.GetProperty("ConfiguredHyperbolicModel").GetString());
            AssertEqual(
                "Reduced χ²",
                algorithm.GetProperty("FitRejectionCriterion").GetString());
            AssertEqual(
                "Mean + outlier detection",
                algorithm.GetProperty("MeasurementAverage").GetString());
            AssertEqual(
                "Optimized",
                algorithm.GetProperty("StarDetectionMode").GetString());
            AssertEqual(2, algorithm.GetProperty("DetectionBinning").GetInt32());
            AssertEqual(5, report.GetProperty("MeasurePoints").GetArrayLength());
            AssertFalse(report.TryGetProperty("HocusFocusStarDetectionOptions", out _));
            AssertFalse(report.TryGetProperty("HocusFocusAutoFocusOptions", out _));
            AssertFalse(report.TryGetProperty("FocuserOptions", out _));

            var serialized = report.GetRawText();
            AssertFalse(serialized.Contains("observer", StringComparison.OrdinalIgnoreCase));
            AssertFalse(serialized.Contains("Observatory", StringComparison.OrdinalIgnoreCase));
            AssertFalse(serialized.Contains("private-focuser", StringComparison.OrdinalIgnoreCase));

            using var unavailableValues = JsonDocument.Parse(
                """
                {
                  "Timestamp": "2026-08-26T22:15:30.1250000-07:00",
                  "Temperature": "NaN",
                  "CalculatedFocusPoint": {
                    "Position": 24980.376175368903,
                    "Value": 1.9,
                    "Error": "NaN"
                  }
                }
                """);
            var unavailableProjection = DirectAutofocusReportProjection.Project(
                unavailableValues.RootElement);
            AssertEqual("NaN", unavailableProjection.GetProperty("Temperature").GetString());
            AssertEqual(
                "NaN",
                unavailableProjection.GetProperty("CalculatedFocusPoint")
                    .GetProperty("Error")
                    .GetString());

            // Hocus Focus 4.0.0.13 writes a profile-scoped report and publishes
            // the normal N.I.N.A. start/completion lifecycle. Prove that exact
            // route is chat-visible and queryable end to end.
            var profileScopedPath = Path.Combine(
                reportDirectory,
                $"{timestamp:yyyy-MM-dd--HH-mm-ss}--{completion.ProfileId!.Value:D}.json");
            File.Copy(fixturePath, profileScopedPath);
            var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
            var activeProfile = DispatchProxy.Create<
                global::NINA.Profile.Interfaces.IProfile,
                ActiveProfileProxy>();
            ((ActiveProfileProxy)(object)activeProfile).Id = completion.ProfileId!.Value;
            var profileService = DispatchProxy.Create<
                global::NINA.Profile.Interfaces.IProfileService,
                ActiveProfileServiceProxy>();
            ((ActiveProfileServiceProxy)(object)profileService).ActiveProfile = activeProfile;
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                deliveryPolicy: delivery,
                profileService: profileService,
                autofocusReportDirectory: reportDirectory);
            using var captureStop = new CancellationTokenSource();
            SetProviderStarted(provider, true);
            (typeof(NinaDirectDataProvider).GetField(
                "eventCaptureStop",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "Provider capture cancellation was not found."))
            .SetValue(provider, captureStop);
            try
            {
                provider.AutoFocusRunStarting();
                provider.UpdateEndAutoFocusRun(new AutoFocusInfo(
                    completion.Temperature,
                    completion.Position,
                    completion.Filter,
                    completion.Timestamp));

                JsonElement[] events = [];
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    events = await SnapshotEvents(provider);
                    if (events.Any(item =>
                        item.GetProperty("Event").GetString() == "AUTOFOCUS-FINISHED"))
                    {
                        break;
                    }
                    await Task.Delay(10);
                }
                var finished = events.Single(item =>
                    item.GetProperty("Event").GetString() == "AUTOFOCUS-FINISHED");
                AssertTrue(finished.GetProperty("ChatEnabled").GetBoolean());

                var lifecycleResponse = (DirectApiEnvelope<JsonElement>)(
                    await provider.ExecuteAsync(
                        new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
                        CancellationToken.None))!;
                AssertEqual(
                    1.9076325878204998,
                    lifecycleResponse.Response.GetProperty("FinalHFR").GetDouble());
            }
            finally
            {
                (typeof(NinaDirectDataProvider).GetField(
                    "eventCaptureStop",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "Provider capture cancellation was not found."))
                .SetValue(provider, null);
                SetProviderStarted(provider, false);
            }

            using var malformedPosition = JsonDocument.Parse(
                """
                {
                  "Timestamp": "2026-08-26T22:15:30.1250000-07:00",
                  "CalculatedFocusPoint": { "Position": "NaN" }
                }
                """);
            AssertThrows<JsonException>(() => DirectAutofocusReportProjection.Project(
                malformedPosition.RootElement));
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static void HocusFocusStarDetectionModesAreProjectedAccurately()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "hocus_focus_v4_report.json");
        var fixture = JObject.Parse(File.ReadAllText(fixturePath));
        var cases = new (bool Advanced, bool UseOptimized, bool? HasOptimized, string Mode)[]
        {
            (true, true, true, "Advanced"),
            (false, true, true, "Optimized"),
            (false, true, false, "Simple"),
            (false, true, null, "Simple"),
            (false, false, true, "Simple"),
        };

        foreach (var testCase in cases)
        {
            var report = (JObject)fixture.DeepClone();
            var detection = (JObject)report["HocusFocusStarDetectionOptions"]!;
            detection["UseAdvanced"] = testCase.Advanced;
            detection["UseOptimizedSettings"] = testCase.UseOptimized;
            if (testCase.HasOptimized is bool hasOptimized)
            {
                detection["HasOptimizedSettings"] = hasOptimized;
            }
            else
            {
                detection.Remove("HasOptimizedSettings");
            }

            using var document = JsonDocument.Parse(
                report.ToString(Newtonsoft.Json.Formatting.None));
            var projected = DirectAutofocusReportProjection.Project(document.RootElement);
            var algorithm = projected.GetProperty("HocusFocusAlgorithm");
            AssertEqual(
                testCase.Mode,
                algorithm.GetProperty("StarDetectionMode").GetString());
            if (testCase.HasOptimized is bool expected)
            {
                AssertEqual(
                    expected,
                    algorithm.GetProperty("HasOptimizedSettings").GetBoolean());
            }
            else
            {
                AssertFalse(algorithm.TryGetProperty("HasOptimizedSettings", out _));
            }

            if (testCase.HasOptimized is bool reflectedHasOptimized)
            {
                var observed = new HocusAutoFocusReportStub
                {
                    Timestamp = new DateTime(2026, 8, 27, 5, 45, 0, DateTimeKind.Utc),
                    CalculatedFocusPoint =
                        new global::NINA.WPF.Base.Utility.AutoFocus.FocusPoint
                        {
                            Position = 4_188.5,
                            Value = 2.2,
                            Error = 0.0,
                        },
                    HocusFocusStarDetectionOptions = new HocusStarDetectionOptionsStub
                    {
                        UseAdvanced = testCase.Advanced,
                        UseOptimizedSettings = testCase.UseOptimized,
                        HasOptimizedSettings = reflectedHasOptimized,
                    },
                };
                var observedAlgorithm = NinaDirectDataProvider
                    .SerializeObservedAutofocusReport(observed)
                    .GetProperty("HocusFocusAlgorithm");
                AssertEqual(
                    testCase.Mode,
                    observedAlgorithm.GetProperty("StarDetectionMode").GetString());
                AssertEqual(
                    reflectedHasOptimized,
                    observedAlgorithm.GetProperty("HasOptimizedSettings").GetBoolean());
            }
        }
    }

    private static void HocusFocusStringEnumsAreProjectedSafely()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "hocus_focus_v4_report.json");
        var report = JObject.Parse(File.ReadAllText(fixturePath));
        report["HyperbolicFitModelChosen"] = "TiltedHyperbola";
        ((JObject)report["HocusFocusAutoFocusOptions"]!)["HyperbolicFitModel"] =
            "Hybrid";
        ((JObject)report["HocusFocusAutoFocusOptions"]!)["FitRejectionCriterion"] =
            "ReducedChiSquared";
        ((JObject)report["HocusFocusStarDetectionOptions"]!)["MeasurementAverage"] =
            "MeanOutliers";

        using var document = JsonDocument.Parse(
            report.ToString(Newtonsoft.Json.Formatting.None));
        var projected = DirectAutofocusReportProjection.Project(document.RootElement);
        AssertEqual(
            "Tilted Hyperbola",
            projected.GetProperty("HyperbolicFitModelChosen").GetString());
        var algorithm = projected.GetProperty("HocusFocusAlgorithm");
        AssertEqual(
            "Hybrid (Best Fit)",
            algorithm.GetProperty("ConfiguredHyperbolicModel").GetString());
        AssertEqual(
            "Reduced χ²",
            algorithm.GetProperty("FitRejectionCriterion").GetString());
        AssertEqual(
            "Mean + outlier detection",
            algorithm.GetProperty("MeasurementAverage").GetString());
    }

    private static async Task DerivedHocusFocusReportsSurviveCacheOrdering()
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        var timestamp = new DateTime(2026, 8, 27, 5, 45, 0, DateTimeKind.Utc);
        var completion = new DirectAutofocusCompletion(
            "L",
            Position: 24_980.376175368903,
            Temperature: -0.92,
            timestamp,
            Guid.NewGuid(),
            ChatEnabled: true);
        SetPendingAutofocusCompletion(provider, completion);
        var generation = GetAutofocusCaptureGeneration(provider);

        using var fileReportDocument = JsonDocument.Parse(
            """
            {
              "Timestamp": "2026-08-27T05:45:00Z",
              "Filter": "L",
              "Temperature": -0.92,
              "CalculatedFocusPoint": {
                "Position": 24980.376175368903,
                "Value": 1.95,
                "Error": 0.03
              },
              "FinalHFR": 1.9076325878204998
            }
            """);
        AssertTrue(provider.TryCacheObservedAutofocusReport(
            fileReportDocument.RootElement,
            generation,
            chatEnabledAtCompletion: true));

        global::NINA.WPF.Base.Utility.AutoFocus.AutoFocusReport commandResult =
            new HocusAutoFocusReportStub
            {
                Timestamp = timestamp,
                Filter = "L",
                Temperature = -0.92,
                CalculatedFocusPoint = new global::NINA.WPF.Base.Utility.AutoFocus.FocusPoint
                {
                    Position = 24_980.376175368903,
                    Value = 1.95,
                    Error = 0.03,
                },
                FinalHFR = 1.9076325878204998,
                HyperbolicMinimumStdError = 0.70,
                HyperbolicReducedChiSquared = 0.0017,
                HyperbolicLeaveOneOutStdError = 0.57,
                AcceptedStarCountMin = 83,
                AcceptedStarCountMax = 119,
                HyperbolicFitModelChosen = HocusFitModelStub.TiltedHyperbola,
                Region = new HocusRegionStub
                {
                    Index = 3,
                    OuterBoundary = new HocusRatioRectStub(0.5, 0.0, 0.5, 0.5),
                    InnerCropBoundary = null,
                },
                HocusFocusAutoFocusOptions = new HocusAutoFocusOptionsStub
                {
                    ValidateHfrImprovement = false,
                    HFRImprovementThreshold = 0.2,
                    WeightedHyperbolicFitEnabled = true,
                    HyperbolicFitModel = HocusFitModelStub.Hybrid,
                    FitRejectionCriterion = HocusFitCriterionStub.ReducedChiSquared,
                    ReducedChiSquaredRejectionThreshold = 5.0,
                    MaxOutlierRejections = 1,
                    OutlierRejectionConfidence = 0.99,
                    SavePath = @"D:\Observatory\private",
                },
                HocusFocusStarDetectionOptions = new HocusStarDetectionOptionsStub
                {
                    UseAdvanced = false,
                    UseOptimizedSettings = true,
                    HasOptimizedSettings = true,
                    ModelPSF = true,
                    DetectionBinning = HocusDetectionBinningStub.Bin2,
                    MeasurementAverage = HocusMeasurementAverageStub.MeanOutliers,
                    IntermediateSavePath = @"C:\Users\observer\private",
                },
                FocuserOptions = new HocusFocuserOptionsStub
                {
                    RSquaredThreshold = 0.9,
                    Id = "ASCOM.private-focuser",
                },
                ReportPath = @"C:\Users\observer\private\HocusFocus.json",
            };
        var serializedCommandResult = NinaDirectDataProvider.SerializeObservedAutofocusReport(
            commandResult);
        AssertEqual(
            1.9076325878204998,
            serializedCommandResult.GetProperty("FinalHFR").GetDouble());
        AssertEqual(
            "Tilted Hyperbola",
            serializedCommandResult.GetProperty("HyperbolicFitModelChosen").GetString());
        AssertEqual(
            83,
            serializedCommandResult.GetProperty("AcceptedStarCountMin").GetInt32());
        AssertEqual(
            3,
            serializedCommandResult.GetProperty("Region").GetProperty("Index").GetInt32());
        AssertFalse(serializedCommandResult.GetProperty("InitialHFRMeasured").GetBoolean());
        AssertEqual(
            "fitted_estimate",
            serializedCommandResult.GetProperty("FinalHFRSource").GetString());
        var commandAlgorithm = serializedCommandResult.GetProperty("HocusFocusAlgorithm");
        AssertEqual(
            "Hybrid (Best Fit)",
            commandAlgorithm.GetProperty("ConfiguredHyperbolicModel").GetString());
        AssertEqual(
            "Reduced χ²",
            commandAlgorithm.GetProperty("FitRejectionCriterion").GetString());
        AssertEqual(
            "Optimized",
            commandAlgorithm.GetProperty("StarDetectionMode").GetString());
        AssertFalse(serializedCommandResult.TryGetProperty("ReportPath", out _));
        AssertFalse(serializedCommandResult.GetRawText().Contains(
            "observer",
            StringComparison.OrdinalIgnoreCase));

        // The command continuation can run after the file watcher. It must
        // retain derived Hocus data instead of replacing the complete cached
        // report with a base AutoFocusReport projection.
        AssertTrue(provider.TryCacheObservedAutofocusReport(
            serializedCommandResult,
            generation,
            chatEnabledAtCompletion: true));
        var response = (DirectApiEnvelope<JsonElement>)(await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
            CancellationToken.None))!;
        AssertEqual(1.9076325878204998, response.Response.GetProperty("FinalHFR").GetDouble());
        AssertFalse(response.Response.TryGetProperty("ReportPath", out _));
        AssertFalse(response.Response.GetRawText().Contains(
            "observer",
            StringComparison.OrdinalIgnoreCase));

        var baseOnlyCommandResult =
            new global::NINA.WPF.Base.Utility.AutoFocus.AutoFocusReport
            {
                Timestamp = timestamp,
                Filter = "L",
                Temperature = -0.92,
                CalculatedFocusPoint = new global::NINA.WPF.Base.Utility.AutoFocus.FocusPoint
                {
                    Position = 24_980.376175368903,
                    Value = 1.95,
                    Error = 0.03,
                },
            };
        AssertTrue(provider.TryCacheObservedAutofocusReport(
            NinaDirectDataProvider.SerializeObservedAutofocusReport(baseOnlyCommandResult),
            generation,
            chatEnabledAtCompletion: true));
        var afterBaseOnlyResult = (DirectApiEnvelope<JsonElement>)(await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
            CancellationToken.None))!;
        AssertEqual(
            1.9076325878204998,
            afterBaseOnlyResult.Response.GetProperty("FinalHFR").GetDouble());
        AssertEqual(
            "Hybrid (Best Fit)",
            afterBaseOnlyResult.Response.GetProperty("HocusFocusAlgorithm")
                .GetProperty("ConfiguredHyperbolicModel")
                .GetString());
    }

    private static async Task PartialHocusFocusEnrichmentMergesInEitherOrder()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "hocus_focus_v4_report.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var complete = DirectAutofocusReportProjection.Project(fixture.RootElement);

        // Model the two independently produced views of the same Hocus run.
        // The disk view is missing one reviewed algorithm leaf and the
        // reflected command result is missing another; only one includes the
        // optional inner region. The reflected view also contains conflicting
        // live settings, while the persisted report must remain authoritative.
        // This previously made the last continuation destructive.
        // Keep Timestamp as a JSON string while producing partial views.
        // Newtonsoft's default date coercion reserializes offsets in the host
        // time zone and can make the synthetic command result fail correlation.
        var withoutMeasurement = JsonNode.Parse(complete.GetRawText())!.AsObject();
        withoutMeasurement["HocusFocusAlgorithm"]!.AsObject()
            .Remove("MeasurementAverage");
        withoutMeasurement["Region"]!.AsObject()
            .Remove("InnerCropBoundary");
        var firstPartial = JsonSerializer.SerializeToElement(
            withoutMeasurement,
            DirectProtocol.JsonOptions);

        var withoutConfiguredModel = JsonNode.Parse(complete.GetRawText())!.AsObject();
        var reflectedAlgorithm = withoutConfiguredModel["HocusFocusAlgorithm"]!.AsObject();
        reflectedAlgorithm.Remove("ConfiguredHyperbolicModel");
        reflectedAlgorithm["HFRImprovementThreshold"] = 0.75;
        withoutConfiguredModel["Region"]!["OuterBoundary"]!["Width"] =
            0.75;
        var secondPartial = JsonSerializer.SerializeToElement(
            withoutConfiguredModel,
            DirectProtocol.JsonOptions);

        AssertEqual(
            complete.GetProperty("Timestamp").GetString(),
            firstPartial.GetProperty("Timestamp").GetString());
        AssertEqual(
            complete.GetProperty("Timestamp").GetString(),
            secondPartial.GetProperty("Timestamp").GetString());

        var calculated = complete.GetProperty("CalculatedFocusPoint");
        var timestamp = complete.GetProperty("Timestamp").GetDateTimeOffset().DateTime;
        var completion = new DirectAutofocusCompletion(
            complete.GetProperty("Filter").GetString()
                ?? throw new InvalidOperationException("The fixture filter is missing."),
            Position: calculated.GetProperty("Position").GetDouble(),
            Temperature: complete.GetProperty("Temperature").GetDouble(),
            timestamp,
            Guid.NewGuid(),
            ChatEnabled: true);

        await AssertPartialHocusFocusMerge(
            firstPartial,
            secondPartial,
            completion,
            diskFirst: true);
        await AssertPartialHocusFocusMerge(
            firstPartial,
            secondPartial,
            completion,
            diskFirst: false);
        await AssertInFlightHocusFocusMerge(
            firstPartial,
            secondPartial,
            completion);
    }

    private static async Task AssertPartialHocusFocusMerge(
        JsonElement diskProjection,
        JsonElement commandProjection,
        DirectAutofocusCompletion completion,
        bool diskFirst)
    {
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default));
        SetPendingAutofocusCompletion(provider, completion);
        var generation = GetAutofocusCaptureGeneration(provider);
        var cacheDiskProjection = typeof(NinaDirectDataProvider).GetMethod(
            "CacheAutofocusReport",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The completed autofocus cache method was not found.");
        bool CacheDiskProjection()
        {
            var arguments = new object[]
            {
                diskProjection,
                completion,
                generation,
                default(JsonElement),
            };
            return (bool)(cacheDiskProjection.Invoke(provider, arguments) ?? false);
        }

        if (diskFirst)
        {
            AssertTrue(CacheDiskProjection());
            AssertTrue(provider.TryCacheObservedAutofocusReport(
                commandProjection,
                generation,
                chatEnabledAtCompletion: true));
        }
        else
        {
            AssertTrue(provider.TryCacheObservedAutofocusReport(
                commandProjection,
                generation,
                chatEnabledAtCompletion: true));
            AssertTrue(CacheDiskProjection());
        }

        var response = (DirectApiEnvelope<JsonElement>)(await provider.ExecuteAsync(
            new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
            CancellationToken.None))!;
        AssertMergedHocusFocusResponse(response.Response);
    }

    private static async Task AssertInFlightHocusFocusMerge(
        JsonElement diskProjection,
        JsonElement commandProjection,
        DirectAutofocusCompletion completion)
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-hocus-focus-in-flight-merge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                autofocusReportDirectory: reportDirectory);
            SetPendingAutofocusCompletion(provider, completion);
            var generation = GetAutofocusCaptureGeneration(provider);

            // Start a disk lookup while the report is absent, then let the
            // reflected command continuation populate the cache before the
            // persisted projection appears. This query must return the merged
            // snapshot, not the pre-merge disk value.
            var query = provider.ExecuteAsync(
                new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus),
                CancellationToken.None);
            await Task.Delay(150);
            AssertTrue(provider.TryCacheObservedAutofocusReport(
                commandProjection,
                generation,
                chatEnabledAtCompletion: true));
            var reportPath = Path.Combine(
                reportDirectory,
                $"{completion.Timestamp:yyyy-MM-dd--HH-mm-ss}--{completion.ProfileId!.Value:D}.json");
            await File.WriteAllTextAsync(reportPath, diskProjection.GetRawText());

            var response = (DirectApiEnvelope<JsonElement>)(await query)!;
            AssertMergedHocusFocusResponse(response.Response);
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static void AssertMergedHocusFocusResponse(JsonElement response)
    {
        var algorithm = response.GetProperty("HocusFocusAlgorithm");
        AssertEqual(
            "Hybrid (Best Fit)",
            algorithm.GetProperty("ConfiguredHyperbolicModel").GetString());
        AssertEqual(
            "Mean + outlier detection",
            algorithm.GetProperty("MeasurementAverage").GetString());
        AssertEqual(
            0.15,
            algorithm.GetProperty("HFRImprovementThreshold").GetDouble());
        AssertEqual(
            0.5,
            response.GetProperty("Region")
                .GetProperty("OuterBoundary")
                .GetProperty("Width")
                .GetDouble());
        AssertEqual(
            0.3,
            response.GetProperty("Region")
                .GetProperty("InnerCropBoundary")
                .GetProperty("Width")
                .GetDouble());
    }

    private static async Task ProfileScopedAutofocusReportsPrecedeProfilelessFallbacks()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-precedence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var timestamp = new DateTime(2026, 8, 27, 3, 45, 0, DateTimeKind.Utc);
            var profileId = Guid.NewGuid();
            var completion = new DirectAutofocusCompletion(
                "L",
                Position: 4_068,
                Temperature: -5.0,
                timestamp,
                profileId,
                ChatEnabled: true);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "2026-08-27--03-45-00.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "L",
                    AutoFocuserName = "Profileless fallback",
                    CalculatedFocusPoint = new { Position = 4_068 },
                }));
            await File.WriteAllTextAsync(
                Path.Combine(
                    reportDirectory,
                    $"{timestamp.ToLocalTime().AddSeconds(1):yyyy-MM-dd--HH-mm-ss}--{profileId:D}.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "L",
                    AutoFocuserName = "Active profile",
                    CalculatedFocusPoint = new { Position = 4_068 },
                }));

            var report = await NinaDirectDataProvider.ReadCompletedAutofocusReportAsync(
                reportDirectory,
                completion,
                CancellationToken.None);

            AssertEqual("Active profile", report.GetProperty("AutoFocuserName").GetString());
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task ProfilelessAutofocusReportsRequireExactTimestamp()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-correlation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var timestamp = new DateTime(2026, 8, 27, 4, 0, 0, DateTimeKind.Utc);
            var completion = new DirectAutofocusCompletion(
                "L",
                Position: 4_068,
                Temperature: -5.0,
                timestamp,
                Guid.NewGuid(),
                ChatEnabled: true);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "2026-08-27--04-00-00.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp.AddSeconds(1),
                    Filter = "L",
                    AutoFocuserName = "Wrong-timestamp profileless report",
                    CalculatedFocusPoint = new { Position = 4_068 },
                }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await AssertThrowsAsync<OperationCanceledException>(() =>
                NinaDirectDataProvider.ReadCompletedAutofocusReportAsync(
                    reportDirectory,
                    completion,
                    timeout.Token));
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task ProfilelessAutofocusReportsRequireIdentityFields()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-identity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var timestamp = new DateTime(2026, 8, 27, 4, 15, 0, DateTimeKind.Utc);
            var completion = new DirectAutofocusCompletion(
                "L",
                Position: 4_068,
                Temperature: -5.0,
                timestamp,
                Guid.NewGuid(),
                ChatEnabled: true);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "2026-08-27--04-15-00.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    AutoFocuserName = "Unidentified profileless report",
                    CalculatedFocusPoint = new { Position = 4_068 },
                }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await AssertThrowsAsync<OperationCanceledException>(() =>
                NinaDirectDataProvider.ReadCompletedAutofocusReportAsync(
                    reportDirectory,
                    completion,
                    timeout.Token));
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task AutofocusReportsRequireProvenanceAndLiveConsent()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-consent-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                deliveryPolicy: delivery,
                autofocusReportDirectory: reportDirectory);
            var query = new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus);
            var timestamp = new DateTime(2026, 8, 25, 4, 30, 0, DateTimeKind.Utc);
            var profileId = Guid.NewGuid();

            // A plausible report on disk is not evidence that it came from a
            // run observed during this profile session.
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, $"2026-08-25--04-30-00--{profileId:D}.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp,
                    Filter = "L",
                    CalculatedFocusPoint = new { Position = 4_200 },
                }));
            await AssertThrowsAsync<InvalidOperationException>(() =>
                provider.ExecuteAsync(query, CancellationToken.None));

            var pendingTimestamp = timestamp.AddMinutes(5);
            var completion = new DirectAutofocusCompletion(
                "Ha",
                Position: 5_100,
                Temperature: -7.25,
                pendingTimestamp,
                profileId,
                ChatEnabled: true);
            SetPendingAutofocusCompletion(provider, completion);

            var delayed = provider.ExecuteAsync(query, CancellationToken.None);
            await Task.Delay(150);
            delivery.Update(delivery.Current with { Autofocus = false });
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, $"2026-08-25--04-35-00--{profileId:D}.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = pendingTimestamp,
                    Filter = "Ha",
                    CalculatedFocusPoint = new { Position = 5_100 },
                    MeasurePoints = new[] { new { Position = 5_100, Value = 2.1 } },
                }));

            // Consent is checked again after the delayed disk read, not only
            // when the Direct query first arrives.
            await AssertThrowsAsync<InvalidOperationException>(async () => await delayed);

            delivery.Update(delivery.Current with { Autofocus = true });
            _ = await provider.ExecuteAsync(query, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static async Task AutofocusCaptureHonorsConsentAndGenerations()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var query = new DirectQuery(Guid.NewGuid(), DirectQueryKind.LastAutofocus);
        var timestamp = new DateTime(2026, 8, 25, 5, 0, 0, DateTimeKind.Utc);
        var report = JsonSerializer.SerializeToElement(new
        {
            Timestamp = timestamp,
            Filter = "OIII",
            CalculatedFocusPoint = new { Position = 6_250 },
        });

        var generation = GetAutofocusCaptureGeneration(provider);
        SetPendingAutofocusCompletion(
            provider,
            new DirectAutofocusCompletion(
                "OIII",
                Position: 6_250,
                Temperature: -6.0,
                timestamp,
                ProfileId: null,
                ChatEnabled: false));

        // Re-enabling before the command-task continuation runs must not turn
        // an off-at-callback result into a shareable cached report.
        AssertFalse(provider.TryCacheObservedAutofocusReport(
            report,
            generation,
            chatEnabledAtCompletion: true));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(query, CancellationToken.None));

        provider.Reset();
        AssertFalse(provider.TryCacheObservedAutofocusReport(
            report,
            generation,
            chatEnabledAtCompletion: true));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(query, CancellationToken.None));

        generation = GetAutofocusCaptureGeneration(provider);
        AssertTrue(provider.TryCacheObservedAutofocusReport(
            report,
            generation,
            chatEnabledAtCompletion: true));
        _ = await provider.ExecuteAsync(query, CancellationToken.None);

        provider.RevokeProfileAccess();
        AssertFalse(provider.TryCacheObservedAutofocusReport(
            report,
            generation,
            chatEnabledAtCompletion: true));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(query, CancellationToken.None));

        generation = GetAutofocusCaptureGeneration(provider);
        AssertFalse(provider.TryCacheObservedAutofocusReport(
            report,
            generation,
            chatEnabledAtCompletion: false));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(query, CancellationToken.None));
    }

    private static async Task AutofocusRunSharingRequiresContinuousConsent()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        using var captureStop = new CancellationTokenSource();
        SetProviderStarted(provider, true);
        (typeof(NinaDirectDataProvider).GetField(
            "eventCaptureStop",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Provider capture cancellation was not found."))
        .SetValue(provider, captureStop);
        try
        {
            // A third-party engine that publishes only a completion gives us
            // no proof that autofocus sharing stayed enabled for the whole
            // run. Keep that result local rather than inferring consent.
            provider.UpdateEndAutoFocusRun(new AutoFocusInfo(
                temperature: -8.0,
                position: 4_225,
                filter: "L",
                timestamp: DateTime.UtcNow));
            AssertFalse((await SnapshotEvents(provider)).Any(item =>
                item.GetProperty("Event").GetString() == "AUTOFOCUS-FINISHED"));
            var completion = GetPrivateField<DirectAutofocusCompletion>(
                provider,
                "pendingAutofocusCompletion");
            AssertFalse(completion.ChatEnabled);
            provider.Reset();

            provider.AutoFocusRunStarting();
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { Autofocus = false });
            ApplyEventDeliveryChange(
                provider,
                delivery,
                delivery.Current with { Autofocus = true });
            provider.UpdateEndAutoFocusRun(new AutoFocusInfo(
                temperature: -8.0,
                position: 4_200,
                filter: "L",
                timestamp: DateTime.UtcNow));

            var withheld = await SnapshotEvents(provider);
            AssertFalse(withheld.Any(item =>
                item.GetProperty("Event").GetString() == "AUTOFOCUS-FINISHED"));
            completion = GetPrivateField<DirectAutofocusCompletion>(
                provider,
                "pendingAutofocusCompletion");
            AssertFalse(completion.ChatEnabled);

            // Disabling begins before the policy object is published. A run
            // that starts and even completes in that narrow window must not
            // retain visible start, point, completion, or report provenance.
            provider.Reset();
            var previous = delivery.Current;
            var disabled = previous with { Autofocus = false };
            provider.EventDeliveryPolicyChanging(previous, disabled);
            provider.AutoFocusRunStarting();
            provider.NewAutoFocusPoint(new OxyPlot.DataPoint(4_150, 2.3));
            provider.UpdateEndAutoFocusRun(new AutoFocusInfo(
                temperature: -8.0,
                position: 4_150,
                filter: "L",
                timestamp: DateTime.UtcNow));
            AssertEqual(0, (await SnapshotEvents(provider)).Length);
            completion = GetPrivateField<DirectAutofocusCompletion>(
                provider,
                "pendingAutofocusCompletion");
            AssertFalse(completion.ChatEnabled);
            delivery.Update(disabled);
            provider.EventDeliveryPolicyChanged(previous, disabled);
            ApplyEventDeliveryChange(provider, delivery, previous);

            // The barrier belongs to one run. A later run that begins with
            // sharing enabled starts with valid continuous provenance.
            provider.Reset();
            provider.AutoFocusRunStarting();
            AssertTrue(GetPrivateField<bool>(
                provider,
                "autofocusRunContinuouslyShareable"));
            AssertEqual(
                GetAutofocusCaptureGeneration(provider),
                GetPrivateField<long>(provider, "autofocusRunGeneration"));
        }
        finally
        {
            (typeof(NinaDirectDataProvider).GetField(
                "eventCaptureStop",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Provider capture cancellation was not found."))
            .SetValue(provider, null);
            SetProviderStarted(provider, false);
        }
    }

    private static async Task ProfileChangesCancelPendingAutofocusReads()
    {
        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "chatstronomy-autofocus-profile-cancellation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            using var provider = CreateSecurityTestProvider(
                new DirectAccessPolicy(DirectAccessOptions.Default),
                autofocusReportDirectory: reportDirectory);
            var completion = new DirectAutofocusCompletion(
                "SII",
                Position: 7_100,
                Temperature: -5.0,
                new DateTime(2026, 8, 25, 5, 30, 0, DateTimeKind.Utc),
                Guid.NewGuid(),
                ChatEnabled: true);
            var capture = typeof(NinaDirectDataProvider).GetMethod(
                "CaptureCompletedAutofocusAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Autofocus capture task was not found.");
            var pending = (Task)capture.Invoke(
                provider,
                new object[]
                {
                    completion,
                    GetAutofocusCaptureGeneration(provider),
                    CancellationToken.None,
                    provider.ProfileSessionToken,
                })!;

            await Task.Delay(150);
            provider.RevokeProfileAccess();
            var completed = await Task.WhenAny(pending, Task.Delay(500));
            AssertTrue(ReferenceEquals(pending, completed));
            await pending;
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static long GetAutofocusCaptureGeneration(NinaDirectDataProvider provider) =>
        (long)(typeof(NinaDirectDataProvider).GetField(
            "autofocusCaptureGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException("Autofocus generation was not found."));

    private static long GetCommandGeneration(NinaDirectDataProvider provider) =>
        (long)(typeof(NinaDirectDataProvider).GetField(
            "commandGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException("Command generation was not found."));

    private static long GetHistoryGeneration(NinaDirectDataProvider provider) =>
        (long)(typeof(NinaDirectDataProvider).GetField(
            "historyGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException("History generation was not found."));

    private static void SetPendingAutofocusCompletion(
        NinaDirectDataProvider provider,
        DirectAutofocusCompletion completion)
    {
        var type = typeof(NinaDirectDataProvider);
        var generation = GetAutofocusCaptureGeneration(provider);
        (type.GetField("pendingAutofocusCompletion", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pending autofocus completion was not found."))
            .SetValue(provider, completion);
        (type.GetField("pendingAutofocusGeneration", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pending autofocus generation was not found."))
            .SetValue(provider, generation);
    }

    private static void DirectGuiderPayloadMatchesRustChart()
    {
        var measured = new[]
        {
            new DirectGuideStep(1, 0.85, 1.15, -1, -2, -120, 0, 0, 80, "NO"),
            new DirectGuideStep(2, 1.85, 2.15, 1, 2, 140, 2, 4, -90, "NO"),
        };
        var rms = DirectGuideRms.FromSteps(measured, pixelScale: 2);
        AssertEqual(1.0, rms.RA);
        AssertEqual(1.0, rms.Dec);
        AssertTrue(Math.Abs(rms.Total - Math.Sqrt(2)) < 1e-12);
        AssertEqual(2.0, rms.Scale);
        AssertEqual(2, rms.DataPoints);
        AssertTrue(rms.RAText.Contains("(2.00\")", StringComparison.Ordinal));

        var steps = measured.Append(
            new DirectGuideStep(3, 2.85, 3.15, 0, 0, 0, 0, 0, 0, "0.01"))
            .ToArray();
        var graph = new DirectGuiderGraph(
            rms,
            Interval: 1.1,
            MaxY: 4.4,
            MinY: -4.4,
            MaxDurationY: 140,
            MinDurationY: -140,
            GuideSteps: steps,
            HistorySize: 500,
            PixelScale: 2,
            Scale: 1);
        var json = JsonSerializer.Serialize(
            DirectApiEnvelope<DirectGuiderGraph>.Ok(graph),
            DirectProtocol.JsonOptions);
        using var document = JsonDocument.Parse(json);
        var response = document.RootElement.GetProperty("Response");
        AssertEqual(1, response.GetProperty("Scale").GetInt32());
        AssertEqual(1.1, response.GetProperty("Interval").GetDouble());
        AssertEqual("0.01", response.GetProperty("GuideSteps")[2].GetProperty("Dither").GetString());
        AssertEqual(2, response.GetProperty("RMS").GetProperty("DataPoints").GetInt32());
    }

    private static async Task DisabledGuidingSamplesAreNotRetained()
    {
        var delivery = new DirectEventDeliveryPolicy(DirectEventDeliveryOptions.Default);
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            deliveryPolicy: delivery);
        var type = typeof(NinaDirectDataProvider);
        var recordStep = type.GetMethod(
            "GuiderGuideEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Guider sample callback was not found.");
        var recordDither = type.GetMethod(
            "GuiderDithered",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Guider dither callback was not found.");
        var history = (BoundedHistory<DirectGuideStep>)(type.GetField(
            "guideSteps",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException("Guider history was not found."));

        recordStep.Invoke(provider, new object?[] { null, new TestGuideStep(1, -1) });
        await (Task)recordDither.Invoke(provider, new object?[] { provider, EventArgs.Empty })!;
        AssertEqual(2, history.Snapshot().Count);

        delivery.Update(delivery.Current with { Guiding = false });
        recordStep.Invoke(provider, new object?[] { null, new TestGuideStep(99, -99) });
        await (Task)recordDither.Invoke(provider, new object?[] { provider, EventArgs.Empty })!;
        AssertEqual(2, history.Snapshot().Count);

        delivery.Update(delivery.Current with { Guiding = true });
        var retained = history.Snapshot();
        AssertEqual(2, retained.Count);
        AssertFalse(retained.Any(step => step.RADistanceRaw == 99));
        recordStep.Invoke(provider, new object?[] { null, new TestGuideStep(2, -2) });
        retained = history.Snapshot();
        AssertEqual(3, retained.Count);
        AssertEqual(3L, retained[^1].Id);
    }

    private sealed class HocusAutoFocusReportStub
        : global::NINA.WPF.Base.Utility.AutoFocus.AutoFocusReport
    {
        public double FinalHFR { get; init; }

        public double HyperbolicMinimumStdError { get; init; }

        public double HyperbolicReducedChiSquared { get; init; }

        public double HyperbolicLeaveOneOutStdError { get; init; }

        public int AcceptedStarCountMin { get; init; }

        public int AcceptedStarCountMax { get; init; }

        public HocusFitModelStub? HyperbolicFitModelChosen { get; init; }

        public HocusRegionStub? Region { get; init; }

        public HocusAutoFocusOptionsStub? HocusFocusAutoFocusOptions { get; init; }

        public HocusStarDetectionOptionsStub? HocusFocusStarDetectionOptions { get; init; }

        public HocusFocuserOptionsStub? FocuserOptions { get; init; }

        public string ReportPath { get; init; } = string.Empty;
    }

    private enum HocusFitModelStub
    {
        Symmetric,
        UnevenBlend,
        TiltedHyperbola,
        SmoothBlend,
        Hybrid,
    }

    private enum HocusFitCriterionStub
    {
        RSquared,
        ReducedChiSquared,
    }

    private enum HocusDetectionBinningStub
    {
        Bin1 = 1,
        Bin2 = 2,
    }

    private enum HocusMeasurementAverageStub
    {
        Median,
        MeanOutliers,
    }

    private sealed class HocusAutoFocusOptionsStub
    {
        public bool ValidateHfrImprovement { get; init; }

        public double HFRImprovementThreshold { get; init; }

        public bool WeightedHyperbolicFitEnabled { get; init; }

        public HocusFitModelStub HyperbolicFitModel { get; init; }

        public HocusFitCriterionStub FitRejectionCriterion { get; init; }

        public double ReducedChiSquaredRejectionThreshold { get; init; }

        public int MaxOutlierRejections { get; init; }

        public double OutlierRejectionConfidence { get; init; }

        public string SavePath { get; init; } = string.Empty;
    }

    private sealed class HocusStarDetectionOptionsStub
    {
        public bool UseAdvanced { get; init; }

        public bool UseOptimizedSettings { get; init; }

        public bool HasOptimizedSettings { get; init; }

        public bool ModelPSF { get; init; }

        public HocusDetectionBinningStub DetectionBinning { get; init; }

        public HocusMeasurementAverageStub MeasurementAverage { get; init; }

        public string IntermediateSavePath { get; init; } = string.Empty;
    }

    private sealed class HocusFocuserOptionsStub
    {
        public double RSquaredThreshold { get; init; }

        public string Id { get; init; } = string.Empty;
    }

    private sealed class HocusRegionStub
    {
        public int Index { get; init; }

        public HocusRatioRectStub OuterBoundary { get; init; } = null!;

        public HocusRatioRectStub? InnerCropBoundary { get; init; }
    }

    private sealed record HocusRatioRectStub(
        double StartX,
        double StartY,
        double Width,
        double Height);

    private sealed class TestGuideStep(double ra, double dec) : global::NINA.Core.Interfaces.IGuideStep
    {
        public double Frame => 0;
        public double Time => 0;
        public double RADistanceRaw { get; set; } = ra;
        public double DECDistanceRaw { get; set; } = dec;
        public double RADuration => 100;
        public double DECDuration => 100;
        public string Event => string.Empty;
        public string TimeStamp => string.Empty;
        public string Host => string.Empty;
        public int Inst => 0;
        public global::NINA.Core.Interfaces.IGuideStep Clone() =>
            new TestGuideStep(RADistanceRaw, DECDistanceRaw);
    }

    private static void DirectRuntimeBootstrapCarriesOnlyPipe()
    {
        var runtimePath = Environment.ProcessPath ?? "test-runtime.exe";
        var configuration = new ChatstronomyConfiguration(
            new DiscordWebhookDeliveryConfiguration(
                new Uri("https://discord.com/api/webhooks/123/token")),
            Matrix: null,
            new LocalRuntimeConfiguration(runtimePath));
        var json = PluginRuntimeBootstrap.Serialize(
            configuration,
            new LocalRuntimeIdentity(Guid.NewGuid(), Guid.NewGuid(), "Direct Rig"),
            directPipeName: "chatstronomy-direct-test",
            directCapabilities: new DirectCapabilities(
                EventHistory: true,
                ImageHistory: true,
                Thumbnails: true,
                Sequence: true,
                EquipmentSnapshots: true,
                AutofocusDetails: true,
                GuiderGraph: true,
                Commands: true));

        using var document = JsonDocument.Parse(json);
        var source = document.RootElement.GetProperty("source");
        AssertEqual("nina_direct", source.GetProperty("kind").GetString());
        AssertEqual("chatstronomy-direct-test", source.GetProperty("pipe_name").GetString());
        AssertTrue(source.GetProperty("capabilities").GetProperty("event_history").GetBoolean());
        AssertTrue(source.GetProperty("capabilities").GetProperty("sequence").GetBoolean());
        AssertTrue(source.GetProperty("capabilities").GetProperty("autofocus_details").GetBoolean());
        AssertTrue(source.GetProperty("capabilities").GetProperty("guider_graph").GetBoolean());
        AssertTrue(source.GetProperty("capabilities").GetProperty("commands").GetBoolean());
        AssertFalse(source.TryGetProperty("base_url", out _));
        AssertFalse(json.Contains("127.0.0.1:1888", StringComparison.Ordinal));
    }

    private static void DirectQueryResultsMatchRustEnvelope()
    {
        var id = Guid.Parse("7afcde18-b5a8-46fd-ad1f-ed54cf3bbc4e");
        var json = DirectProtocol.SerializeSuccess(
            id,
            DirectApiEnvelope<IReadOnlyList<object>>.Ok(Array.Empty<object>()));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        AssertEqual("query_result", root.GetProperty("type").GetString());
        var payload = root.GetProperty("payload");
        AssertEqual(id, payload.GetProperty("id").GetGuid());
        AssertTrue(payload.GetProperty("ok").GetBoolean());
        var envelope = payload.GetProperty("payload");
        AssertTrue(envelope.GetProperty("Success").GetBoolean());
        AssertEqual("API", envelope.GetProperty("Type").GetString());
        AssertEqual(0, envelope.GetProperty("Response").GetArrayLength());

        var failure = DirectProtocol.SerializeFailure(
            id,
            "The autofocus report is not available yet.",
            "resource_not_ready");
        using var failureDocument = JsonDocument.Parse(failure);
        var failurePayload = failureDocument.RootElement.GetProperty("payload");
        AssertFalse(failurePayload.GetProperty("ok").GetBoolean());
        AssertEqual(
            "resource_not_ready",
            failurePayload.GetProperty("error_code").GetString());
    }

    private static void DirectHistoriesAreBounded()
    {
        var history = new BoundedHistory<int>(capacity: 2);
        history.Add(1);
        history.Add(2);
        history.Add(3);

        AssertEqual(2, history.Count);
        AssertTrue(history.Snapshot().SequenceEqual(new[] { 2, 3 }));
        AssertTrue(history.TryGetAt(1, out var item));
        AssertEqual(3, item);
        AssertFalse(history.TryGetAt(2, out _));
        history.Clear();
        AssertEqual(0, history.Count);
    }

    private static void DirectImageThumbnailsAreSizedForChat()
    {
        const int sourceWidth = 2_048;
        const int sourceHeight = 1_024;
        var pixels = new byte[sourceWidth * sourceHeight];
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            sourceWidth,
            sourceHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Gray8,
            null,
            pixels,
            sourceWidth);
        source.Freeze();

        var encoded = DirectThumbnailEncoder.Encode(source);
        using var stream = new MemoryStream(encoded);
        var decoder = new System.Windows.Media.Imaging.JpegBitmapDecoder(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var thumbnail = decoder.Frames[0];

        AssertEqual(DirectThumbnailEncoder.MaxWidth, thumbnail.PixelWidth);
        AssertEqual(sourceHeight / 2, thumbnail.PixelHeight);
    }

    private static async Task ThumbnailPreparationIsBoundedAndLatestWins()
    {
        var encoderCalls = new ConcurrentQueue<int>();
        var firstEncoderStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstEncoder = new ManualResetEventSlim();
        using var provider = CreateSecurityTestProvider(
            new DirectAccessPolicy(DirectAccessOptions.Default),
            thumbnailEncoder: source =>
            {
                encoderCalls.Enqueue(source.PixelWidth);
                if (source.PixelWidth == 101)
                {
                    firstEncoderStarted.TrySetResult(null);
                    releaseFirstEncoder.Wait();
                }
                return BitConverter.GetBytes(source.PixelWidth);
            });
        using var captureStop = new CancellationTokenSource();
        SetProviderStarted(provider, true);
        (typeof(NinaDirectDataProvider).GetField(
            "eventCaptureStop",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Provider capture cancellation was not found."))
        .SetValue(provider, captureStop);
        try
        {
            var queue = typeof(NinaDirectDataProvider).GetMethod(
                "QueueThumbnail",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Thumbnail queue was not found.");
            var historyGeneration = GetHistoryGeneration(provider);
            var captureGeneration = GetPrivateField<long>(provider, "imageCaptureGeneration");
            var first = AddInternalImage(provider, chatEnabled: true, value: 101);
            var second = AddInternalImage(provider, chatEnabled: true, value: 102);
            var latest = AddInternalImage(provider, chatEnabled: true, value: 103);
            first.ThumbnailData = null;
            second.ThumbnailData = null;
            latest.ThumbnailData = null;

            System.Windows.Media.Imaging.BitmapSource Source(int width)
            {
                var source = System.Windows.Media.Imaging.BitmapSource.Create(
                    width,
                    1,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Gray8,
                    null,
                    new byte[width],
                    width);
                source.Freeze();
                return source;
            }

            queue.Invoke(provider, new object[]
            {
                Source(101), first, historyGeneration, captureGeneration,
            });
            await firstEncoderStarted.Task;
            queue.Invoke(provider, new object[]
            {
                Source(102), second, historyGeneration, captureGeneration,
            });
            queue.Invoke(provider, new object[]
            {
                Source(103), latest, historyGeneration, captureGeneration,
            });
            releaseFirstEncoder.Set();

            await WaitUntilAsync(
                () => latest.ThumbnailData is not null,
                TimeSpan.FromSeconds(2));
            AssertTrue(encoderCalls.SequenceEqual(new[] { 101, 103 }));
            AssertEqual(101, BitConverter.ToInt32(first.ThumbnailData!));
            AssertTrue(second.ThumbnailData is null);
            AssertEqual(103, BitConverter.ToInt32(latest.ThumbnailData!));
        }
        finally
        {
            releaseFirstEncoder.Set();
            (typeof(NinaDirectDataProvider).GetField(
                "eventCaptureStop",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Provider capture cancellation was not found."))
            .SetValue(provider, null);
            SetProviderStarted(provider, false);
        }
    }

    private static async Task DirectPipeServesCameraSnapshots()
    {
        var provider = new FakeDirectDataProvider();
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var server = new NinaDirectPipeServer(provider, pipeName);
        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        var id = Guid.NewGuid();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "query",
            payload = new { id, kind = "camera_info" },
        }));
        var responseLine = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("Direct camera query returned no response.");
        using var response = JsonDocument.Parse(responseLine);
        AssertEqual(id, response.RootElement.GetProperty("payload").GetProperty("id").GetGuid());
        AssertTrue(response.RootElement.GetProperty("payload").GetProperty("ok").GetBoolean());
        var camera = response.RootElement
            .GetProperty("payload")
            .GetProperty("payload")
            .GetProperty("Response");
        AssertEqual(5.0, camera.GetProperty("Temperature").GetDouble());
        AssertEqual(-10.0, camera.GetProperty("TemperatureSetPoint").GetDouble());
        AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.CameraInfo));
    }

    private static async Task HostedPluginPairsAndServesGuiderGraphs()
    {
        var hello = HostedHello();
        var queryId = Guid.NewGuid();
        var socketFactory = new ScriptedHubSocketFactory(
            PairResultJson(hello, "csrc_saved"),
            QueryJson(queryId, "guider_graph", expiresAt: 4_102_444_800));
        var provider = new FakeDirectDataProvider();
        provider.Start();
        var client = new ChatstronomyHubClient(provider, socketFactory);
        HubCredentialIssuedEventArgs? issued = null;
        client.CredentialIssued += (_, args) => issued = args;
        var configuration = new HubConnectionConfiguration(
            new Uri("https://hub.example.test/"),
            Credential: null,
            PairingToken: "cspt_once",
            hello.ProfileId);

        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(configuration, hello, CancellationToken.None));

        AssertEqual("wss://hub.example.test/v1/direct", socketFactory.Socket.Endpoint!.AbsoluteUri);
        AssertEqual("csrc_saved", issued!.Credential);
        AssertEqual(hello.ProfileId, issued.ProfileId);
        AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.GuiderGraph));

        var sent = socketFactory.Socket.SentMessages.ToArray();
        AssertTrue(sent.Length >= 3);
        using var pair = JsonDocument.Parse(sent[0]);
        AssertEqual("pair", pair.RootElement.GetProperty("type").GetString());
        AssertTrue(sent.Any(message =>
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.GetProperty("type").GetString() == "heartbeat";
        }));
        var resultJson = sent.Single(message =>
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.GetProperty("type").GetString() == "query_result";
        });
        using var result = JsonDocument.Parse(resultJson);
        var payload = result.RootElement.GetProperty("payload");
        AssertEqual(queryId, payload.GetProperty("id").GetGuid());
        AssertTrue(payload.GetProperty("ok").GetBoolean());
        var graph = payload.GetProperty("payload").GetProperty("Response");
        AssertTrue(graph.GetProperty("GuideSteps").GetArrayLength() >= 3);
        AssertEqual(2, graph.GetProperty("RMS").GetProperty("DataPoints").GetInt32());
        provider.Dispose();
    }

    private static async Task HostedCredentialTakesPrecedenceOverPairingCode()
    {
        var hello = HostedHello();
        var socketFactory = new ScriptedHubSocketFactory(AgentHelloJson(hello));
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var client = new ChatstronomyHubClient(provider, socketFactory);
            var configuration = new HubConnectionConfiguration(
                new Uri("https://hub.example.test/"),
                Credential: "csrc_existing",
                PairingToken: "cspt_already_consumed",
                hello.ProfileId);

            await AssertThrowsAsync<HubDisconnectedException>(() =>
                client.RunSingleConnectionAsync(configuration, hello, CancellationToken.None));

            using var first = JsonDocument.Parse(socketFactory.Socket.SentMessages.First());
            AssertEqual("auth", first.RootElement.GetProperty("type").GetString());
            AssertEqual(
                "csrc_existing",
                first.RootElement.GetProperty("payload").GetProperty("credential").GetString());
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedLegacyHubPayloadsAreLabeled()
    {
        var hello = HostedHello();
        var socketFactory = new ScriptedHubSocketFactory(LegacyAgentHelloJson(hello));
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var client = new ChatstronomyHubClient(provider, socketFactory);
            var statuses = new ConcurrentQueue<string>();
            client.StateChanged += (_, _) => statuses.Enqueue(client.StatusMessage);
            var configuration = new HubConnectionConfiguration(
                new Uri("https://hub.example.test/"),
                Credential: "csrc_legacy",
                PairingToken: null,
                hello.ProfileId);

            await AssertThrowsAsync<HubDisconnectedException>(() =>
                client.RunSingleConnectionAsync(configuration, hello, CancellationToken.None));

            AssertTrue(statuses.Any(status => status.Contains("legacy payload v1")));
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedConnectionAttemptsTimeOut()
    {
        foreach (var hangDuringConnect in new[] { true, false })
        {
            var hello = HostedHello();
            var provider = new FakeDirectDataProvider();
            provider.Start();
            try
            {
                var socketFactory = new HangingHubSocketFactory(hangDuringConnect);
                var client = new ChatstronomyHubClient(
                    provider,
                    socketFactory,
                    connectionAttemptTimeout: TimeSpan.FromMilliseconds(25));
                var configuration = new HubConnectionConfiguration(
                    new Uri("https://hub.example.test/"),
                    Credential: "csrc_existing",
                    PairingToken: null,
                    hello.ProfileId);
                using var outerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await AssertThrowsAsync<HubDisconnectedException>(() =>
                    client.RunSingleConnectionAsync(
                        configuration,
                        hello,
                        outerTimeout.Token));
            }
            finally
            {
                provider.Dispose();
            }
        }
    }

    private static async Task HostedStopFinalizesBeforeCallerCancellation()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var client = new ChatstronomyHubClient(
                provider,
                new HangingHubSocketFactory(hangDuringConnect: true));
            var configuration = new HubConnectionConfiguration(
                new Uri("https://hub.example.test/"),
                Credential: "csrc_existing",
                PairingToken: null,
                hello.ProfileId);
            await client.StartAsync(configuration, hello, CancellationToken.None);
            using var callerCancellation = new CancellationTokenSource();
            callerCancellation.Cancel();

            await AssertThrowsAsync<OperationCanceledException>(() =>
                client.StopAsync(callerCancellation.Token));

            AssertFalse(client.IsRunning);
            AssertFalse(client.IsConnected);
            AssertEqual("Hosted connection is stopped.", client.StatusMessage);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedMissingHeartbeatAcknowledgementReconnects()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var sockets = new ControlledHubSocketFactory(index => new ControlledHubSocket(
                AgentHelloJson(hello),
                acknowledgeHeartbeats: index > 1));
            var client = new ChatstronomyHubClient(
                provider,
                sockets,
                timings: FastHubTimings(),
                jitterSource: () => 0.5);
            var configuration = HostedConfiguration(hello);

            await client.StartAsync(configuration, hello, CancellationToken.None);
            await WaitUntilAsync(() => sockets.CreateCount >= 2, TimeSpan.FromSeconds(2));

            var first = sockets.Sockets.First();
            AssertTrue(first.HeartbeatCount >= 1);
            AssertTrue(first.AbortCount >= 1);
            await WaitUntilAsync(
                () => sockets.Sockets.ElementAtOrDefault(1)?.HeartbeatCount >= 3,
                TimeSpan.FromSeconds(2));
            AssertTrue(client.IsConnected);
            await Task.Delay(100);
            AssertEqual(2, sockets.CreateCount);

            await client.StopAsync(CancellationToken.None);
            AssertFalse(client.IsConnected);
            AssertFalse(client.IsRunning);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedHeartbeatAcknowledgementsKeepConnectionAlive()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var sockets = new ControlledHubSocketFactory(_ => new ControlledHubSocket(
                AgentHelloJson(hello),
                acknowledgeHeartbeats: true));
            var client = new ChatstronomyHubClient(
                provider,
                sockets,
                timings: FastHubTimings(),
                jitterSource: () => 0.5);

            await client.StartAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None);
            await WaitUntilAsync(
                () => sockets.Sockets.FirstOrDefault()?.HeartbeatCount >= 3,
                TimeSpan.FromSeconds(2));

            AssertEqual(1, sockets.CreateCount);
            AssertTrue(client.IsConnected);
            await client.StopAsync(CancellationToken.None);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedStalledSendReconnects()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var sockets = new ControlledHubSocketFactory(index => new ControlledHubSocket(
                AgentHelloJson(hello),
                acknowledgeHeartbeats: index > 1,
                stallHeartbeatSends: index == 1));
            var client = new ChatstronomyHubClient(
                provider,
                sockets,
                timings: FastHubTimings(),
                jitterSource: () => 0.5);

            await client.StartAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None);
            await WaitUntilAsync(() => sockets.CreateCount >= 2, TimeSpan.FromSeconds(2));

            AssertTrue(sockets.Sockets.First().AbortCount >= 1);
            await WaitUntilAsync(
                () => sockets.Sockets.ElementAtOrDefault(1)?.HeartbeatCount >= 3,
                TimeSpan.FromSeconds(2));
            AssertTrue(client.IsConnected);
            await Task.Delay(100);
            AssertEqual(2, sockets.CreateCount);
            await client.StopAsync(CancellationToken.None);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedStopAbortsCancellationResistantSocket()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var sockets = new ControlledHubSocketFactory(_ => new ControlledHubSocket(
                AgentHelloJson(hello),
                acknowledgeHeartbeats: true));
            var timings = FastHubTimings() with
            {
                HeartbeatAcknowledgementTimeout = TimeSpan.FromSeconds(5),
            };
            var client = new ChatstronomyHubClient(
                provider,
                sockets,
                timings: timings,
                jitterSource: () => 0.5);

            await client.StartAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None);
            await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(1));
            var elapsed = Stopwatch.StartNew();
            await client.StopAsync(CancellationToken.None);
            elapsed.Stop();

            AssertTrue(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            AssertTrue(sockets.Sockets.Single().AbortCount >= 1);
            AssertFalse(client.IsConnected);
            AssertFalse(client.IsRunning);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedSessionsCannotCrossProfiles()
    {
        var permittedInBothProfiles = new DirectAccessOptions(
            AllowRemoteControl: true,
            ShareObservatoryLocation: false,
            AllowedCommands: DirectCommandPermissions.UnparkMount);
        var access = new DirectAccessPolicy(permittedInBothProfiles);
        using var provider = new FakeDirectDataProvider(access);
        var hello = HostedHello() with { Capabilities = provider.Capabilities };
        var firstRead = QueryJson(
            Guid.NewGuid(),
            "camera_info",
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        var sockets = new ControlledHubSocketFactory(_ => new ControlledHubSocket(
            AgentHelloJson(hello),
            acknowledgeHeartbeats: true,
            additionalInbound: new[] { firstRead }));
        var client = new ChatstronomyHubClient(
            provider,
            sockets,
            timings: FastHubTimings(),
            jitterSource: () => 0.5);
        await client.StartAsync(HostedConfiguration(hello), hello, CancellationToken.None);
        await WaitUntilAsync(
            () => client.IsConnected && provider.QueryCount == 1,
            TimeSpan.FromSeconds(2));
        var previousSession = provider.ProfileSessionToken;
        var socket = sockets.Sockets.Single();

        // Changing one command checkbox must not disconnect monitoring.
        provider.RevokeRemoteControl();
        AssertFalse(previousSession.IsCancellationRequested);
        AssertEqual(0, socket.AbortCount);
        AssertTrue(client.IsConnected);

        var elapsed = Stopwatch.StartNew();
        ChatstronomyPlugin.ApplyProfileAccessChange(access, provider, permittedInBothProfiles);
        elapsed.Stop();
        AssertTrue(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        AssertTrue(previousSession.IsCancellationRequested);
        AssertFalse(provider.ProfileSessionToken.IsCancellationRequested);
        AssertTrue(socket.AbortCount >= 1);
        AssertTrue(provider.Capabilities.Commands);

        // Both reads and identically allowed commands are rejected because
        // their old authenticated socket has already been synchronously shut.
        AssertFalse(socket.TryEnqueue(QueryJson(
            Guid.NewGuid(),
            "camera_info",
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds())));
        AssertFalse(socket.TryEnqueue(CommandQueryJson(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(),
            "unpark_mount")));
        AssertEqual(1, provider.QueryCount);
        await WaitUntilAsync(() => !client.IsConnected, TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        AssertEqual(1, sockets.CreateCount);
        await client.StopAsync(CancellationToken.None);
    }

    private static async Task HostedBlockedQueryDoesNotBlockHeartbeats()
    {
        var hello = HostedHello();
        var query = QueryJson(
            Guid.NewGuid(),
            "camera_info",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        var provider = new BlockingDirectDataProvider();
        var sockets = new ControlledHubSocketFactory(_ => new ControlledHubSocket(
            AgentHelloJson(hello),
            acknowledgeHeartbeats: true,
            additionalInbound: new[] { query }));
        var client = new ChatstronomyHubClient(
            provider,
            sockets,
            timings: FastHubTimings(),
            jitterSource: () => 0.5);
        try
        {
            await client.StartAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None);
            await provider.Started.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(
                () => sockets.Sockets.Single().HeartbeatCount >= 3,
                TimeSpan.FromSeconds(2));

            AssertTrue(client.IsConnected);
            AssertEqual(1, sockets.CreateCount);
            await client.StopAsync(CancellationToken.None);
            AssertFalse(client.IsConnected);

            // A replacement connection may keep reading/acking, but it must
            // not overtake the old N.I.N.A. provider call that ignored
            // cancellation.
            await client.StartAsync(
                HostedConfiguration(hello),
                hello,
                CancellationToken.None);
            await WaitUntilAsync(
                () => sockets.Sockets.ElementAtOrDefault(1)?.HeartbeatCount >= 2,
                TimeSpan.FromSeconds(2));
            AssertEqual(1, provider.QueryCount);

            provider.Release();
            await WaitUntilAsync(() => provider.QueryCount == 2, TimeSpan.FromSeconds(1));
            await client.StopAsync(CancellationToken.None);
        }
        finally
        {
            provider.Release();
            provider.Dispose();
        }
    }

    private static async Task HostedConcurrentStartsLeaveOneConnection()
    {
        var hello = HostedHello();
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            var sockets = new ControlledHubSocketFactory(_ => new ControlledHubSocket(
                AgentHelloJson(hello),
                acknowledgeHeartbeats: true));
            var client = new ChatstronomyHubClient(
                provider,
                sockets,
                timings: FastHubTimings(),
                jitterSource: () => 0.5);
            var configuration = HostedConfiguration(hello);

            await Task.WhenAll(
                client.StartAsync(configuration, hello, CancellationToken.None),
                client.StartAsync(configuration, hello, CancellationToken.None));
            await WaitUntilAsync(
                () => client.IsConnected
                    && sockets.Sockets.LastOrDefault()?.HeartbeatCount >= 2,
                TimeSpan.FromSeconds(2));

            AssertEqual(1, sockets.Sockets.Count(socket => socket.AbortCount == 0));
            await client.StopAsync(CancellationToken.None);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task HostedPluginRejectsExpiredCommands()
    {
        var hello = HostedHello();
        var queryId = Guid.NewGuid();
        var socketFactory = new ScriptedHubSocketFactory(
            AgentHelloJson(hello),
            CommandQueryJson(queryId, expiresAt: 1, commandKind: "unpark_mount"));
        var provider = new FakeDirectDataProvider();
        provider.Start();
        var client = new ChatstronomyHubClient(provider, socketFactory);
        var configuration = new HubConnectionConfiguration(
            new Uri("wss://hub.example.test/v1/direct"),
            Credential: "csrc_existing",
            PairingToken: null,
            hello.ProfileId);

        await AssertThrowsAsync<HubDisconnectedException>(() =>
            client.RunSingleConnectionAsync(configuration, hello, CancellationToken.None));

        AssertEqual(0, provider.QueryCount);
        using var first = JsonDocument.Parse(socketFactory.Socket.SentMessages.First());
        AssertEqual("auth", first.RootElement.GetProperty("type").GetString());
        var resultJson = socketFactory.Socket.SentMessages.Single(message =>
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.GetProperty("type").GetString() == "query_result";
        });
        using var result = JsonDocument.Parse(resultJson);
        var payload = result.RootElement.GetProperty("payload");
        AssertFalse(payload.GetProperty("ok").GetBoolean());
        AssertTrue(payload.GetProperty("error").GetString()!.Contains("expired"));
        provider.Dispose();
    }

    private static ClientHello HostedHello() => new(
        DirectProtocol.CurrentVersion,
        DirectProtocol.CurrentPayloadVersion,
        Guid.Parse("363db028-9d79-4fdc-8940-1b1ff52b9e8d"),
        Guid.Parse("7afcde18-b5a8-46fd-ad1f-ed54cf3bbc4e"),
        4242,
        Guid.Parse("460a8c62-28ce-4781-92e5-ab2440982175"),
        "North Rig",
        typeof(ChatstronomyPlugin).Assembly.GetName().Version?.ToString() ?? "unknown",
        "3.2.0.9001",
        new DirectCapabilities(true, true, true, true, true, true, true, true));

    private static HubConnectionConfiguration HostedConfiguration(ClientHello hello) => new(
        new Uri("https://hub.example.test/"),
        Credential: "csrc_existing",
        PairingToken: null,
        hello.ProfileId);

    private static HubClientTimings FastHubTimings() => HubClientTimings.Default with
    {
        ConnectionAttemptTimeout = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        HeartbeatAcknowledgementTimeout = TimeSpan.FromMilliseconds(40),
        SocketOperationTimeout = TimeSpan.FromMilliseconds(40),
        ShutdownTimeout = TimeSpan.FromMilliseconds(100),
        InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
        MaximumReconnectDelay = TimeSpan.FromMilliseconds(40),
        StableConnectionDuration = TimeSpan.FromMilliseconds(100),
        QueryQueueCapacity = 2,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException("Timed out waiting for the test condition.");
            }
            await Task.Delay(5);
        }
    }

    private static string QueryJson(Guid id, string kind, long? expiresAt = null) =>
        JsonSerializer.Serialize(
            new
            {
                type = "query",
                payload = new { id, expires_at = expiresAt, kind },
            },
            DirectProtocol.JsonOptions);

    private static string ThumbnailQueryJson(Guid id, uint index) =>
        JsonSerializer.Serialize(new
        {
            type = "query",
            payload = new { id, kind = "thumbnail", index },
        });

    private static string CommandQueryJson(Guid id, long expiresAt, string commandKind) =>
        JsonSerializer.Serialize(new
        {
            type = "query",
            payload = new
            {
                id,
                expires_at = expiresAt,
                kind = "command",
                command = new { kind = commandKind },
            },
        });

    private static string PairResultJson(ClientHello hello, string credential) =>
        JsonSerializer.Serialize(new
        {
            type = "pair_result",
            payload = new { credential, agent_hello = AgentHelloPayload(hello) },
        });

    private static string AgentHelloJson(ClientHello hello) =>
        JsonSerializer.Serialize(new { type = "agent_hello", payload = AgentHelloPayload(hello) });

    private static string HeartbeatAcknowledgementJson(ulong sequence) =>
        JsonSerializer.Serialize(new
        {
            type = "heartbeat_ack",
            payload = new { seq = sequence },
        });

    private static string LegacyAgentHelloJson(ClientHello hello) =>
        JsonSerializer.Serialize(new
        {
            type = "agent_hello",
            payload = new
            {
                protocol_version = 1,
                connection_id = Guid.Parse("6dd05107-5b90-4d46-99c8-eb9a17489e81"),
                rig_id = new { node_id = hello.NodeId, profile_id = hello.ProfileId },
            },
        });

    private static object AgentHelloPayload(ClientHello hello) => new
    {
        protocol_version = 1,
        payload_version = hello.PayloadVersion,
        connection_id = Guid.Parse("6dd05107-5b90-4d46-99c8-eb9a17489e81"),
        rig_id = new { node_id = hello.NodeId, profile_id = hello.ProfileId },
    };

    private static async Task PluginRuntimeStartsAndStops(string runtimePath)
    {
        var provider = new FakeDirectDataProvider();
        var controller = new ChatstronomyRuntimeController(provider);
        var profileId = Guid.NewGuid();
        try
        {
            await controller.StartAsync(
                BuildDirectRuntimeConfiguration(runtimePath),
                new LocalRuntimeIdentity(
                    Guid.NewGuid(),
                    profileId,
                    "Controller Integration Test"),
                CancellationToken.None);
            if (!controller.IsRunning)
            {
                throw new InvalidOperationException(
                    "The local runtime exited immediately after acknowledging startup.");
            }
            if (!controller.ProcessId.HasValue)
            {
                throw new InvalidOperationException(
                    "The running local runtime did not expose its Windows process ID.");
            }

            await controller.StopAsync(CancellationToken.None);
            if (controller.IsRunning)
            {
                throw new InvalidOperationException(
                    "The local runtime was still running after graceful shutdown completed.");
            }

            var logPath = RuntimeLogPath(profileId);
            var log = await ReadAllTextWhenUnlockedAsync(logPath);
            AssertRuntimeLogDoesNotContain(log, "api/webhooks", "a Discord webhook URL");
            AssertRuntimeLogDoesNotContain(log, "token", "a possible delivery credential");
        }
        finally
        {
            if (controller.IsRunning)
            {
                await controller.StopAsync(CancellationToken.None);
            }
            provider.Dispose();
        }
    }

    private static async Task PluginRuntimeRedactsFailedWebhookDeliveries(string runtimePath)
    {
        const string privateWebhookProbe = "chatstronomy-private-webhook-probe";
        const string deliveryFailure = "Failed to send message to Discord";
        var provider = new FakeDirectDataProvider();
        var controller = new ChatstronomyRuntimeController(provider);
        var profileId = Guid.NewGuid();
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proxyRequest = RejectWebhookProxyRequestAsync(listener, timeout.Token);
        var proxy = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var previousProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var previousNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");

        try
        {
            try
            {
                // The child receives a copy of this environment at process
                // creation. Restore our own settings as soon as its secure
                // bootstrap completes so no other integration uses the proxy.
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy);
                Environment.SetEnvironmentVariable("NO_PROXY", string.Empty);
                await controller.StartAsync(
                    BuildDirectRuntimeConfiguration(runtimePath, privateWebhookProbe),
                    new LocalRuntimeIdentity(
                        Guid.NewGuid(),
                        profileId,
                        "Webhook Privacy Integration Test"),
                    timeout.Token);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", previousProxy);
                Environment.SetEnvironmentVariable("NO_PROXY", previousNoProxy);
            }

            // Waiting for the loopback CONNECT and the resulting runtime log
            // proves a request really failed before shutdown: the privacy
            // assertion cannot pass merely because startup was interrupted.
            await proxyRequest.WaitAsync(timeout.Token);
            var logPath = RuntimeLogPath(profileId);
            await WaitForRuntimeLogEntryAsync(logPath, deliveryFailure, timeout.Token);
            await controller.StopAsync(CancellationToken.None);
            var log = await ReadAllTextWhenUnlockedAsync(logPath);
            AssertRuntimeLogDoesNotContain(log, "api/webhooks", "a Discord webhook URL");
            AssertRuntimeLogDoesNotContain(log, privateWebhookProbe, "a Discord webhook credential");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            if (controller.IsRunning)
            {
                await controller.StopAsync(CancellationToken.None);
            }
            provider.Dispose();
        }
    }

    private static async Task RejectWebhookProxyRequestAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var requestBytes = new byte[1024];
        var read = await stream.ReadAsync(requestBytes, cancellationToken);
        var request = Encoding.ASCII.GetString(requestBytes, 0, read);
        if (!request.StartsWith("CONNECT discord.com:443 ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The local runtime did not route its webhook request through the isolated proxy.");
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string RuntimeLogPath(Guid profileId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chatstronomy",
        "logs",
        $"nina-runtime-{profileId:N}.log");

    private static async Task WaitForRuntimeLogEntryAsync(
        string path,
        string marker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var log = await reader.ReadToEndAsync(cancellationToken);
                if (log.Contains(marker, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // Process startup may not have opened its log yet.
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static void AssertRuntimeLogDoesNotContain(
        string log,
        string privateValue,
        string description)
    {
        if (log.Contains(privateValue, StringComparison.OrdinalIgnoreCase))
        {
            // Never print the offending log line or credential in CI output.
            throw new InvalidOperationException($"The local runtime log exposed {description}.");
        }
    }

    private static async Task<string> ReadAllTextWhenUnlockedAsync(string path)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException) when (
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(5))
            {
                // Windows can retain the exiting process's file handle for a
                // moment after WaitForExitAsync completes. The release test is
                // interested in the log contents, not that transient teardown
                // timing, so give the handle a bounded chance to close.
                await Task.Delay(50);
            }
        }
    }

    private static async Task PluginRuntimeUsesDirectPipe(string runtimePath)
    {
        var provider = new FakeDirectDataProvider();
        var controller = new ChatstronomyRuntimeController(provider);
        try
        {
            provider.Start();
            await controller.StartAsync(
                BuildDirectRuntimeConfiguration(runtimePath),
                new LocalRuntimeIdentity(Guid.NewGuid(), Guid.NewGuid(), "Direct Pipe Test"),
                CancellationToken.None);
            AssertTrue(controller.IsRunning);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (provider.QueryCount < 3)
            {
                await Task.Delay(50, timeout.Token);
            }

            await controller.StopAsync(CancellationToken.None);
            AssertFalse(controller.IsRunning);
        }
        finally
        {
            if (controller.IsRunning)
            {
                await controller.StopAsync(CancellationToken.None);
            }
            provider.Dispose();
        }
    }

    private static async Task DirectPipeRendersCharts(string runtimePath)
    {
        var provider = new FakeDirectDataProvider();
        var pipeName = NinaDirectPipeServer.CreatePipeName();
        using var pipe = new NinaDirectPipeServer(provider, pipeName);
        var artifactDirectory = Environment.GetEnvironmentVariable(
            "CHATSTRONOMY_CHART_ARTIFACT_DIRECTORY");
        var outputDirectory = string.IsNullOrWhiteSpace(artifactDirectory)
            ? Path.GetTempPath()
            : Path.GetFullPath(artifactDirectory);
        Directory.CreateDirectory(outputDirectory);
        var suffix = string.IsNullOrWhiteSpace(artifactDirectory)
            ? $"-{Guid.NewGuid():N}"
            : string.Empty;
        var guiderOutputPath = Path.Combine(
            outputDirectory,
            $"chatstronomy-direct-guider{suffix}.png");
        var autofocusOutputPath = Path.Combine(
            outputDirectory,
            $"chatstronomy-direct-autofocus{suffix}.png");
        try
        {
            provider.Start();
            pipe.Start();
            var startInfo = new System.Diagnostics.ProcessStartInfo(runtimePath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("direct-render-probe");
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--guider-output");
            startInfo.ArgumentList.Add(guiderOutputPath);
            startInfo.ArgumentList.Add("--autofocus-output");
            startInfo.ArgumentList.Add(autofocusOutputPath);
            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Direct render probe.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Direct render probe exited with {process.ExitCode}: {standardError}");
            }
            AssertTrue(string.IsNullOrWhiteSpace(standardError));
            AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.GuiderGraph));
            AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.LastAutofocus));

            foreach (var outputPath in new[] { guiderOutputPath, autofocusOutputPath })
            {
                var png = await File.ReadAllBytesAsync(outputPath, timeout.Token);
                AssertTrue(png.Length > 1_000);
                AssertTrue(png.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a }));
            }
        }
        finally
        {
            provider.Dispose();
            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                foreach (var outputPath in new[] { guiderOutputPath, autofocusOutputPath })
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
            }
        }
    }

    private static async Task HostedPluginUsesRustHub(string runtimePath)
    {
        var artifactDirectory = Environment.GetEnvironmentVariable(
            "CHATSTRONOMY_CHART_ARTIFACT_DIRECTORY");
        var outputDirectory = string.IsNullOrWhiteSpace(artifactDirectory)
            ? Path.GetTempPath()
            : Path.GetFullPath(artifactDirectory);
        Directory.CreateDirectory(outputDirectory);
        var suffix = string.IsNullOrWhiteSpace(artifactDirectory)
            ? $"-{Guid.NewGuid():N}"
            : string.Empty;
        var guiderOutputPath = Path.Combine(
            outputDirectory,
            $"chatstronomy-hosted-guider{suffix}.png");
        var autofocusOutputPath = Path.Combine(
            outputDirectory,
            $"chatstronomy-hosted-autofocus{suffix}.png");
        var startInfo = new System.Diagnostics.ProcessStartInfo(runtimePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("direct-hub-probe");
        startInfo.ArgumentList.Add("--guider-output");
        startInfo.ArgumentList.Add(guiderOutputPath);
        startInfo.ArgumentList.Add("--autofocus-output");
        startInfo.ArgumentList.Add(autofocusOutputPath);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Direct hub probe.");
        var provider = new FakeDirectDataProvider();
        provider.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            JsonElement? ready = null;
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("probe", out var probe)
                        && probe.GetString() == "direct_hub_ready")
                    {
                        ready = document.RootElement.Clone();
                        break;
                    }
                }
                catch (JsonException)
                {
                    // The normal CLI banner precedes the probe's JSON line.
                }
            }
            AssertTrue(ready.HasValue);

            var hello = HostedHello();
            var client = new ChatstronomyHubClient(provider);
            HubCredentialIssuedEventArgs? issued = null;
            client.CredentialIssued += (_, args) => issued = args;
            var configuration = new HubConnectionConfiguration(
                new Uri(ready!.Value.GetProperty("hub_url").GetString()!),
                Credential: null,
                PairingToken: ready.Value.GetProperty("pairing_token").GetString(),
                hello.ProfileId,
                AllowInsecureLoopback: true);
            var connectionTask = client.RunSingleConnectionAsync(
                configuration,
                hello,
                timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            try
            {
                await connectionTask;
            }
            catch (HubDisconnectedException)
            {
                // The diagnostic hub exits after it receives both chart payloads.
            }
            catch (System.Net.WebSockets.WebSocketException) when (process.HasExited)
            {
                // Axum is aborted after the two successful requests, so the
                // diagnostic socket need not complete a graceful close.
            }
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Hosted hub probe exited with {process.ExitCode}: {standardError}");
            }
            AssertTrue(string.IsNullOrWhiteSpace(standardError));
            AssertTrue(issued?.Credential.StartsWith("csrc_", StringComparison.Ordinal) == true);
            AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.GuiderGraph));
            AssertTrue(provider.QueriedKinds.Contains(DirectQueryKind.LastAutofocus));

            foreach (var outputPath in new[] { guiderOutputPath, autofocusOutputPath })
            {
                var png = await File.ReadAllBytesAsync(outputPath, timeout.Token);
                AssertTrue(png.Length > 1_000);
                AssertTrue(png.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a }));
            }
        }
        finally
        {
            provider.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                foreach (var outputPath in new[] { guiderOutputPath, autofocusOutputPath })
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
            }
        }
    }

    private static ChatstronomyConfiguration BuildDirectRuntimeConfiguration(
        string runtimePath,
        string webhookToken = "token") =>
        new(
            new DiscordWebhookDeliveryConfiguration(
                new Uri($"https://discord.com/api/webhooks/123/{webhookToken}")),
            Matrix: null,
            new LocalRuntimeConfiguration(runtimePath));

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL: {name}: {exception.Message}");
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL: {name}: {exception.Message}");
        }
    }

    private static T AssertType<T>(object value)
    {
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(T).Name}, but received {value.GetType().Name}.");
    }

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}', but received '{actual}'.");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} to be thrown.");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} to be thrown.");
    }

    private class BlockingSafetyMonitorProxy : DispatchProxy
    {
        internal TaskCompletionSource<object?> ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim ReleaseRead { get; } = new();

        internal bool ConnectedState { get; set; }

        internal bool SafeState { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != "GetInfo")
            {
                throw new NotSupportedException(
                    $"Unexpected safety-monitor operation '{method?.Name}'.");
            }

            ReadStarted.TrySetResult(null);
            ReleaseRead.Wait();
            var info = Activator.CreateInstance(method.ReturnType)
                ?? throw new InvalidOperationException(
                    "Safety-monitor information could not be created.");
            method.ReturnType.GetProperty("Connected")?.SetValue(info, ConnectedState);
            method.ReturnType.GetProperty("IsSafe")?.SetValue(info, SafeState);
            return info;
        }
    }

    private class ActiveProfileProxy : DispatchProxy
    {
        internal Guid Id { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            return method?.Name switch
            {
                "get_Id" => Id,
                "set_Id" => Id = (Guid)arguments![0]!,
                "Dispose" => null,
                _ => throw new NotSupportedException(
                    $"Unexpected active-profile operation '{method?.Name}'."),
            };
        }
    }

    private class ActiveProfileServiceProxy : DispatchProxy
    {
        internal global::NINA.Profile.Interfaces.IProfile ActiveProfile { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            return method?.Name switch
            {
                "get_ActiveProfile" => ActiveProfile,
                _ => throw new NotSupportedException(
                    $"Unexpected profile-service operation '{method?.Name}'."),
            };
        }
    }

    private class GuardedTelescopeProxy : DispatchProxy
    {
        private int actuationCount;

        internal Action? BeforeGetInfo { get; set; }

        internal int ActuationCount => Volatile.Read(ref actuationCount);

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method is null)
            {
                throw new InvalidOperationException("A telescope method was not supplied.");
            }
            if (method.Name == "GetInfo")
            {
                BeforeGetInfo?.Invoke();
                var info = Activator.CreateInstance(method.ReturnType)
                    ?? throw new InvalidOperationException("Mount information could not be created.");
                method.ReturnType.GetProperty("Connected")?.SetValue(info, true);
                method.ReturnType.GetProperty("AtPark")?.SetValue(info, true);
                return info;
            }
            if (method.Name == "UnparkTelescope")
            {
                Interlocked.Increment(ref actuationCount);
                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected telescope operation '{method.Name}'.");
        }
    }

    private sealed class ControlledHubSocketFactory(
        Func<int, ControlledHubSocket> createSocket) : IHubSocketFactory
    {
        private readonly ConcurrentQueue<ControlledHubSocket> sockets = new();
        private int createCount;

        internal int CreateCount => Volatile.Read(ref createCount);

        internal IReadOnlyList<ControlledHubSocket> Sockets => sockets.ToArray();

        public IHubSocket Create()
        {
            var socket = createSocket(Interlocked.Increment(ref createCount));
            sockets.Enqueue(socket);
            return socket;
        }
    }

    private sealed class ControlledHubSocket : IHubSocket
    {
        private readonly Channel<string> inbound = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly TaskCompletionSource aborted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool acknowledgeHeartbeats;
        private readonly bool stallHeartbeatSends;
        private int heartbeatCount;
        private int abortCount;

        internal ControlledHubSocket(
            string handshake,
            bool acknowledgeHeartbeats,
            bool stallHeartbeatSends = false,
            IEnumerable<string>? additionalInbound = null)
        {
            this.acknowledgeHeartbeats = acknowledgeHeartbeats;
            this.stallHeartbeatSends = stallHeartbeatSends;
            inbound.Writer.TryWrite(handshake);
            if (additionalInbound is not null)
            {
                foreach (var message in additionalInbound)
                {
                    inbound.Writer.TryWrite(message);
                }
            }
        }

        internal ConcurrentQueue<string> SentMessages { get; } = new();

        internal int HeartbeatCount => Volatile.Read(ref heartbeatCount);

        internal int AbortCount => Volatile.Read(ref abortCount);

        internal bool TryEnqueue(string message) => inbound.Writer.TryWrite(message);

        public void Abort()
        {
            Interlocked.Increment(ref abortCount);
            aborted.TrySetResult();
            inbound.Writer.TryComplete();
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentMessages.Enqueue(message);
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.GetProperty("type").GetString() != "heartbeat")
            {
                return;
            }

            Interlocked.Increment(ref heartbeatCount);
            if (stallHeartbeatSends)
            {
                await aborted.Task.ConfigureAwait(false);
                throw new IOException("Socket send was aborted.");
            }
            if (acknowledgeHeartbeats)
            {
                var sequence = document.RootElement
                    .GetProperty("payload")
                    .GetProperty("seq")
                    .GetUInt64();
                inbound.Writer.TryWrite(HeartbeatAcknowledgementJson(sequence));
            }
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            // Deliberately ignore cancellation: the lifecycle code must use
            // Abort to make cleanup bounded when a socket implementation or
            // network stack does not cooperate with its token.
            try
            {
                return await inbound.Reader.ReadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Abort();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHubSocketFactory(params string[] inbound)
        : IHubSocketFactory
    {
        internal ScriptedHubSocket Socket { get; } = new(inbound);

        public IHubSocket Create() => Socket;
    }

    private sealed class ScriptedHubSocket(IEnumerable<string> inbound) : IHubSocket
    {
        private readonly ConcurrentQueue<string> inboundMessages = new(inbound);

        internal ConcurrentQueue<string> SentMessages { get; } = new();

        internal Uri? Endpoint { get; private set; }

        public void Abort()
        {
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoint = endpoint;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentMessages.Enqueue(message);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(inboundMessages.TryDequeue(out var message) ? message : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HangingHubSocketFactory(bool hangDuringConnect)
        : IHubSocketFactory
    {
        public IHubSocket Create() => new HangingHubSocket(hangDuringConnect);
    }

    private sealed class HangingHubSocket(bool hangDuringConnect) : IHubSocket
    {
        public void Abort()
        {
        }

        public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            if (hangDuringConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingDirectDataProvider : INinaDirectDataProvider
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource directSession = new();
        private Guid directSessionId = Guid.NewGuid();
        private CancellationTokenSource profileSession = new();
        private int queryCount;

        public DirectCapabilities Capabilities { get; } = new(
            EventHistory: true,
            ImageHistory: true,
            Thumbnails: true,
            Sequence: true,
            EquipmentSnapshots: true,
            AutofocusDetails: true,
            GuiderGraph: true,
            Commands: true);

        public CancellationToken ProfileSessionToken =>
            Volatile.Read(ref profileSession).Token;

        public CancellationToken DirectSessionToken =>
            Volatile.Read(ref directSession).Token;

        public Guid DirectSessionId => directSessionId;

        internal Task Started => started.Task;

        internal int QueryCount => Volatile.Read(ref queryCount);

        internal void Release() => release.TrySetResult(
            DirectApiEnvelope<object>.Ok(new { Connected = true }));

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Reset()
        {
        }

        public void ApplyLogDeliveryOptions()
        {
        }

        public void RevokeRemoteControl()
        {
        }

        public void RotateDirectSession()
        {
            directSessionId = Guid.NewGuid();
            var previous = Interlocked.Exchange(
                ref directSession,
                new CancellationTokenSource());
            previous.Cancel();
        }

        public void RevokeProfileAccess()
        {
            RotateDirectSession();
            var previous = Interlocked.Exchange(
                ref profileSession,
                new CancellationTokenSource());
            previous.Cancel();
            RevokeRemoteControl();
        }

        public async Task<object?> ExecuteAsync(
            DirectQuery query,
            CancellationToken cancellationToken,
            CancellationToken? directSessionToken = null)
        {
            Interlocked.Increment(ref queryCount);
            started.TrySetResult();
            // Model a synchronous UI/provider call that cannot honor the
            // connection token until N.I.N.A. gives control back.
            return await release.Task.ConfigureAwait(false);
        }

        public void ConfirmDirectQueryResponse(
            DirectQuery query,
            CancellationToken directSessionToken)
        {
        }

        public void Dispose() => Release();
    }

    private sealed class FakeDirectDataProvider : INinaDirectDataProvider
    {
        private readonly DirectAccessPolicy? accessPolicy;
        private readonly Exception? executeFailure;
        private CancellationTokenSource directSession = new();
        private Guid directSessionId = Guid.NewGuid();
        private CancellationTokenSource profileSession = new();
        private int queryCount;
        private int revocationCount;
        private int directSessionRotationCount;
        private int eventCaptureSuspendCount;
        private int eventCaptureResumeCount;
        private int eventCaptureSuspended;
        private readonly ConcurrentBag<DirectQueryKind> queriedKinds = new();

        internal FakeDirectDataProvider(
            DirectAccessPolicy? accessPolicy = null,
            Exception? executeFailure = null)
        {
            this.accessPolicy = accessPolicy;
            this.executeFailure = executeFailure;
        }

        public DirectCapabilities Capabilities => new(
            EventHistory: true,
            ImageHistory: true,
            Thumbnails: true,
            Sequence: true,
            EquipmentSnapshots: true,
            AutofocusDetails: true,
            GuiderGraph: true,
            Commands: accessPolicy?.Current.CommandsEnabled ?? true);

        public CancellationToken ProfileSessionToken =>
            Volatile.Read(ref profileSession).Token;

        public CancellationToken DirectSessionToken =>
            Volatile.Read(ref directSession).Token;

        public Guid DirectSessionId => directSessionId;

        public int QueryCount => Volatile.Read(ref queryCount);
        public int RevocationCount => Volatile.Read(ref revocationCount);
        public int DirectSessionRotationCount =>
            Volatile.Read(ref directSessionRotationCount);
        public int EventCaptureSuspendCount => Volatile.Read(ref eventCaptureSuspendCount);
        public int EventCaptureResumeCount => Volatile.Read(ref eventCaptureResumeCount);
        public bool EventCaptureSuspended => Volatile.Read(ref eventCaptureSuspended) != 0;
        public IReadOnlyCollection<DirectQueryKind> QueriedKinds => queriedKinds;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Reset()
        {
        }

        public void ApplyLogDeliveryOptions()
        {
        }

        public void RevokeRemoteControl()
        {
            Interlocked.Increment(ref revocationCount);
        }

        public void RotateDirectSession()
        {
            Interlocked.Increment(ref directSessionRotationCount);
            directSessionId = Guid.NewGuid();
            var previous = Interlocked.Exchange(
                ref directSession,
                new CancellationTokenSource());
            previous.Cancel();
        }

        public void SuspendEventCapture()
        {
            Interlocked.Increment(ref eventCaptureSuspendCount);
            Interlocked.Exchange(ref eventCaptureSuspended, 1);
        }

        public void ResumeEventCapture()
        {
            Interlocked.Increment(ref eventCaptureResumeCount);
            Interlocked.Exchange(ref eventCaptureSuspended, 0);
        }

        public void RevokeProfileAccess()
        {
            RotateDirectSession();
            var previous = Interlocked.Exchange(
                ref profileSession,
                new CancellationTokenSource());
            previous.Cancel();
            RevokeRemoteControl();
        }

        public Task<object?> ExecuteAsync(
            DirectQuery query,
            CancellationToken cancellationToken,
            CancellationToken? directSessionToken = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref queryCount);
            queriedKinds.Add(query.Kind);
            if (executeFailure is not null)
            {
                throw executeFailure;
            }

            object response = query.Kind switch
            {
                DirectQueryKind.EventHistory =>
                    DirectApiEnvelope<IReadOnlyList<object>>.Ok(Array.Empty<object>()),
                DirectQueryKind.ImageHistory =>
                    DirectApiEnvelope<IReadOnlyList<DirectImageMetadata>>.Ok(
                        Array.Empty<DirectImageMetadata>()),
                DirectQueryKind.Sequence =>
                    DirectApiEnvelope<IReadOnlyList<object>>.Ok(Array.Empty<object>()),
                DirectQueryKind.CameraInfo =>
                    DirectApiEnvelope<DirectCameraInfo>.Ok(new DirectCameraInfo(
                        Connected: true,
                        CanSetTemperature: true,
                        CoolerOn: true,
                        CoolerPower: 60,
                        Temperature: 5,
                        TemperatureSetPoint: -10,
                        AtTargetTemp: false,
                        Name: "Test camera",
                        DisplayName: "Test camera")),
                DirectQueryKind.Thumbnail => new DirectThumbnail(
                    new byte[] { 0xff, 0xd8, 0xff, 0xd9 },
                    "image/jpeg",
                    200),
                DirectQueryKind.GuiderGraph => GuiderGraph(),
                DirectQueryKind.LastAutofocus => LastAutofocus(),
                _ => throw new NotSupportedException(),
            };
            return Task.FromResult<object?>(response);
        }

        public void ConfirmDirectQueryResponse(
            DirectQuery query,
            CancellationToken directSessionToken)
        {
        }

        private static object GuiderGraph()
        {
            var steps = new[]
            {
                new DirectGuideStep(1, 0.85, 1.15, -0.2, -0.4, -120, 0.1, 0.2, 80, "NO"),
                new DirectGuideStep(2, 1.85, 2.15, 0.3, 0.6, 140, -0.2, -0.4, -90, "NO"),
                new DirectGuideStep(3, 2.85, 3.15, 0, 0, 0, 0, 0, 0, "0.01"),
            };
            var rms = DirectGuideRms.FromSteps(steps[..2], pixelScale: 2);
            return DirectApiEnvelope<DirectGuiderGraph>.Ok(new DirectGuiderGraph(
                rms,
                Interval: 1,
                MaxY: 4,
                MinY: -4,
                MaxDurationY: 140,
                MinDurationY: -140,
                GuideSteps: steps,
                HistorySize: 500,
                PixelScale: 2,
                Scale: 1));
        }

        private static object LastAutofocus()
        {
            var contractsDirectory = Environment.GetEnvironmentVariable(
                "CHATSTRONOMY_CONTRACTS_DIR");
            var coordinatedBackend = !string.IsNullOrWhiteSpace(contractsDirectory)
                && File.Exists(Path.Combine(
                    contractsDirectory,
                    "direct",
                    "v1",
                    "fixtures",
                    "query-result-resource-not-ready.json"));
            var fixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                coordinatedBackend
                    ? "hocus_focus_v4_report.json"
                    : "example_last_af.json");
            using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
            return DirectApiEnvelope<JsonElement>.Ok(
                DirectAutofocusReportProjection.Project(document.RootElement));
        }

        public void Dispose() => Stop();
    }
}
