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
/// the N.I.N.A. process, exposing every emitted level selected by the user.
/// </summary>
internal sealed class NinaLogWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// How long <see cref="Stop"/> waits for the tail to unwind before giving
    /// up. The loop only ever blocks on a file read or the poll delay, so this
    /// is generous.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    /// A single log line is normally well under a kilobyte. A co-installed
    /// plugin logging a serialized response can emit far more, and that text is
    /// held in the event ring and re-sent on every poll, so cap it.
    private const int MaxMessageChars = 2_000;
    /// Ceiling for a partial line carried between polls. Without it, one
    /// newline-free megabyte accumulates across every read.
    private const int MaxPendingChars = 64 * 1024;

    private readonly Action<NinaLogRecord, long> onRecord;
    /// Start and Stop are called from plugin lifecycle code and must not
    /// interleave: a Stop that returned before its tail unwound used to let a
    /// following Start run a second tail over the same file.
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? watcherTask;
    private bool desiredRunning;
    private long desiredGeneration;

    internal NinaLogWatcher(Action<NinaLogRecord, long> onRecord)
    {
        this.onRecord = onRecord;
    }

    internal void Start(long captureGeneration)
    {
        lock (gate)
        {
            desiredRunning = true;
            desiredGeneration = captureGeneration;
            if (watcherTask is not null)
            {
                return;
            }
            StartCore(captureGeneration);
        }
    }

    internal void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (gate)
        {
            desiredRunning = false;
            cancellation = stop;
            running = watcherTask;
            if (cancellation is null)
            {
                return;
            }
            cancellation.Cancel();
        }
        try
        {
            // A timed-out tail remains registered as the sole active worker;
            // Start() records the successor generation and its continuation
            // starts that tail only after this one has actually unwound.
            running?.Wait(StopTimeout);
        }
        catch (AggregateException)
        {
            // WatchAsync handles its own failures; nothing to add here.
        }
    }

    private void StartCore(long captureGeneration)
    {
        var cancellation = new CancellationTokenSource();
        stop = cancellation;
        var running = Task.Run(() => WatchAsync(
            cancellation.Token,
            captureGeneration));
        watcherTask = running;
        _ = running.ContinueWith(
            _ => WatcherEnded(running, cancellation),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void WatcherEnded(
        Task completed,
        CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (!ReferenceEquals(watcherTask, completed))
            {
                cancellation.Dispose();
                return;
            }
            watcherTask = null;
            stop = null;
            cancellation.Dispose();
            if (desiredRunning)
            {
                StartCore(desiredGeneration);
            }
        }
    }

    public void Dispose() => Stop();

    private async Task WatchAsync(
        CancellationToken cancellationToken,
        long captureGeneration)
    {
        string? activePath = null;
        long position = 0;
        var pending = string.Empty;
        var firstLogFile = true;
        // A read can land mid-character, so decoding must carry state across
        // polls; a fresh GetString per chunk turns one split character into two
        // replacement characters.
        var decoder = Encoding.UTF8.GetDecoder();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var latestPath = FindActiveLogFile();
                if (!string.Equals(activePath, latestPath, StringComparison.OrdinalIgnoreCase))
                {
                    activePath = latestPath;
                    decoder.Reset();
                    // Existing lines predate plugin startup and belong to the
                    // updater baseline. Start at EOF to avoid racing a replay
                    // of the whole N.I.N.A. session into chat. A newly rotated
                    // file starts at zero so no live records are missed.
                    position = firstLogFile && latestPath is not null
                        ? new FileInfo(latestPath).Length
                        : 0;
                    firstLogFile &= latestPath is null;
                    pending = string.Empty;
                }

                if (activePath is not null)
                {
                    var read = await ReadNewTextAsync(
                            activePath,
                            position,
                            decoder,
                            cancellationToken)
                        .ConfigureAwait(false);
                    position = read.Position;
                    if (read.Reset)
                    {
                        pending = string.Empty;
                        decoder.Reset();
                    }
                    if (read.Text.Length > 0)
                    {
                        pending += read.Text;
                        pending = ProcessCompleteLines(
                            pending,
                            captureGeneration,
                            cancellationToken);
                        if (pending.Length > MaxPendingChars)
                        {
                            // A single line this long is not a log record we
                            // can use. Drop it rather than keep concatenating
                            // it on every poll.
                            pending = string.Empty;
                        }
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

    private string ProcessCompleteLines(
        string text,
        long captureGeneration,
        CancellationToken cancellationToken)
    {
        var start = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    onRecord(record, captureGeneration);
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
        Decoder decoder,
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
        if (reset)
        {
            decoder.Reset();
        }
        stream.Position = reset ? 0 : position;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var byteCount = checked((int)buffer.Length);
        var chars = new char[decoder.GetCharCount(buffer.GetBuffer(), 0, byteCount, flush: false)];
        var charCount = decoder.GetChars(buffer.GetBuffer(), 0, byteCount, chars, 0, flush: false);
        return new LogReadResult(stream.Position, new string(chars, 0, charCount), reset);
    }

    private static string? FindActiveLogFile()
    {
        var directory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
        if (!Directory.Exists(directory))
        {
            return null;
        }
        var processMarker = $".{Environment.ProcessId}-";
        // Order by name, not by last-write time. Windows recycles process IDs,
        // so a closed log from an earlier session can carry the same marker,
        // and NTFS defers the directory timestamp of a file with an open
        // write handle — which let a stale file out-rank the live one and
        // replay an entire old session into chat. The leading
        // yyyyMMdd-HHmmss in the file name sorts chronologically.
        return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Contains(
                processMarker,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
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
        var message = parts[5].Trim();
        if (message.Length > MaxMessageChars)
        {
            message = string.Concat(message.AsSpan(0, MaxMessageChars - 1), "…");
        }
        record = new NinaLogRecord(
            time,
            NormalizeLevel(parts[1]),
            parts[2].Trim(),
            parts[3].Trim(),
            sourceLine,
            message);
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
