using System.IO;
using Chatstronomy.NINA.Settings;

namespace Chatstronomy.NINA.Configuration;

internal abstract record ChatDeliveryConfiguration;

internal sealed record DiscordWebhookDeliveryConfiguration(Uri WebhookUrl)
    : ChatDeliveryConfiguration;

internal sealed record DiscordBotDeliveryConfiguration(
    string BotToken,
    ulong? ApplicationId,
    ulong DefaultChannelId)
    : ChatDeliveryConfiguration;

internal sealed record HostedDeliveryConfiguration(Uri ServiceUrl)
    : ChatDeliveryConfiguration;

internal sealed record MatrixDeliveryConfiguration(
    Uri HomeserverUrl,
    string Username,
    string Password,
    string DefaultRoomId);

internal abstract record RuntimeSourceConfiguration;

internal sealed record NinaDirectSourceConfiguration : RuntimeSourceConfiguration;

internal sealed record AdvancedApiPollingSourceConfiguration(
    Uri BaseUrl,
    uint PollIntervalSeconds)
    : RuntimeSourceConfiguration;

internal sealed record LocalRuntimeConfiguration(
    string ExecutablePath,
    RuntimeSourceConfiguration Source,
    bool StartWithNina,
    bool StopWithNina);

internal sealed record ChatstronomyConfiguration(
    ChatDeliveryConfiguration? Delivery,
    MatrixDeliveryConfiguration? Matrix,
    LocalRuntimeConfiguration? LocalRuntime);

internal static class ChatstronomyConfigurationValidator
{
    public static Uri RequireDiscordWebhook(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsDiscordHost(uri.Host)
            || !uri.IsDefaultPort
            || !IsDiscordWebhookPath(uri.AbsolutePath))
        {
            throw new InvalidOperationException(
                "Enter a complete HTTPS Discord webhook URL containing its numeric ID and token.");
        }

        return uri;
    }

    public static ulong RequireDiscordSnowflake(string value, string label)
    {
        if (!ulong.TryParse(value, out var id) || id == 0)
        {
            throw new InvalidOperationException($"{label} must be a numeric Discord ID.");
        }

        return id;
    }

    public static ulong? OptionalDiscordSnowflake(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RequireDiscordSnowflake(value, label);
    }

    public static string RequireSecret(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        return value;
    }

    public static Uri RequireHostedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Chatstronomy service URL must be an absolute HTTPS or WSS URL without credentials, a query, or a fragment.");
        }

        return uri;
    }

    public static Uri RequireMatrixHomeserver(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                "Matrix homeserver URL must be an absolute https:// URL.");
        }

        return uri;
    }

    public static Uri RequireAdvancedApiUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                "N.I.N.A. Advanced API URL must be an absolute http:// or https:// URL.");
        }

        return uri;
    }

    public static LocalRuntimeConfiguration BuildLocalRuntime(
        string executablePath,
        RuntimeSourceMode sourceMode,
        string advancedApiBaseUrl,
        string pollingIntervalSeconds,
        bool startWithNina,
        bool stopWithNina)
    {
        var path = executablePath?.Trim() ?? string.Empty;
        if (sourceMode == RuntimeSourceMode.Direct && !startWithNina)
        {
            throw new InvalidOperationException(
                "Direct mode requires Chatstronomy to start with N.I.N.A.");
        }
        if (startWithNina && string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Select the Chatstronomy runtime executable or turn off automatic start.");
        }

        if (startWithNina && !File.Exists(path))
        {
            throw new InvalidOperationException(
                "The configured Chatstronomy runtime executable was not found.");
        }

        // Only a process started by this plugin is eligible for teardown. This
        // prevents "Stop with N.I.N.A." from terminating a separately managed
        // Chatstronomy instance.
        RuntimeSourceConfiguration source = sourceMode switch
        {
            RuntimeSourceMode.Direct => new NinaDirectSourceConfiguration(),
            RuntimeSourceMode.AdvancedApi => BuildAdvancedApiSource(
                advancedApiBaseUrl,
                pollingIntervalSeconds),
            _ => throw new InvalidOperationException("Unknown Chatstronomy source mode."),
        };

        return new LocalRuntimeConfiguration(
            path,
            source,
            startWithNina,
            startWithNina && (sourceMode == RuntimeSourceMode.Direct || stopWithNina));
    }

    private static AdvancedApiPollingSourceConfiguration BuildAdvancedApiSource(
        string baseUrl,
        string pollingIntervalSeconds)
    {
        if (!uint.TryParse(pollingIntervalSeconds, out var interval)
            || interval is < 1 or > 300)
        {
            throw new InvalidOperationException(
                "Polling interval must be a whole number from 1 to 300 seconds.");
        }

        return new AdvancedApiPollingSourceConfiguration(
            RequireAdvancedApiUrl(baseUrl),
            interval);
    }

    private static bool IsDiscordHost(string host) =>
        host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiscordWebhookPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var webhookIdIndex = 2;

        if (segments.Length == 5
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && IsDiscordApiVersion(segments[1])
            && segments[2].Equals("webhooks", StringComparison.OrdinalIgnoreCase))
        {
            webhookIdIndex = 3;
        }
        else if (segments.Length != 4
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("webhooks", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ulong.TryParse(segments[webhookIdIndex], out var webhookId)
            && webhookId != 0
            && !string.IsNullOrWhiteSpace(segments[webhookIdIndex + 1]);
    }

    private static bool IsDiscordApiVersion(string value) =>
        value.Length > 1
        && (value[0] == 'v' || value[0] == 'V')
        && value.Skip(1).All(char.IsDigit);
}
