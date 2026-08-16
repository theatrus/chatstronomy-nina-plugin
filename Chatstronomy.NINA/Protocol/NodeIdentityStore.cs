using System.IO;

namespace Chatstronomy.NINA.Protocol;

/// <summary>
/// Persists a non-secret node ID shared by every N.I.N.A. instance running
/// under the same Windows user. Authentication credentials are separate and
/// will be stored with Windows data protection when pairing is implemented.
/// </summary>
internal static class NodeIdentityStore
{
    private const string FileName = "node-id";

    internal static Guid LoadOrCreate()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chatstronomy");
        var path = Path.Combine(directory, FileName);
        Directory.CreateDirectory(directory);

        // Multiple N.I.N.A. processes may load the plugin at the same time.
        // CreateNew gives one process ownership; the others retry the read.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (TryRead(path, out var existing))
            {
                return existing;
            }

            var created = Guid.NewGuid();
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream);
                writer.Write(created.ToString("D"));
                writer.Flush();
                stream.Flush(flushToDisk: true);
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(25);
            }
        }

        throw new IOException($"Could not read or create the Chatstronomy node ID at {path}");
    }

    private static bool TryRead(string path, out Guid nodeId)
    {
        nodeId = Guid.Empty;
        try
        {
            return File.Exists(path)
                && Guid.TryParse(File.ReadAllText(path).Trim(), out nodeId)
                && nodeId != Guid.Empty;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
