using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Direct;

internal interface INinaDirectDataProvider : IDisposable
{
    DirectCapabilities Capabilities { get; }

    void Start();

    void Stop();

    void Reset();

    Task<object?> ExecuteAsync(DirectQuery query, CancellationToken cancellationToken);
}
