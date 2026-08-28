using System.IO;
using System.IO.Pipes;
using System.Text;
using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// One-client, current-user-only transport between the N.I.N.A. plugin and
/// the supervised local Rust runtime. Frames are newline-delimited Direct
/// protocol JSON and requests are answered serially.
/// </summary>
internal sealed class NinaDirectPipeServer : IDisposable
{
    private const int MaxFrameCharacters = 1024 * 1024;
    private readonly INinaDirectDataProvider provider;
    private readonly CancellationToken directSessionToken;
    private readonly CancellationTokenSource stopping;
    private readonly CancellationTokenRegistration invalidateOnSessionChange;
    private NamedPipeServerStream? pipe;
    private Task? runTask;
    private int invalidated;
    private int disposed;

    internal NinaDirectPipeServer(
        INinaDirectDataProvider provider,
        string pipeName,
        CancellationToken? directSessionToken = null)
    {
        this.provider = provider;
        PipeName = pipeName;
        var session = directSessionToken ?? provider.DirectSessionToken;
        this.directSessionToken = session;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(session);
        invalidateOnSessionChange = session.Register(
            static state => ((NinaDirectPipeServer)state!).Invalidate(),
            this);
    }

    internal string PipeName { get; }

    internal static string CreatePipeName() => $"chatstronomy-direct-{Guid.NewGuid():N}";

    internal void Start()
    {
        if (runTask is not null)
        {
            throw new InvalidOperationException("The Direct data pipe is already running.");
        }

        runTask = RunAsync(stopping.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Invalidate();
        invalidateOnSessionChange.Dispose();
        stopping.Dispose();
    }

    internal void Invalidate()
    {
        if (Interlocked.Exchange(ref invalidated, 1) != 0)
        {
            return;
        }
        stopping.Cancel();
        Volatile.Read(ref pipe)?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            provider.BeginDirectTransport(directSessionToken);

            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
                if (line.Length > MaxFrameCharacters)
                {
                    throw new DirectProtocolException("Direct query frame exceeds the size limit.");
                }

                var query = DirectProtocol.ParseQuery(line);
                string response;
                var queryCompleted = false;
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
                            NinaDirectDataProvider.RedactCommandError(exception.Message),
                            DirectProtocol.FailureCode(exception));
                    }
                }

                await writer.WriteLineAsync(response.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (queryCompleted)
                {
                    provider.ConfirmDirectQueryResponse(query, directSessionToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
