using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Runtime;

internal static class PluginRuntimeBootstrap
{
    internal const ushort ProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static string Serialize(
        ChatstronomyConfiguration configuration,
        LocalRuntimeIdentity identity,
        string? directPipeName = null,
        DirectCapabilities? directCapabilities = null)
    {
        var localRuntime = configuration.LocalRuntime
            ?? throw new InvalidOperationException(
                "A local runtime configuration is required for local process startup.");

        var source = new RuntimeSourcePayload
        {
            Kind = "nina_direct",
            PipeName = string.IsNullOrWhiteSpace(directPipeName)
                ? throw new InvalidOperationException(
                    "A Direct data pipe is required for native N.I.N.A. mode.")
                : directPipeName,
            Capabilities = directCapabilities
                ?? throw new InvalidOperationException(
                    "Direct capabilities are required for native N.I.N.A. mode."),
        };

        var delivery = configuration.Delivery switch
        {
            DiscordWebhookDeliveryConfiguration webhook => new RuntimeDeliveryPayload
            {
                Kind = "discord_webhook",
                WebhookUrl = webhook.WebhookUrl.AbsoluteUri,
            },
            DiscordBotDeliveryConfiguration bot => new RuntimeDeliveryPayload
            {
                Kind = "discord_bot",
                BotToken = bot.BotToken,
                ApplicationId = bot.ApplicationId,
                DefaultChannelId = bot.DefaultChannelId,
            },
            null => null,
            HostedDeliveryConfiguration => throw new InvalidOperationException(
                "Hosted delivery cannot be passed to a local Chatstronomy runtime."),
            _ => throw new InvalidOperationException("Unknown Chatstronomy delivery mode."),
        };

        var matrix = configuration.Matrix is { } matrixConfiguration
            ? new RuntimeMatrixPayload
            {
                HomeserverUrl = matrixConfiguration.HomeserverUrl.AbsoluteUri,
                Username = matrixConfiguration.Username,
                Password = matrixConfiguration.Password,
                DefaultRoomId = matrixConfiguration.DefaultRoomId,
            }
            : null;

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chatstronomy");
        Directory.CreateDirectory(dataDirectory);

        var payload = new RuntimeBootstrapPayload
        {
            ProtocolVersion = ProtocolVersion,
            Profile = new RuntimeProfilePayload
            {
                NodeId = identity.NodeId,
                ProfileId = identity.ProfileId,
                ProfileName = identity.ProfileName,
            },
            Source = source,
            Delivery = delivery,
            Matrix = matrix,
            DataDirectory = dataDirectory,
            ExitOnControlDisconnect = true,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed class RuntimeBootstrapPayload
    {
        public required ushort ProtocolVersion { get; init; }

        public required RuntimeProfilePayload Profile { get; init; }

        public required RuntimeSourcePayload Source { get; init; }

        public RuntimeDeliveryPayload? Delivery { get; init; }

        public RuntimeMatrixPayload? Matrix { get; init; }

        public required string DataDirectory { get; init; }

        public required bool ExitOnControlDisconnect { get; init; }
    }

    private sealed class RuntimeProfilePayload
    {
        public required Guid NodeId { get; init; }

        public required Guid ProfileId { get; init; }

        public required string ProfileName { get; init; }
    }

    private sealed class RuntimeSourcePayload
    {
        public required string Kind { get; init; }

        public string? PipeName { get; init; }

        public DirectCapabilities? Capabilities { get; init; }

    }

    private sealed class RuntimeDeliveryPayload
    {
        public required string Kind { get; init; }

        public string? WebhookUrl { get; init; }

        public string? BotToken { get; init; }

        public ulong? ApplicationId { get; init; }

        public ulong? DefaultChannelId { get; init; }
    }

    private sealed class RuntimeMatrixPayload
    {
        public required string HomeserverUrl { get; init; }

        public required string Username { get; init; }

        public required string Password { get; init; }

        public required string DefaultRoomId { get; init; }
    }
}
