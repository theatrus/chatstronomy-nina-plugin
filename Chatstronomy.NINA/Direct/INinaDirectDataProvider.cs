using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Direct;

internal interface INinaDirectDataProvider : IDisposable
{
    DirectCapabilities Capabilities { get; }

    void Start();

    void Stop();

    void Reset();

    /// Re-read the delivery options, starting or stopping the N.I.N.A. log
    /// tail so it only runs while at least one level is selected.
    void ApplyLogDeliveryOptions();

    Task<object?> ExecuteAsync(DirectQuery query, CancellationToken cancellationToken);
}
