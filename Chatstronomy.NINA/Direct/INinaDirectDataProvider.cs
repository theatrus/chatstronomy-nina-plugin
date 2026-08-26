using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;

namespace Chatstronomy.NINA.Direct;

internal interface INinaDirectDataProvider : IDisposable
{
    DirectCapabilities Capabilities { get; }

    /// Autofocus capture and accepted hardware work belong to one N.I.N.A.
    /// profile. Only a profile change cancels this trust token.
    CancellationToken ProfileSessionToken { get; }

    /// Hosted sockets and local Direct pipes belong to one transport session.
    /// Privacy-policy changes can rotate this token without disturbing
    /// accepted hardware work or autofocus capture for the current profile.
    CancellationToken DirectSessionToken { get; }

    /// Logical identity for the current Direct data/privacy session. Ordinary
    /// socket reconnects keep it; privacy-policy and profile changes rotate it.
    Guid DirectSessionId { get; }

    void Start();

    void Stop();

    void Reset();

    /// Re-read the delivery options, starting or stopping the N.I.N.A. log
    /// tail so it only runs while at least one level is selected.
    void ApplyLogDeliveryOptions();

    /// Cancel cancellable hardware operations after local control consent is
    /// revoked. Queued commands are independently rejected by the live policy.
    void RevokeRemoteControl();

    /// Close every authenticated Direct transport while preserving the
    /// current profile's accepted commands and autofocus capture.
    void RotateDirectSession();

    /// Mark the start of one authenticated physical transport inside the
    /// current logical Direct session. Implementations can rewind delivery
    /// cursors so a restarted consumer receives the last unacknowledged delta.
    void BeginDirectTransport(CancellationToken directSessionToken)
    {
    }

    /// Pause callback history while a delivery policy is being published.
    void SuspendEventCapture()
    {
    }

    /// Resume callback history after the matching policy publication.
    void ResumeEventCapture()
    {
    }

    /// Advance only the capture provenance owned by categories whose policy
    /// is changing. This runs before the new policy is published so callbacks
    /// from the preceding consent state fail closed without invalidating
    /// unrelated safety, autofocus, image, or guider work.
    void EventDeliveryPolicyChanging(
        DirectEventDeliveryOptions previous,
        DirectEventDeliveryOptions current)
    {
    }

    /// Apply provider-owned provenance changes after a new delivery policy is
    /// published but before callback capture resumes.
    void EventDeliveryPolicyChanged(
        DirectEventDeliveryOptions previous,
        DirectEventDeliveryOptions current)
    {
    }

    /// End every authenticated transport and outstanding command belonging to
    /// the previous profile, even when its successor grants identical access.
    void RevokeProfileAccess();

    Task<object?> ExecuteAsync(
        DirectQuery query,
        CancellationToken cancellationToken,
        CancellationToken? directSessionToken = null);

    /// Record that a history response reached its Direct transport. History
    /// cursors stay inside the plugin and are never added to the wire payload.
    void ConfirmDirectQueryResponse(
        DirectQuery query,
        CancellationToken directSessionToken);
}
