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
    private readonly CancellationTokenSource stopping = new();
    private NamedPipeServerStream? pipe;
    private Task? runTask;

    internal NinaDirectPipeServer(INinaDirectDataProvider provider, string pipeName)
    {
        this.provider = provider;
        PipeName = pipeName;
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
        stopping.Cancel();
        pipe?.Dispose();
        stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

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
                        var payload = await provider.ExecuteAsync(query, cancellationToken)
                            .ConfigureAwait(false);
                        response = DirectProtocol.SerializeSuccess(query.Id, payload);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested)
                    {
                        response = DirectProtocol.SerializeFailure(
                            query.Id,
                            NinaDirectDataProvider.RedactCommandError(exception.Message));
                    }
                }

                await writer.WriteLineAsync(response.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
