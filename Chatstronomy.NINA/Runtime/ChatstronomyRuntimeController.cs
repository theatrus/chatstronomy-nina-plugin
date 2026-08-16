using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Direct;

namespace Chatstronomy.NINA.Runtime;

/// <summary>
/// Owns a Chatstronomy child process started by this plugin. Bootstrap data is
/// sent through a random current-user-only named pipe, so delivery credentials
/// never appear in the command line or a temporary configuration file.
/// </summary>
internal sealed class ChatstronomyRuntimeController : IChatstronomyRuntimeController
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private const string ShutdownMessage = "{\"type\":\"shutdown\"}";

    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly INinaDirectDataProvider? directDataProvider;
    private Process? ownedProcess;
    private NamedPipeServerStream? controlPipe;
    private StreamWriter? controlWriter;
    private NinaDirectPipeServer? directPipeServer;
    private string statusMessage = "Local runtime is stopped.";

    internal ChatstronomyRuntimeController(INinaDirectDataProvider? directDataProvider = null)
    {
        this.directDataProvider = directDataProvider;
    }

    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (stateGate)
            {
                return ownedProcess is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (stateGate)
            {
                return ownedProcess is { HasExited: false } process
                    ? process.Id
                    : null;
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (stateGate)
            {
                return statusMessage;
            }
        }
    }

    public async Task StartAsync(
        ChatstronomyConfiguration configuration,
        LocalRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var runtime = configuration.LocalRuntime
                ?? throw new InvalidOperationException("Local runtime configuration is missing.");
            if (!File.Exists(runtime.ExecutablePath))
            {
                throw new InvalidOperationException(
                    "The configured Chatstronomy runtime executable was not found.");
            }

            NinaDirectPipeServer? pendingDirectPipe = null;
            if (runtime.Source is NinaDirectSourceConfiguration)
            {
                var provider = directDataProvider
                    ?? throw new InvalidOperationException(
                        "The native N.I.N.A. Direct provider is unavailable.");
                pendingDirectPipe = new NinaDirectPipeServer(
                    provider,
                    NinaDirectPipeServer.CreatePipeName());
                pendingDirectPipe.Start();
            }

            var payload = PluginRuntimeBootstrap.Serialize(
                configuration,
                identity,
                pendingDirectPipe?.PipeName,
                pendingDirectPipe is null ? null : directDataProvider?.Capabilities);
            var pipeName = $"chatstronomy-bootstrap-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process? process = null;

            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Chatstronomy",
                    "logs");
                Directory.CreateDirectory(logDirectory);
                var logPath = Path.Combine(
                    logDirectory,
                    $"nina-runtime-{identity.ProfileId:N}.log");

                var startInfo = new ProcessStartInfo(runtime.ExecutablePath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(runtime.ExecutablePath)
                        ?? AppContext.BaseDirectory,
                };
                startInfo.ArgumentList.Add("plugin-runtime");
                startInfo.ArgumentList.Add("--bootstrap-pipe");
                startInfo.ArgumentList.Add(pipeName);
                startInfo.ArgumentList.Add("--log-file");
                startInfo.ArgumentList.Add(logPath);

                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "Windows did not start the Chatstronomy runtime process.");

                using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                startupTimeout.CancelAfter(StartupTimeout);
                var startupToken = startupTimeout.Token;
                var connectionTask = pipe.WaitForConnectionAsync(startupToken);
                var exitTask = process.WaitForExitAsync(startupToken);
                var completed = await Task.WhenAny(connectionTask, exitTask).ConfigureAwait(false);
                if (completed == exitTask)
                {
                    await exitTask.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Chatstronomy exited before accepting configuration (exit code {process.ExitCode}).");
                }

                await connectionTask.ConfigureAwait(false);
                var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                await writer.WriteLineAsync(payload.AsMemory(), startupToken)
                    .ConfigureAwait(false);
                var response = await reader.ReadLineAsync(startupToken).ConfigureAwait(false);
                ValidateReadyResponse(response);

                lock (stateGate)
                {
                    ownedProcess = process;
                    controlPipe = pipe;
                    controlWriter = writer;
                    directPipeServer = pendingDirectPipe;
                    statusMessage = $"Local runtime is running (process {process.Id}).";
                }
                process.Exited += OwnedProcessExited;
                process.EnableRaisingEvents = true;
                process = null;
                pipe = null!;
                pendingDirectPipe = null;
                RaiseStateChanged();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Chatstronomy did not accept its local configuration within {StartupTimeout.TotalSeconds:0} seconds.");
            }
            catch
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                process?.Dispose();
                pipe.Dispose();
                pendingDirectPipe?.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Local runtime failed to start: {exception.Message}");
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process;
            StreamWriter? writer;
            lock (stateGate)
            {
                process = ownedProcess;
                writer = controlWriter;
            }

            if (process is null)
            {
                SetStatus("Local runtime is stopped.");
                return;
            }

            if (!process.HasExited && writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(ShutdownMessage.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or ObjectDisposedException)
                {
                    // The process may already be exiting; the bounded wait below
                    // handles both graceful and forced shutdown paths.
                }
            }

            if (!process.HasExited)
            {
                using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                shutdownTimeout.CancelAfter(ShutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(shutdownTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            ClearOwnedProcess(process);
            SetStatus("Local runtime stopped.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process;
            lock (stateGate)
            {
                process = ownedProcess;
            }

            if (process is null)
            {
                return;
            }

            ClearOwnedProcess(process);
            SetStatus("Local runtime was left running independently of N.I.N.A.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static void ValidateReadyResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException(
                "Chatstronomy closed the bootstrap pipe before confirming startup.");
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "ready"
                || root.GetProperty("protocol_version").GetUInt16()
                    != PluginRuntimeBootstrap.ProtocolVersion)
            {
                throw new InvalidOperationException(
                    "Chatstronomy returned an incompatible bootstrap response.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Chatstronomy returned an invalid bootstrap response.",
                exception);
        }
    }

    private async void OwnedProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (!ReferenceEquals(ownedProcess, process))
                {
                    return;
                }
            }

            var exitCode = process.ExitCode;
            ClearOwnedProcess(process);
            SetStatus($"Local runtime exited with code {exitCode}.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void ClearOwnedProcess(Process process)
    {
        NamedPipeServerStream? pipe = null;
        StreamWriter? writer = null;
        NinaDirectPipeServer? directServer = null;
        var shouldDispose = false;
        lock (stateGate)
        {
            if (ReferenceEquals(ownedProcess, process))
            {
                process.Exited -= OwnedProcessExited;
                ownedProcess = null;
                pipe = controlPipe;
                writer = controlWriter;
                controlPipe = null;
                controlWriter = null;
                directServer = directPipeServer;
                directPipeServer = null;
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            writer?.Dispose();
            pipe?.Dispose();
            directServer?.Dispose();
            process.Dispose();
        }
    }

    private void SetStatus(string status)
    {
        lock (stateGate)
        {
            statusMessage = status;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
