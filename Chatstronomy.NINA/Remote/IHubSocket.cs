using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace Chatstronomy.NINA.Remote;

internal interface IHubSocket : IAsyncDisposable
{
    void Abort();

    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    Task SendTextAsync(string message, CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

internal interface IHubSocketFactory
{
    IHubSocket Create();
}

internal sealed class ClientWebSocketFactory : IHubSocketFactory
{
    public IHubSocket Create() => new ClientWebSocketAdapter();
}

internal sealed class ClientWebSocketAdapter : IHubSocket
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private const int MaxInboundMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket socket = new();

    internal ClientWebSocketAdapter()
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public void Abort() => socket.Abort();

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
        socket.ConnectAsync(endpoint, cancellationToken);

    public async Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("The Chatstronomy hub sent a non-text frame.");
            }
            if (content.Length + result.Count > MaxInboundMessageBytes)
            {
                throw new InvalidDataException("The Chatstronomy hub frame exceeds the size limit.");
            }

            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
