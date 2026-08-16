namespace Chatstronomy.NINA.Settings;

/// <summary>
/// Per-profile controls for which native Direct events produce chat messages.
/// Events still cross the Direct boundary for state reconstruction; the
/// <c>ChatEnabled</c> wire flag only suppresses their user-facing delivery.
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
            _ => OtherEvents,
        };
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
