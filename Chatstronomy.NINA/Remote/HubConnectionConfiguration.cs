using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Remote;

internal sealed record HubConnectionConfiguration(
    Uri ServiceUrl,
    string? Credential,
    string? PairingToken,
    Guid ProfileId,
    bool AllowInsecureLoopback = false)
{
    internal Uri WebSocketUrl => BuildWebSocketUrl(ServiceUrl, AllowInsecureLoopback);

    internal void Validate()
    {
        _ = BuildWebSocketUrl(ServiceUrl, AllowInsecureLoopback);
        if (string.IsNullOrWhiteSpace(Credential)
            && string.IsNullOrWhiteSpace(PairingToken))
        {
            throw new InvalidOperationException(
                "Enter a one-time pairing code or connect this profile through the hosted credential flow.");
        }
    }

    internal static Uri BuildWebSocketUrl(
        Uri serviceUrl,
        bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(serviceUrl);
        var secure = serviceUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || serviceUrl.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        var insecureLoopback = allowInsecureLoopback
            && serviceUrl.IsLoopback
            && (serviceUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || serviceUrl.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase));
        if (!serviceUrl.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(serviceUrl.Host)
            || (!secure && !insecureLoopback))
        {
            throw new InvalidOperationException(
                "Chatstronomy service URL must be an absolute HTTPS or WSS URL.");
        }
        if (!string.IsNullOrEmpty(serviceUrl.UserInfo)
            || !string.IsNullOrEmpty(serviceUrl.Query)
            || !string.IsNullOrEmpty(serviceUrl.Fragment))
        {
            throw new InvalidOperationException(
                "Chatstronomy service URL cannot contain credentials, a query, or a fragment.");
        }

        var path = serviceUrl.AbsolutePath.TrimEnd('/');
        if (path.Length > 0
            && !path.Equals(DirectProtocol.WebSocketPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Chatstronomy service URL path must be empty or {DirectProtocol.WebSocketPath}.");
        }

        return new UriBuilder(serviceUrl)
        {
            Scheme = secure ? "wss" : "ws",
            Port = serviceUrl.IsDefaultPort ? -1 : serviceUrl.Port,
            Path = DirectProtocol.WebSocketPath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }
}
