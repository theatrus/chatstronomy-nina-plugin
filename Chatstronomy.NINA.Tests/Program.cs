using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Remote;
using Chatstronomy.NINA.Runtime;
using Chatstronomy.NINA.Settings;
using Newtonsoft.Json.Linq;
using NINA.Plugin;
using NINA.Plugin.ManifestDefinition;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

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
        Run("Advanced API polling settings are validated", AdvancedApiPollingIsValidated);
        Run("Hosted mode defaults to the Chatstronomy hub", HostedModeDefaultsToHub);
        Run("Legacy hosted defaults migrate to the hub", LegacyHostedDefaultsMigrateToHub);
        Run("Hosted hub URLs require TLS and map to Direct WSS", HostedHubUrlsAreSecure);
        await RunAsync(
            "Development repository manifest matches N.I.N.A.'s plugin contract",
            DevelopmentRepositoryManifestMatchesNinaContract);
        Run("Hosted pair and auth frames match the Rust contract", HostedHandshakeFramesMatchRust);
        Run("Hosted client accepts unmarked legacy hub payloads", HostedLegacyHubPayloadsAreAccepted);
        Run("Hosted secrets are scoped to profile and hub origin", HostedSecretsAreOriginScoped);
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
        Run("Direct source does not require Advanced API settings", DirectSourceNeedsNoAdvancedApi);
        Run("Plugin runtime bootstrap is source-explicit", PluginRuntimeBootstrapIsSourceExplicit);
        Run("Direct runtime bootstrap carries only its pipe", DirectRuntimeBootstrapCarriesOnlyPipe);
        Run("Direct commands use semantic wire names", DirectCommandsUseSemanticWireNames);
        Run("Direct camera queries use the shared equipment contract", DirectCameraQueryUsesSharedContract);
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
                "Plugin runtime can detach when configured to outlive N.I.N.A.",
                () => PluginRuntimeDetaches(runtimePath));
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

        AssertEqual("Chatstronomy", manifest.Name);
        AssertEqual("0.1.0.11", manifest.Version.ToString());
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

        var noDeadline = DirectProtocol.ParseQuery(
            QueryJson(id, "guider_graph"));
        AssertFalse(noDeadline.IsExpiredAt(long.MaxValue));
    }

    private static void AdvancedApiPollingIsValidated()
    {
        var configuration = ChatstronomyConfigurationValidator.BuildLocalRuntime(
            Environment.ProcessPath ?? "test-runtime.exe",
            RuntimeSourceMode.AdvancedApi,
            "http://127.0.0.1:1888/",
            "7",
            startWithNina: false,
            stopWithNina: true);
        var source = AssertType<AdvancedApiPollingSourceConfiguration>(configuration.Source);
        AssertEqual("http://127.0.0.1:1888/", source.BaseUrl.AbsoluteUri);
        AssertEqual<uint>(7, source.PollIntervalSeconds);

        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.BuildLocalRuntime(
                Environment.ProcessPath ?? "test-runtime.exe",
                RuntimeSourceMode.AdvancedApi,
                "http://127.0.0.1:1888/",
                "0",
                startWithNina: false,
                stopWithNina: true));
    }

    private static void DirectSourceNeedsNoAdvancedApi()
    {
        var configuration = ChatstronomyConfigurationValidator.BuildLocalRuntime(
            Environment.ProcessPath ?? "test-runtime.exe",
            RuntimeSourceMode.Direct,
            advancedApiBaseUrl: string.Empty,
            pollingIntervalSeconds: string.Empty,
            startWithNina: true,
            stopWithNina: false);

        AssertType<NinaDirectSourceConfiguration>(configuration.Source);
        AssertTrue(configuration.StopWithNina);
        AssertThrows<InvalidOperationException>(() =>
            ChatstronomyConfigurationValidator.BuildLocalRuntime(
                Environment.ProcessPath ?? "test-runtime.exe",
                RuntimeSourceMode.Direct,
                advancedApiBaseUrl: string.Empty,
                pollingIntervalSeconds: string.Empty,
                startWithNina: false,
                stopWithNina: false));
    }

    private static void PluginRuntimeBootstrapIsSourceExplicit()
    {
        var configuration = BuildRuntimeConfiguration(
            Environment.ProcessPath ?? "test-runtime.exe");
        var json = PluginRuntimeBootstrap.Serialize(
            configuration,
            new LocalRuntimeIdentity(
                Guid.Parse("363db028-9d79-4fdc-8940-1b1ff52b9e8d"),
                Guid.Parse("460a8c62-28ce-4781-92e5-ab2440982175"),
                "North Rig"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        AssertEqual(PluginRuntimeBootstrap.ProtocolVersion,
            root.GetProperty("protocol_version").GetUInt16());
        AssertEqual("advanced_api_polling",
            root.GetProperty("source").GetProperty("kind").GetString());
        AssertEqual<uint>(5,
            root.GetProperty("source").GetProperty("poll_interval_seconds").GetUInt32());
        AssertEqual("discord_webhook",
            root.GetProperty("delivery").GetProperty("kind").GetString());
        AssertFalse(json.Contains("test-runtime.exe", StringComparison.OrdinalIgnoreCase));
    }

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
        AssertTrue(!string.IsNullOrWhiteSpace(output["ThumbnailBase64"] as string));
        AssertEqual("image/jpeg", output["ThumbnailMediaType"] as string);
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
            new LocalRuntimeConfiguration(
                runtimePath,
                new NinaDirectSourceConfiguration(),
                StartWithNina: false,
                StopWithNina: false));
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
        "0.1.0.13",
        "3.2.0.9001",
        new DirectCapabilities(true, true, true, true, true, true, true, true));

    private static string QueryJson(Guid id, string kind, long? expiresAt = null) =>
        JsonSerializer.Serialize(
            new
            {
                type = "query",
                payload = new { id, expires_at = expiresAt, kind },
            },
            DirectProtocol.JsonOptions);

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
        var controller = new ChatstronomyRuntimeController();
        var profileId = Guid.NewGuid();
        try
        {
            await controller.StartAsync(
                BuildRuntimeConfiguration(runtimePath),
                new LocalRuntimeIdentity(
                    Guid.NewGuid(),
                    profileId,
                    "Controller Integration Test"),
                CancellationToken.None);
            AssertTrue(controller.IsRunning);
            AssertTrue(controller.ProcessId.HasValue);

            await controller.StopAsync(CancellationToken.None);
            AssertFalse(controller.IsRunning);

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Chatstronomy",
                "logs",
                $"nina-runtime-{profileId:N}.log");
            var log = File.ReadAllText(logPath);
            AssertFalse(log.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase));
            AssertFalse(log.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (controller.IsRunning)
            {
                await controller.StopAsync(CancellationToken.None);
            }
        }
    }

    private static async Task PluginRuntimeDetaches(string runtimePath)
    {
        var controller = new ChatstronomyRuntimeController();
        int? processId = null;
        try
        {
            await controller.StartAsync(
                BuildRuntimeConfiguration(runtimePath, stopWithNina: false),
                new LocalRuntimeIdentity(Guid.NewGuid(), Guid.NewGuid(), "Detach Test"),
                CancellationToken.None);
            processId = controller.ProcessId;
            AssertTrue(processId.HasValue);

            await controller.DetachAsync(CancellationToken.None);
            AssertFalse(controller.IsRunning);

            await Task.Delay(250);
            using var detached = System.Diagnostics.Process.GetProcessById(processId!.Value);
            AssertFalse(detached.HasExited);
            detached.Kill(entireProcessTree: true);
            await detached.WaitForExitAsync();
        }
        finally
        {
            if (controller.IsRunning)
            {
                await controller.StopAsync(CancellationToken.None);
            }
            if (processId.HasValue)
            {
                try
                {
                    using var detached = System.Diagnostics.Process.GetProcessById(processId.Value);
                    if (!detached.HasExited)
                    {
                        detached.Kill(entireProcessTree: true);
                        await detached.WaitForExitAsync();
                    }
                }
                catch (ArgumentException)
                {
                    // Already exited and removed from the process table.
                }
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

    private static ChatstronomyConfiguration BuildRuntimeConfiguration(
        string runtimePath,
        bool stopWithNina = true) =>
        new(
            new DiscordWebhookDeliveryConfiguration(
                new Uri("https://discord.com/api/webhooks/123/token")),
            Matrix: null,
            new LocalRuntimeConfiguration(
                runtimePath,
                new AdvancedApiPollingSourceConfiguration(
                    new Uri("http://127.0.0.1:1888/"),
                    PollIntervalSeconds: 5),
                StartWithNina: true,
                StopWithNina: stopWithNina));

    private static ChatstronomyConfiguration BuildDirectRuntimeConfiguration(string runtimePath) =>
        new(
            new DiscordWebhookDeliveryConfiguration(
                new Uri("https://discord.com/api/webhooks/123/token")),
            Matrix: null,
            new LocalRuntimeConfiguration(
                runtimePath,
                new NinaDirectSourceConfiguration(),
                StartWithNina: true,
                StopWithNina: true));

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

    private sealed class FakeDirectDataProvider : INinaDirectDataProvider
    {
        private int queryCount;
        private readonly ConcurrentBag<DirectQueryKind> queriedKinds = new();

        public DirectCapabilities Capabilities { get; } = new(
            EventHistory: true,
            ImageHistory: true,
            Thumbnails: true,
            Sequence: true,
            EquipmentSnapshots: true,
            AutofocusDetails: true,
            GuiderGraph: true,
            Commands: true);

        public int QueryCount => Volatile.Read(ref queryCount);
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

        public Task<object?> ExecuteAsync(
            DirectQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref queryCount);
            queriedKinds.Add(query.Kind);
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
