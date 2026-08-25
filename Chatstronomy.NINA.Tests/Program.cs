using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Remote;
using Chatstronomy.NINA.Runtime;
using Chatstronomy.NINA.Settings;
using Newtonsoft.Json.Linq;
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
            "Disabled events and images never cross the local Direct pipe",
            LocalDirectPipesDoNotTransmitDisabledEvents);
        await RunAsync(
            "Hosted Direct connections cannot bypass local command consent",
            HostedDirectConnectionsEnforceConsent);
        await RunAsync(
            "Synchronous hosted Direct failures never expose observatory filesystem paths",
            HostedDirectConnectionsRedactSynchronousFailures);
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
            "Disabled command-failure notifications never leave N.I.N.A.",
            CommandFailuresHonorOtherEventConsent);
        Run("Direct commands use semantic wire names", DirectCommandsUseSemanticWireNames);
        Run("Direct camera queries use the shared equipment contract", DirectCameraQueryUsesSharedContract);
        Run("Direct event delivery categories are independently configurable", DirectEventDeliveryIsConfigurable);
        Run("N.I.N.A. log lines preserve structured source and message data", NinaLogLinesAreStructured);
        Run("N.I.N.A. popup colors map to chat severity", NinaPopupColorsMapToSeverity);
        Run("Direct sequence marks chat-visible operations", DirectSequenceMarksChatVisibleOperations);
        Run("Direct guider payload matches the Rust chart contract", DirectGuiderPayloadMatchesRustChart);
        Run("Direct query results match Rust envelope", DirectQueryResultsMatchRustEnvelope);
        Run("Direct histories stay insertion ordered and bounded", DirectHistoriesAreBounded);
        Run("Direct image thumbnails are sized for chat", DirectImageThumbnailsAreSizedForChat);
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
            value.Contains("every individual command", StringComparison.Ordinal)
            && value.Contains("default to off", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("master switch alone grants no command access", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("never sent to the Hub or local bot", StringComparison.Ordinal)
            && value.Contains("blocks image history and thumbnails", StringComparison.Ordinal)));
        AssertTrue(descriptions.Any(value =>
            value.Contains("Events, images, and popup notifications start enabled", StringComparison.Ordinal)
            && value.Contains("Disable unwanted categories before connecting", StringComparison.Ordinal)));
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
            ["{Binding SendSequenceEvents}"] = "Sequence, waits, and cooling",
            ["{Binding SendTargetSchedulerEvents}"] = "Targets and Target Scheduler",
            ["{Binding SendFilterFocuserRotatorEvents}"] = "Filter, focuser, and rotator",
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
        var addEvent = typeof(NinaDirectDataProvider).GetMethod(
            "AddEventCore",
            BindingFlags.Instance | BindingFlags.NonPublic)
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
            ("images", new[] { "IMAGE-SAVE", "API-CAPTURE-FINISHED" },
                options => options with { Images = false }),
            ("autofocus", new[] { "AUTOFOCUS-FINISHED", "ERROR-AF", "FOCUSER-USER-FOCUSED" },
                options => options with { Autofocus = false }),
            ("guiding", new[] { "GUIDER-START", "GUIDER-DITHER" },
                options => options with { Guiding = false }),
            ("mount", new[] { "MOUNT-PARKED", "MOUNT-CENTER", "ERROR-PLATESOLVE" },
                options => options with { Mount = false }),
            ("sequence", new[] { "SEQUENCE-STARTING", "SEQUENCE-FINISHED" },
                options => options with { Sequence = false }),
            ("targets", new[] { "TS-TARGETSTART", "TS-NEWTARGETSTART", "TS-WAITSTART" },
                options => options with { TargetScheduler = false }),
            ("filter, focuser, and rotator", new[]
                { "FILTERWHEEL-CHANGED", "FOCUSER-MOVED", "ROTATOR-MOVED" },
                options => options with { FilterFocuserRotator = false }),
            ("connections", new[]
                { "MOUNT-CONNECTED", "GUIDER-DISCONNECTED", "CAMERA-DOWNLOAD-TIMEOUT" },
                options => options with { EquipmentConnections = false }),
            ("other", new[] { "UNKNOWN-NINA-EVENT", "CHATSTRONOMY-COMMAND-FAILED" },
                options => options with { OtherEvents = false }),
            ("popup notifications", new[] { "NINA-NOTIFICATION" },
                options => options with { NinaNotifications = false }),
        };

        foreach (var (category, eventNames, disable) in cases)
        {
            var initial = DirectEventDeliveryOptions.Default;
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
        var addEvent = typeof(NinaDirectDataProvider).GetMethod(
            "AddEvent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("N.I.N.A. event recorder was not found.");
        addEvent.Invoke(provider, new object[]
        {
            eventName,
            new (string Name, object? Value)[] { ("Marker", marker) },
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
        observeCommand.Invoke(provider, new object[] { completion.Task, "Cool camera" });
        completion.SetCanceled();

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

    private static async Task CommandFailuresHonorOtherEventConsent()
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
        AssertEqual(0, (await SnapshotEvents(provider)).Length);

        delivery.Update(delivery.Current with { OtherEvents = true });
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
        addFailure.Invoke(provider, new object[] { "Warm camera", "Explicitly shared failure" });
        var allowed = await SnapshotEvents(provider);
        AssertEqual(1, allowed.Length);
        AssertEqual("Warm camera", allowed[0].GetProperty("Command").GetString());

        delivery.Update(delivery.Current with { OtherEvents = false });
        AssertEqual(0, (await SnapshotEvents(provider)).Length);
    }

    private static NinaDirectDataProvider CreateSecurityTestProvider(
        DirectAccessPolicy access,
        DirectEventDeliveryOptions? delivery = null,
        DirectEventDeliveryPolicy? deliveryPolicy = null,
        ITelescopeMediator? telescope = null) => new(
            profileService: null!,
            telescope: telescope!,
            camera: null!,
            filterWheel: null!,
            guider: null!,
            rotator: null!,
            focuser: null!,
            sequence: null!,
            imageSave: null!,
            applicationStatus: null!,
            autoFocusFactory: null!,
            imageHistory: null!,
            windowFactory: null!,
            messageBroker: null!,
            eventDelivery: deliveryPolicy ?? new DirectEventDeliveryPolicy(
                delivery ?? DirectEventDeliveryOptions.Default),
            accessPolicy: access);

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
            EquipmentConnections = false,
            TargetScheduler = false,
            NinaLogWarnings = true,
        };

        AssertFalse(options.ShouldSendEvent("GUIDER-DITHER"));
        AssertFalse(options.ShouldSendEvent("GUIDER-CONNECTED"));
        AssertFalse(options.ShouldSendEvent("TS-TARGETSTART"));
        AssertTrue(options.ShouldSendEvent("MOUNT-CENTER"));
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

    private static void DirectSequenceMarksChatVisibleOperations()
    {
        var method = typeof(NinaDirectSequenceSnapshot).GetMethod(
            "AddItemDetails",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sequence detail projector was not found.");

        var wait = new global::NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 90 };
        var waitDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object[] { wait, waitDetails });
        AssertEqual("time_wait", waitDetails["OperationKind"] as string);
        AssertEqual(90.0, Convert.ToDouble(waitDetails["Delay"]));
        AssertEqual(TimeSpan.FromSeconds(90), (TimeSpan)waitDetails["CalculatedWaitDuration"]!);

        var cooling = new global::NINA.Sequencer.SequenceItem.Camera.CoolCamera(null!)
        {
            Temperature = -10,
            Duration = 15,
        };
        var coolingDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object[] { cooling, coolingDetails });
        AssertEqual("camera_cooling", coolingDetails["OperationKind"] as string);
        AssertEqual(-10.0, Convert.ToDouble(coolingDetails["Temperature"]));
        AssertEqual(15.0, Convert.ToDouble(coolingDetails["MinCoolingTime"]));

        var slew = new global::NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec(null!, null!);
        var slewDetails = new Dictionary<string, object?>();
        method.Invoke(null, new object[] { slew, slewDetails });
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
        method.Invoke(null, new object[] { center, centerDetails });
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

        public void RevokeProfileAccess()
        {
            var previous = Interlocked.Exchange(
                ref profileSession,
                new CancellationTokenSource());
            previous.Cancel();
            RevokeRemoteControl();
        }

        public async Task<object?> ExecuteAsync(
            DirectQuery query,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref queryCount);
            started.TrySetResult();
            // Model a synchronous UI/provider call that cannot honor the
            // connection token until N.I.N.A. gives control back.
            return await release.Task.ConfigureAwait(false);
        }

        public void Dispose() => Release();
    }

    private sealed class FakeDirectDataProvider : INinaDirectDataProvider
    {
        private readonly DirectAccessPolicy? accessPolicy;
        private readonly Exception? executeFailure;
        private CancellationTokenSource profileSession = new();
        private int queryCount;
        private int revocationCount;
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

        public int QueryCount => Volatile.Read(ref queryCount);
        public int RevocationCount => Volatile.Read(ref revocationCount);
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

        public void RevokeProfileAccess()
        {
            var previous = Interlocked.Exchange(
                ref profileSession,
                new CancellationTokenSource());
            previous.Cancel();
            RevokeRemoteControl();
        }

        public Task<object?> ExecuteAsync(
            DirectQuery query,
            CancellationToken cancellationToken)
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
            var fixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "example_last_af.json");
            using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
            return DirectApiEnvelope<JsonElement>.Ok(
                document.RootElement.GetProperty("Response").Clone());
        }

        public void Dispose() => Stop();
    }
}
