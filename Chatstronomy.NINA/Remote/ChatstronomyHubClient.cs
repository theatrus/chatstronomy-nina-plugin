using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Remote;

internal sealed class ChatstronomyHubClient
{
    private readonly INinaDirectDataProvider provider;
    private readonly IHubSocketFactory socketFactory;
    private readonly HubClientTimings timings;
    private readonly Func<double> jitterSource;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim providerGate = new(1, 1);
    private readonly object stateGate = new();
    private CancellationTokenSource? stopping;
    private Task? runTask;
    private IHubSocket? activeSocket;
    private string statusMessage = "Hosted connection is stopped.";
    private bool isConnected;
    private long lifecycleGeneration;

    internal ChatstronomyHubClient(
        INinaDirectDataProvider provider,
        IHubSocketFactory? socketFactory = null,
        TimeSpan? connectionAttemptTimeout = null,
        HubClientTimings? timings = null,
        Func<double>? jitterSource = null)
    {
        this.provider = provider;
        this.socketFactory = socketFactory ?? new ClientWebSocketFactory();
        this.timings = timings ?? HubClientTimings.Default;
        if (connectionAttemptTimeout is { } timeout)
        {
            this.timings = this.timings with { ConnectionAttemptTimeout = timeout };
        }
        this.timings.Validate();
        this.jitterSource = jitterSource ?? Random.Shared.NextDouble;
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
        // Bind the identity to its originating Direct session before awaiting
        // the lifecycle gate; a privacy or profile transition may happen
        // while it is queued.
        var directSessionToken = provider.DirectSessionToken;
        configuration.Validate();
        ValidateHello(configuration, hello);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            directSessionToken);
        await lifecycleGate.WaitAsync(starting.Token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            directSessionToken.ThrowIfCancellationRequested();
            lock (stateGate)
            {
                var generation = ++lifecycleGeneration;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    directSessionToken);
                stopping = cancellation;
                runTask = Task.Run(
                    () => RunAsync(
                        configuration,
                        hello,
                        generation,
                        directSessionToken,
                        cancellation.Token),
                    CancellationToken.None);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cancellation;
        Task? running;
        IHubSocket? socket;
        lock (stateGate)
        {
            ++lifecycleGeneration;
            cancellation = stopping;
            running = runTask;
            socket = activeSocket;
            stopping = null;
            runTask = null;
            activeSocket = null;
        }

        SetState(false, "Hosted connection is stopped.");
        cancellation?.Cancel();
        SafeAbort(socket);
        if (cancellation is null || running is null)
        {
            cancellation?.Dispose();
            return;
        }

        var completed = await WaitForShutdownAsync(running).ConfigureAwait(false);
        if (completed)
        {
            cancellation.Dispose();
        }
        else
        {
            DisposeWhenComplete(running, cancellation);
        }

        SetState(false, "Hosted connection is stopped.");
    }

