using System.Globalization;
using System.IO;
using System.Text;
using NINA.Core.Utility;

namespace Chatstronomy.NINA.Direct;

internal sealed record NinaLogRecord(
    DateTime Time,
    string Level,
    string Source,
    string Member,
    int Line,
    string Message);

/// <summary>
/// Tails the current N.I.N.A. process log without replacing or reconfiguring
/// N.I.N.A.'s global Serilog pipeline. This follows the same file boundary as
/// Advanced API, but exposes every emitted level selected by the user.
/// </summary>
internal sealed class NinaLogWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly Action<NinaLogRecord> onRecord;
    private CancellationTokenSource? stop;
    private Task? watcherTask;

    internal NinaLogWatcher(Action<NinaLogRecord> onRecord)
    {
        this.onRecord = onRecord;
    }

    internal void Start()
    {
        if (stop is not null)
        {
            return;
        }
        stop = new CancellationTokenSource();
        watcherTask = Task.Run(() => WatchAsync(stop.Token));
    }

    internal void Stop()
    {
        var cancellation = Interlocked.Exchange(ref stop, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        watcherTask = null;
    }

    public void Dispose() => Stop();

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        string? activePath = null;
        long position = 0;
        var pending = string.Empty;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var latestPath = FindActiveLogFile();
                if (!string.Equals(activePath, latestPath, StringComparison.OrdinalIgnoreCase))
                {
                    activePath = latestPath;
                    position = 0;
                    pending = string.Empty;
                }

                if (activePath is not null)
                {
                    var read = await ReadNewTextAsync(activePath, position, cancellationToken)
                        .ConfigureAwait(false);
                    position = read.Position;
                    if (read.Reset)
                    {
                        pending = string.Empty;
                    }
                    if (read.Text.Length > 0)
                    {
                        pending += read.Text;
                        pending = ProcessCompleteLines(pending);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // N.I.N.A. can rotate the log between discovery and read.
            }
            catch (UnauthorizedAccessException)
            {
                // Retry; the logger may briefly hold the file during rotation.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private string ProcessCompleteLines(string text)
    {
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                return text[start..];
            }
            var line = text[start..newline].TrimEnd('\r');
            start = newline + 1;
            if (TryParseLine(line, out var record))
            {
                try
                {
                    onRecord(record);
                }
                catch
                {
                    // A consumer failure must never stop N.I.N.A. log capture.
                }
            }
        }
    }

    private static async Task<LogReadResult> ReadNewTextAsync(
        string path,
        long position,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var reset = position > stream.Length;
        stream.Position = reset ? 0 : position;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new LogReadResult(
            stream.Position,
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)),
            reset);
    }

    private static string? FindActiveLogFile()
    {
        var directory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
        if (!Directory.Exists(directory))
        {
            return null;
        }
        var processMarker = $".{Environment.ProcessId}-";
        return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains(
                processMarker,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    internal static bool TryParseLine(string line, out NinaLogRecord record)
    {
        record = null!;
        var parts = line.Split(new[] { '|' }, 6, StringSplitOptions.None);
        if (parts.Length != 6
            || !DateTime.TryParse(
                parts[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var time)
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[5]))
        {
            return false;
        }

        _ = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceLine);
        record = new NinaLogRecord(
            time,
            NormalizeLevel(parts[1]),
            parts[2].Trim(),
            parts[3].Trim(),
            sourceLine,
            parts[5].Trim());
        return true;
    }

    private static string NormalizeLevel(string level) =>
        level.Trim().ToUpperInvariant() switch
        {
            "VERBOSE" => "TRACE",
            "INFORMATION" => "INFO",
            "WARN" => "WARNING",
            var normalized => normalized,
        };

    private sealed record LogReadResult(long Position, string Text, bool Reset);
}
