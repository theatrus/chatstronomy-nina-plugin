using System.IO;
using System.Net.WebSockets;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Remote;

internal sealed class ChatstronomyHubClient
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultConnectionAttemptTimeout = TimeSpan.FromSeconds(15);
    private readonly INinaDirectDataProvider provider;
    private readonly IHubSocketFactory socketFactory;
    private readonly TimeSpan connectionAttemptTimeout;
    private readonly object stateGate = new();
    private CancellationTokenSource? stopping;
    private Task? runTask;
    private string statusMessage = "Hosted connection is stopped.";
    private bool isConnected;

    internal ChatstronomyHubClient(
        INinaDirectDataProvider provider,
        IHubSocketFactory? socketFactory = null,
        TimeSpan? connectionAttemptTimeout = null)
    {
        this.provider = provider;
        this.socketFactory = socketFactory ?? new ClientWebSocketFactory();
        this.connectionAttemptTimeout = connectionAttemptTimeout
            ?? DefaultConnectionAttemptTimeout;
        if (this.connectionAttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionAttemptTimeout),
                "Hosted connection timeout must be greater than zero.");
        }
    }

    internal event EventHandler? StateChanged;

    internal event EventHandler<HubCredentialIssuedEventArgs>? CredentialIssued;

    internal bool IsRunning
    {
        get
        {
            lock (stateGate)
            {
                return runTask is { IsCompleted: false };
            }
        }
    }

    internal bool IsConnected
    {
        get
        {
            lock (stateGate)
            {
                return isConnected;
            }
        }
    }

    internal string StatusMessage
    {
        get
        {
            lock (stateGate)
            {
                return statusMessage;
            }
        }
    }

    internal async Task StartAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        CancellationToken cancellationToken)
    {
        configuration.Validate();
        ValidateHello(configuration, hello);
        await StopAsync(cancellationToken).ConfigureAwait(false);

        SetState(false, "Connecting to the Chatstronomy hub...");
        lock (stateGate)
        {
            stopping = new CancellationTokenSource();
            runTask = RunAsync(configuration, hello, stopping.Token);
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (stateGate)
        {
            cancellation = stopping;
            running = runTask;
            stopping = null;
            runTask = null;
        }

        if (cancellation is null || running is null)
        {
            SetState(false, "Hosted connection is stopped.");
            return;
        }

        cancellation.Cancel();
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
            SetState(false, "Hosted connection is stopped.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async Task RunSingleConnectionAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        CancellationToken cancellationToken)
    {
        configuration.Validate();
        ValidateHello(configuration, hello);
        var authentication = new HubAuthenticationState(
            configuration.Credential,
            configuration.PairingToken);
        await RunConnectionAsync(configuration, hello, authentication, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        CancellationToken cancellationToken)
    {
        var authentication = new HubAuthenticationState(
            configuration.Credential,
            configuration.PairingToken);
        var reconnectDelay = InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                await RunConnectionAsync(
                    configuration,
                    hello,
                    authentication,
                    cancellationToken).ConfigureAwait(false);
                throw new HubDisconnectedException("The hub closed the connection.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HubFatalException exception)
            {
                SetState(false, $"Hosted connection needs attention: {exception.Message}");
                return;
            }
            catch (Exception exception) when (
                exception is WebSocketException
                or IOException
                or InvalidDataException
                or DirectProtocolException
                or HubDisconnectedException)
            {
                if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMinutes(1))
                {
                    reconnectDelay = InitialReconnectDelay;
                }
                SetState(
                    false,
                    $"Hosted connection lost ({SafeMessage(exception)}). Retrying in {reconnectDelay.TotalSeconds:0} seconds.");
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(
                    reconnectDelay.TotalSeconds * 2,
                    MaximumReconnectDelay.TotalSeconds));
            }
            catch (Exception exception)
            {
                SetState(false, $"Hosted connection needs attention: {SafeMessage(exception)}");
                return;
            }
        }
    }

    private async Task RunConnectionAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        HubAuthenticationState authentication,
        CancellationToken cancellationToken)
    {
        SetState(false, $"Connecting securely to {configuration.ServiceUrl.Host}...");
        await using var socket = socketFactory.Create();
        await ConnectWithTimeoutAsync(
            socket,
            configuration.WebSocketUrl,
            cancellationToken).ConfigureAwait(false);

        // A durable credential always wins over a leftover one-time token. This
        // matches the Rust relay and prevents a token that could not be cleared
        // after pairing from overriding the credential on the next restart.
        var firstMessage = !string.IsNullOrWhiteSpace(authentication.Credential)
            ? DirectProtocol.SerializeAuth(authentication.Credential, hello)
            : DirectProtocol.SerializePair(
                authentication.PairingToken
                    ?? throw new HubFatalException("No hosted credential or pairing code is available."),
                hello);

        var handshakeJson = await ExchangeHandshakeWithTimeoutAsync(
            socket,
            firstMessage,
            cancellationToken).ConfigureAwait(false)
            ?? throw new HubDisconnectedException("The hub closed during authentication.");
        var handshake = DirectProtocol.ParseHubMessage(handshakeJson);
        AgentHello serverHello;
        switch (handshake)
        {
            case HubPairResultMessage paired:
                ValidateAgentHello(paired.Hello, hello);
                authentication.Credential = paired.Credential;
                authentication.PairingToken = null;
                CredentialIssued?.Invoke(
                    this,
                    new HubCredentialIssuedEventArgs(
                        configuration.ProfileId,
                        configuration.ServiceUrl,
                        paired.Credential));
                serverHello = paired.Hello;
                break;
            case HubAgentHelloMessage authenticated:
                ValidateAgentHello(authenticated.Hello, hello);
                serverHello = authenticated.Hello;
                break;
            case HubErrorMessage error:
                throw ErrorFromHub(error);
            default:
                throw new HubFatalException("The hub returned an unexpected authentication response.");
        }

        SetState(
            true,
            $"Connected to {configuration.ServiceUrl.Host} (connection {serverHello.ConnectionId:D}).");
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        var receiveTask = ReceiveLoopAsync(socket, sendGate, connected.Token);
        var heartbeatTask = HeartbeatLoopAsync(socket, sendGate, connected.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
        connected.Cancel();
        try
        {
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
            await IgnoreCancellationAsync(heartbeatTask).ConfigureAwait(false);
            SetState(false, "Hosted connection is reconnecting.");
        }
    }

    private async Task ReceiveLoopAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new HubDisconnectedException("The hub closed the connection.");
            switch (DirectProtocol.ParseHubMessage(json))
            {
                case HubQueryMessage query:
                    await AnswerQueryAsync(socket, sendGate, query.Query, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case HubHeartbeatAckMessage:
                case HubUnknownMessage:
                    break;
                case HubErrorMessage error:
                    throw ErrorFromHub(error);
            }
        }
    }

    private async Task AnswerQueryAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        DirectQuery query,
        CancellationToken cancellationToken)
    {
        string response;
        if (query.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            response = DirectProtocol.SerializeFailure(query.Id, "query expired before execution");
        }
        else
        {
            try
            {
                var payload = await provider.ExecuteAsync(query, cancellationToken)
                    .ConfigureAwait(false);
                response = DirectProtocol.SerializeSuccess(query.Id, payload);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                response = DirectProtocol.SerializeFailure(query.Id, SafeMessage(exception));
            }
        }

        await SendAsync(socket, sendGate, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HeartbeatLoopAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        ulong sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendAsync(
                socket,
                sendGate,
                DirectProtocol.SerializeHeartbeat(++sequence),
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        string message,
        CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendTextAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task ConnectWithTimeoutAsync(
        IHubSocket socket,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionAttemptTimeout);
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            throw new HubDisconnectedException(
                $"Timed out connecting to {endpoint.Host}.");
        }
    }

    private async Task<string?> ExchangeHandshakeWithTimeoutAsync(
        IHubSocket socket,
        string firstMessage,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionAttemptTimeout);
        try
        {
            await socket.SendTextAsync(firstMessage, timeout.Token).ConfigureAwait(false);
            return await socket.ReceiveTextAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            throw new HubDisconnectedException(
                "Timed out waiting for the hub authentication response.");
        }
    }

    private static void ValidateHello(
        HubConnectionConfiguration configuration,
        ClientHello hello)
    {
        if (configuration.ProfileId != hello.ProfileId)
        {
            throw new InvalidOperationException(
                "Hosted connection profile does not match the N.I.N.A. profile identity.");
        }
    }

    private static void ValidateAgentHello(AgentHello server, ClientHello client)
    {
        if (server.ProtocolVersion != DirectProtocol.CurrentVersion)
        {
            throw new HubFatalException(
                $"The hub uses Direct protocol {server.ProtocolVersion}; this plugin uses {DirectProtocol.CurrentVersion}.");
        }
        if (server.NodeId != client.NodeId || server.ProfileId != client.ProfileId)
        {
            throw new HubFatalException("The hub authenticated a different rig identity.");
        }
    }

    private static Exception ErrorFromHub(HubErrorMessage error) =>
        error.Retryable
            ? new HubDisconnectedException(error.Message)
            : new HubFatalException(error.Message);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The task selected by WhenAny already supplied the useful error.
        }
    }

    private static string SafeMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Replace('\r', ' ').Replace('\n', ' ');

    private void SetState(bool connected, string message)
    {
        lock (stateGate)
        {
            isConnected = connected;
            statusMessage = message;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class HubAuthenticationState(string? credential, string? pairingToken)
    {
        internal string? Credential { get; set; } = credential;

        internal string? PairingToken { get; set; } = pairingToken;
    }
}

internal sealed class HubCredentialIssuedEventArgs(
    Guid profileId,
    Uri serviceUrl,
    string credential)
    : EventArgs
{
    internal Guid ProfileId { get; } = profileId;

    internal Uri ServiceUrl { get; } = serviceUrl;

    internal string Credential { get; } = credential;
}

internal sealed class HubDisconnectedException(string message) : IOException(message);

internal sealed class HubFatalException(string message) : Exception(message);