    internal async Task RunSingleConnectionAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        CancellationToken cancellationToken)
    {
        var directSessionToken = provider.DirectSessionToken;
        configuration.Validate();
        ValidateHello(configuration, hello);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            directSessionToken);
        session.Token.ThrowIfCancellationRequested();
        long generation;
        lock (stateGate)
        {
            generation = ++lifecycleGeneration;
        }
        var authentication = new HubAuthenticationState(
            configuration.Credential,
            configuration.PairingToken);
        var attempt = new HubConnectionAttempt();
        await RunConnectionAsync(
            configuration,
            hello,
            authentication,
            attempt,
            generation,
            directSessionToken,
            session.Token).ConfigureAwait(false);
    }

    private async Task RunAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        long generation,
        CancellationToken directSessionToken,
        CancellationToken cancellationToken)
    {
        var authentication = new HubAuthenticationState(
            configuration.Credential,
            configuration.PairingToken);
        var reconnectDelay = timings.InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attempt = new HubConnectionAttempt();
            try
            {
                await RunConnectionAsync(
                    configuration,
                    hello,
                    authentication,
                    attempt,
                    generation,
                    directSessionToken,
                    cancellationToken).ConfigureAwait(false);
                throw new HubDisconnectedException("The hub closed the connection.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Abort commonly surfaces as WebSocketException or
                // ObjectDisposedException. A requested stop must never turn
                // that into another reconnect attempt.
                return;
            }
            catch (HubFatalException exception)
            {
                SetStateForGeneration(
                    generation,
                    false,
                    $"Hosted connection needs attention: {exception.Message}");
                return;
            }
            catch (Exception exception) when (
                exception is WebSocketException
                or IOException
                or InvalidDataException
                or JsonException
                or DirectProtocolException
                or HubDisconnectedException)
            {
                if (attempt.WasStableFor(timings.StableConnectionDuration))
                {
                    reconnectDelay = timings.InitialReconnectDelay;
                }

                var retryDelay = Jittered(reconnectDelay);
                SetStateForGeneration(
                    generation,
                    false,
                    $"Hosted connection lost ({SafeMessage(exception)}). Retrying in {retryDelay.TotalSeconds:0.0} seconds.");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                reconnectDelay = reconnectDelay.Ticks
                    >= timings.MaximumReconnectDelay.Ticks / 2
                        ? timings.MaximumReconnectDelay
                        : TimeSpan.FromTicks(reconnectDelay.Ticks * 2);
            }
            catch (Exception exception)
            {
                SetStateForGeneration(
                    generation,
                    false,
                    $"Hosted connection needs attention: {SafeMessage(exception)}");
                return;
            }
        }
    }

    private async Task RunConnectionAsync(
        HubConnectionConfiguration configuration,
        ClientHello hello,
        HubAuthenticationState authentication,
        HubConnectionAttempt attempt,
        long generation,
        CancellationToken directSessionToken,
        CancellationToken cancellationToken)
    {
        SetStateForGeneration(
            generation,
            false,
            $"Connecting securely to {configuration.ServiceUrl.Host}...");
        await using var socket = socketFactory.Create();
        if (!TrySetActiveSocket(generation, socket))
        {
            SafeAbort(socket);
            throw new OperationCanceledException(cancellationToken);
        }

        using var abortOnStop = cancellationToken.Register(
            static state => SafeAbort((IHubSocket?)state),
            socket);
        try
        {
            await ConnectWithTimeoutAsync(
                socket,
                configuration.WebSocketUrl,
                cancellationToken).ConfigureAwait(false);

            // A durable credential always wins over a leftover one-time token.
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
                    cancellationToken.ThrowIfCancellationRequested();
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

            cancellationToken.ThrowIfCancellationRequested();
            attempt.MarkAuthenticated();
            provider.BeginDirectTransport(directSessionToken);
            var payloadStatus = serverHello.PayloadVersion < DirectProtocol.CurrentPayloadVersion
                ? $", legacy payload v{serverHello.PayloadVersion}"
                : $", payload v{serverHello.PayloadVersion}";
            SetStateForGeneration(
                generation,
                true,
                $"Connected to {configuration.ServiceUrl.Host} (connection {serverHello.ConnectionId:D}{payloadStatus}).");

            var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendGate = new SemaphoreSlim(1, 1);
            var heartbeats = new HeartbeatAcknowledgements();
            var queries = Channel.CreateBounded<DirectQuery>(new BoundedChannelOptions(
                timings.QueryQueueCapacity)
            {
                SingleReader = true,
                // Receive writes queries while supervisor cleanup may
                // concurrently complete the channel.
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var receiveTask = ReceiveLoopAsync(
                socket,
                queries.Writer,
                heartbeats,
                attempt,
                connected.Token);
            var heartbeatTask = HeartbeatLoopAsync(
                socket,
                sendGate,
                heartbeats,
                connected.Token);
            var queryTask = QueryLoopAsync(
                socket,
                sendGate,
                queries.Reader,
                directSessionToken,
                connected.Token);
            var connectionTasks = new[] { receiveTask, heartbeatTask, queryTask };
            try
            {
                var completed = await Task.WhenAny(connectionTasks).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                throw new HubDisconnectedException("A hosted connection worker stopped unexpectedly.");
            }
            finally
            {
                SetStateForGeneration(
                    generation,
                    false,
                    cancellationToken.IsCancellationRequested
                        ? "Hosted connection is stopping."
                        : "Hosted connection is reconnecting.");
                connected.Cancel();
                queries.Writer.TryComplete();
                SafeAbort(socket);
                var drained = await DrainConnectionTasksAsync(connectionTasks).ConfigureAwait(false);
                if (drained)
                {
                    connected.Dispose();
                    sendGate.Dispose();
                }
                else
                {
                    DisposeWhenComplete(
                        Task.WhenAll(connectionTasks.Select(IgnoreCompletionAsync)),
                        connected,
                        sendGate);
                }
            }
        }
        finally
        {
            ClearActiveSocket(socket);
        }
    }

    private async Task ReceiveLoopAsync(
        IHubSocket socket,
        ChannelWriter<DirectQuery> queries,
        HeartbeatAcknowledgements heartbeats,
        HubConnectionAttempt attempt,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new HubDisconnectedException("The hub closed the connection.");
            switch (DirectProtocol.ParseHubMessage(json))
            {
                case HubQueryMessage query:
                    if (!queries.TryWrite(query.Query))
                    {
                        throw new HubDisconnectedException(
                            "The hosted query queue is full; reconnecting to protect N.I.N.A. responsiveness.");
                    }
                    break;
                case HubHeartbeatAckMessage acknowledgement:
                    if (heartbeats.Acknowledge(acknowledgement.Sequence))
                    {
                        attempt.MarkHeartbeatAcknowledged();
                    }
                    break;
                case HubUnknownMessage:
                    break;
                case HubErrorMessage error:
                    throw ErrorFromHub(error);
            }
        }
    }

    private async Task QueryLoopAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        ChannelReader<DirectQuery> queries,
        CancellationToken directSessionToken,
        CancellationToken cancellationToken)
    {
        await foreach (var query in queries.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await AnswerQueryAsync(
                socket,
                sendGate,
                query,
                directSessionToken,
                cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AnswerQueryAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        DirectQuery query,
        CancellationToken directSessionToken,
        CancellationToken cancellationToken)
    {
        string response;
        var queryCompleted = false;
        if (query.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            response = DirectProtocol.SerializeFailure(query.Id, "query expired before execution");
        }
        else
        {
            await providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (query.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    response = DirectProtocol.SerializeFailure(
                        query.Id,
                        "query expired before execution");
                }
                else
                {
                    try
                    {
                        var payload = await provider.ExecuteAsync(
                            query,
                            cancellationToken,
                            directSessionToken)
                            .ConfigureAwait(false);
                        response = DirectProtocol.SerializeSuccess(query.Id, payload);
                        queryCompleted = true;
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested)
                    {
                        response = DirectProtocol.SerializeFailure(
                            query.Id,
                            SafeMessage(exception),
                            DirectProtocol.FailureCode(exception));
                    }
                }
            }
            finally
            {
                providerGate.Release();
            }
        }

        await SendAsync(socket, sendGate, response, cancellationToken).ConfigureAwait(false);
        if (queryCompleted)
        {
            provider.ConfirmDirectQueryResponse(query, directSessionToken);
        }
    }

    private async Task HeartbeatLoopAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        HeartbeatAcknowledgements heartbeats,
        CancellationToken cancellationToken)
    {
        ulong sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var acknowledgement = heartbeats.Expect(++sequence);
            await SendAsync(
                socket,
                sendGate,
                DirectProtocol.SerializeHeartbeat(sequence),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await acknowledgement.WaitAsync(
                    timings.HeartbeatAcknowledgementTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new HubDisconnectedException(
                    $"The hub did not acknowledge heartbeat {sequence} within {timings.HeartbeatAcknowledgementTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(timings.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(
        IHubSocket socket,
        SemaphoreSlim sendGate,
        string message,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await AwaitWithTimeoutAsync(
                socket,
                token => sendGate.WaitAsync(token),
                timings.SocketOperationTimeout,
                "Timed out waiting to send data to the hub.",
                cancellationToken).ConfigureAwait(false);
            acquired = true;
            await AwaitWithTimeoutAsync(
                socket,
                token => socket.SendTextAsync(message, token),
                timings.SocketOperationTimeout,
                "Timed out sending data to the hub.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                sendGate.Release();
            }
        }
    }

    private Task ConnectWithTimeoutAsync(
        IHubSocket socket,
        Uri endpoint,
        CancellationToken cancellationToken) =>
        AwaitWithTimeoutAsync(
            socket,
            token => socket.ConnectAsync(endpoint, token),
            timings.ConnectionAttemptTimeout,
            $"Timed out connecting to {endpoint.Host}.",
            cancellationToken);

    private async Task<string?> ExchangeHandshakeWithTimeoutAsync(
        IHubSocket socket,
        string firstMessage,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        await AwaitWithTimeoutAsync(
            socket,
            token => socket.SendTextAsync(firstMessage, token),
            timings.ConnectionAttemptTimeout,
            "Timed out sending authentication to the hub.",
            cancellationToken).ConfigureAwait(false);
        var remaining = timings.ConnectionAttemptTimeout
            - Stopwatch.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            SafeAbort(socket);
            throw new HubDisconnectedException(
                "Timed out waiting for the hub authentication response.");
        }
        return await AwaitWithTimeoutAsync(
            socket,
            token => socket.ReceiveTextAsync(token),
            remaining,
            "Timed out waiting for the hub authentication response.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AwaitWithTimeoutAsync(
        IHubSocket socket,
        Func<CancellationToken, Task> operationFactory,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);
        Task operation = operationFactory(operationCancellation.Token);
        try
        {
            await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SafeAbort(socket);
            ObserveFault(operation);
            throw new HubDisconnectedException(timeoutMessage);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && operationCancellation.IsCancellationRequested)
        {
            SafeAbort(socket);
            ObserveFault(operation);
            throw new HubDisconnectedException(timeoutMessage);
        }
    }

    private static async Task<T> AwaitWithTimeoutAsync<T>(
        IHubSocket socket,
        Func<CancellationToken, Task<T>> operationFactory,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout);
        Task<T> operation = operationFactory(operationCancellation.Token);
        try
        {
            return await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SafeAbort(socket);
            ObserveFault(operation);
            throw new HubDisconnectedException(timeoutMessage);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && operationCancellation.IsCancellationRequested)
        {
            SafeAbort(socket);
            ObserveFault(operation);
            throw new HubDisconnectedException(timeoutMessage);
        }
    }

    private async Task<bool> DrainConnectionTasksAsync(IEnumerable<Task> tasks)
    {
        var draining = Task.WhenAll(tasks.Select(IgnoreCompletionAsync));
        try
        {
            await draining.WaitAsync(timings.ShutdownTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            ObserveFault(draining);
            return false;
        }
    }

    private async Task<bool> WaitForShutdownAsync(Task running)
    {
        try
        {
            await running.WaitAsync(timings.ShutdownTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            ObserveFault(running);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch
        {
            // Stopping owns final state; the run loop already reported any
            // actionable fault before it ended.
            return true;
        }
    }

    private TimeSpan Jittered(TimeSpan delay)
    {
        var sample = jitterSource();
        if (!double.IsFinite(sample))
        {
            sample = 0.5;
        }
        sample = Math.Clamp(sample, 0, 1);
        var factor = 0.8 + (sample * 0.4);
        var ticks = (long)Math.Min(
            delay.Ticks * factor,
            timings.MaximumReconnectDelay.Ticks);
        return TimeSpan.FromTicks(Math.Max(1, ticks));
    }

    private bool TrySetActiveSocket(long generation, IHubSocket socket)
    {
        lock (stateGate)
        {
            if (lifecycleGeneration != generation)
            {
                return false;
            }
            activeSocket = socket;
            return true;
        }
    }

    private void ClearActiveSocket(IHubSocket socket)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(activeSocket, socket))
            {
                activeSocket = null;
            }
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
        if (server.PayloadVersion < DirectProtocol.LegacyPayloadVersion
            || server.PayloadVersion > client.PayloadVersion)
        {
            throw new HubFatalException(
                $"The hub selected unsupported Direct payload version {server.PayloadVersion}; this plugin supports {DirectProtocol.LegacyPayloadVersion} through {client.PayloadVersion}.");
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

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void DisposeWhenComplete(Task task, params IDisposable[] resources)
    {
        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                foreach (var resource in resources)
                {
                    resource.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void SafeAbort(IHubSocket? socket)
    {
        try
        {
            socket?.Abort();
        }
        catch
        {
            // Abort is best effort and must remain safe during N.I.N.A.
            // shutdown, including races with socket disposal.
        }
    }

    private static string SafeMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : NinaDirectDataProvider.RedactCommandError(
                exception.Message.Replace('\r', ' ').Replace('\n', ' '));

    private void SetState(bool connected, string message)
    {
        EventHandler? handlers;
        lock (stateGate)
        {
            isConnected = connected;
            statusMessage = message;
            handlers = StateChanged;
        }
        InvokeStateChanged(handlers);
    }

    private void SetStateForGeneration(long generation, bool connected, string message)
    {
        EventHandler? handlers;
        lock (stateGate)
        {
            if (lifecycleGeneration != generation)
            {
                return;
            }
            isConnected = connected;
            statusMessage = message;
            handlers = StateChanged;
        }
        InvokeStateChanged(handlers);
    }

    private void InvokeStateChanged(EventHandler? handlers)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // UI/status observers cannot own transport liveness.
            }
        }
    }

    private sealed class HeartbeatAcknowledgements
    {
        private readonly object gate = new();
        private ulong expectedSequence;
        private TaskCompletionSource? pending;

        internal Task Expect(ulong sequence)
        {
            lock (gate)
            {
                expectedSequence = sequence;
                pending = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return pending.Task;
            }
        }

        internal bool Acknowledge(ulong sequence)
        {
            lock (gate)
            {
                if (pending is null || sequence < expectedSequence)
                {
                    return false;
                }
                if (sequence > expectedSequence)
                {
                    pending.TrySetException(new DirectProtocolException(
                        $"The hub acknowledged future heartbeat {sequence}; expected {expectedSequence}."));
                    return false;
                }

                return pending.TrySetResult();
            }
        }
    }

    private sealed class HubConnectionAttempt
    {
        private long authenticatedAt;
        private int heartbeatAcknowledged;

        internal void MarkAuthenticated() =>
            authenticatedAt = Stopwatch.GetTimestamp();

        internal void MarkHeartbeatAcknowledged() =>
            Volatile.Write(ref heartbeatAcknowledged, 1);

        internal bool WasStableFor(TimeSpan duration) =>
            Volatile.Read(ref heartbeatAcknowledged) != 0
            && authenticatedAt != 0
            && Stopwatch.GetElapsedTime(authenticatedAt) >= duration;
    }

    private sealed class HubAuthenticationState(string? credential, string? pairingToken)
    {
        internal string? Credential { get; set; } = credential;

        internal string? PairingToken { get; set; } = pairingToken;
    }
}

internal sealed record HubClientTimings(
    TimeSpan ConnectionAttemptTimeout,
    TimeSpan HeartbeatInterval,
    TimeSpan HeartbeatAcknowledgementTimeout,
    TimeSpan SocketOperationTimeout,
    TimeSpan ShutdownTimeout,
    TimeSpan InitialReconnectDelay,
    TimeSpan MaximumReconnectDelay,
    TimeSpan StableConnectionDuration,
    int QueryQueueCapacity)
{
    internal static HubClientTimings Default { get; } = new(
        ConnectionAttemptTimeout: TimeSpan.FromSeconds(15),
        HeartbeatInterval: TimeSpan.FromSeconds(30),
        HeartbeatAcknowledgementTimeout: TimeSpan.FromSeconds(15),
        SocketOperationTimeout: TimeSpan.FromSeconds(15),
        ShutdownTimeout: TimeSpan.FromSeconds(2),
        InitialReconnectDelay: TimeSpan.FromSeconds(1),
        MaximumReconnectDelay: TimeSpan.FromSeconds(60),
        StableConnectionDuration: TimeSpan.FromMinutes(1),
        QueryQueueCapacity: 8);

    internal void Validate()
    {
        var values = new[]
        {
            ConnectionAttemptTimeout,
            HeartbeatInterval,
            HeartbeatAcknowledgementTimeout,
            SocketOperationTimeout,
            ShutdownTimeout,
            InitialReconnectDelay,
            MaximumReconnectDelay,
            StableConnectionDuration,
        };
        if (values.Any(value => value <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HubClientTimings),
                "Hosted connection timings must be greater than zero.");
        }
        if (MaximumReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReconnectDelay),
                "Maximum reconnect delay cannot be shorter than the initial delay.");
        }
        if (QueryQueueCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueryQueueCapacity),
                "Hosted query queue capacity must be at least one.");
        }
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
