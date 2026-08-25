namespace Chatstronomy.NINA.Settings;

/// <summary>
/// Per-profile privacy controls for which native events and image data may
/// leave N.I.N.A. A disabled category never crosses either Direct transport;
/// local history is retained separately and rechecked at every query.
/// </summary>
internal sealed record DirectEventDeliveryOptions(
    bool Images,
    bool Autofocus,
    bool Guiding,
    bool Mount,
    bool Sequence,
    bool TargetScheduler,
    bool FilterFocuserRotator,
    bool EquipmentConnections,
    bool OtherEvents,
    bool NinaNotifications,
    bool NinaLogErrors,
    bool NinaLogWarnings,
    bool NinaLogInformation,
    bool NinaLogDebug,
    bool NinaLogTrace)
{
    internal static DirectEventDeliveryOptions Default { get; } = new(
        Images: true,
        Autofocus: true,
        Guiding: true,
        Mount: true,
        Sequence: true,
        TargetScheduler: true,
        FilterFocuserRotator: true,
        EquipmentConnections: true,
        OtherEvents: true,
        NinaNotifications: true,
        NinaLogErrors: false,
        NinaLogWarnings: false,
        NinaLogInformation: false,
        NinaLogDebug: false,
        NinaLogTrace: false);

    internal bool ShouldSendEvent(string eventName)
    {
        if (eventName.Equals("NINA-NOTIFICATION", StringComparison.Ordinal))
        {
            return NinaNotifications;
        }
        if (eventName.Equals("NINA-LOG", StringComparison.Ordinal))
        {
            return false;
        }
        if (eventName.StartsWith("TS-", StringComparison.Ordinal))
        {
            return TargetScheduler;
        }
        if (eventName.StartsWith("SEQUENCE-", StringComparison.Ordinal))
        {
            return Sequence;
        }
        if (eventName.StartsWith("IMAGE-", StringComparison.Ordinal)
            || eventName.Equals("API-CAPTURE-FINISHED", StringComparison.Ordinal))
        {
            return Images;
        }
        if (eventName.StartsWith("AUTOFOCUS-", StringComparison.Ordinal)
            || eventName.Equals("ERROR-AF", StringComparison.Ordinal)
            || eventName.Equals("FOCUSER-USER-FOCUSED", StringComparison.Ordinal))
        {
            return Autofocus;
        }
        if (eventName.EndsWith("-CONNECTED", StringComparison.Ordinal)
            || eventName.EndsWith("-DISCONNECTED", StringComparison.Ordinal)
            || eventName.Equals("CAMERA-DOWNLOAD-TIMEOUT", StringComparison.Ordinal))
        {
            return EquipmentConnections;
        }
        if (eventName.StartsWith("GUIDER-", StringComparison.Ordinal))
        {
            return Guiding;
        }
        if (eventName.StartsWith("MOUNT-", StringComparison.Ordinal)
            || eventName.Equals("ERROR-PLATESOLVE", StringComparison.Ordinal))
        {
            return Mount;
        }
        if (eventName.StartsWith("FILTERWHEEL-", StringComparison.Ordinal)
            || eventName.StartsWith("ROTATOR-", StringComparison.Ordinal)
            || eventName.StartsWith("FOCUSER-", StringComparison.Ordinal))
        {
            return FilterFocuserRotator;
        }
        return OtherEvents;
    }

    internal bool ShouldSendLogLevel(string level) =>
        level.ToUpperInvariant() switch
        {
            "FATAL" or "ERROR" => NinaLogErrors,
            "WARN" or "WARNING" => NinaLogWarnings,
            "INFO" or "INFORMATION" => NinaLogInformation,
            "DEBUG" => NinaLogDebug,
            "TRACE" or "VERBOSE" => NinaLogTrace,
            // Log forwarding is opt-in because log lines can carry equipment
            // paths and other private details, so an unrecognised level must
            // stay silent. Falling back to OtherEvents (which defaults to
            // true) would forward it with every log checkbox unticked.
            _ => false,
        };

    /// True when at least one log level is selected. Nothing needs to tail the
    /// N.I.N.A. log until one is.
    internal bool AnyLogLevelEnabled =>
        NinaLogErrors || NinaLogWarnings || NinaLogInformation || NinaLogDebug || NinaLogTrace;
}

internal sealed class DirectEventDeliveryPolicy
{
    private DirectEventDeliveryOptions current;

    internal DirectEventDeliveryPolicy(DirectEventDeliveryOptions initial)
    {
        current = initial;
    }

    internal DirectEventDeliveryOptions Current => Volatile.Read(ref current);

    internal void Update(DirectEventDeliveryOptions options) =>
        Volatile.Write(ref current, options);
}
