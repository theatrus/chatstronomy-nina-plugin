using Chatstronomy.NINA.Configuration;

namespace Chatstronomy.NINA.Runtime;

internal sealed record LocalRuntimeIdentity(
    Guid NodeId,
    Guid ProfileId,
    string ProfileName);

/// <summary>
/// Boundary for the bundled/local runtime implementation. Configuration can
/// be completed now without coupling the options UI to process management or
/// placing delivery secrets on a command line or in a temporary JSON file.
/// </summary>
internal interface IChatstronomyRuntimeController
{
    event EventHandler? StateChanged;

    bool IsRunning { get; }

    int? ProcessId { get; }

    string StatusMessage { get; }

    Task StartAsync(
        ChatstronomyConfiguration configuration,
        LocalRuntimeIdentity identity,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
