using Chatstronomy.NINA.Protocol;

namespace Chatstronomy.NINA.Direct;

internal interface INinaDirectDataProvider : IDisposable
{
    DirectCapabilities Capabilities { get; }

    /// Transport sessions belong to one N.I.N.A. profile. A profile change
    /// cancels its token before the asynchronous connection lifecycle runs.
    CancellationToken ProfileSessionToken { get; }

    void Start();

    void Stop();

    void Reset();

    /// Re-read the delivery options, starting or stopping the N.I.N.A. log
    /// tail so it only runs while at least one level is selected.
    void ApplyLogDeliveryOptions();

    /// Cancel cancellable hardware operations after local control consent is
    /// revoked. Queued commands are independently rejected by the live policy.
    void RevokeRemoteControl();

    /// End every authenticated transport and outstanding command belonging to
    /// the previous profile, even when its successor grants identical access.
    void RevokeProfileAccess();

    Task<object?> ExecuteAsync(DirectQuery query, CancellationToken cancellationToken);
}
