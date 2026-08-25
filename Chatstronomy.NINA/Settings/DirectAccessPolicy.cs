using Chatstronomy.NINA.Protocol;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chatstronomy.NINA.Settings;

[Flags]
internal enum DirectCommandPermissions : ushort
{
    None = 0,
    UnparkMount = 1 << 0,
    HomeMount = 1 << 1,
    ChangeFilter = 1 << 2,
    StartGuiding = 1 << 3,
    StopGuiding = 1 << 4,
    CoolCamera = 1 << 5,
    WarmCamera = 1 << 6,
    StartAutofocus = 1 << 7,
    CancelAutofocus = 1 << 8,
    ParkMount = 1 << 9,
    AbortExposure = 1 << 10,
    StopSequence = 1 << 11,
    StartSequence = 1 << 12,
}

/// <summary>
/// Consent that must be granted inside the active N.I.N.A. profile before
/// either a hosted connection or a locally supervised bot receives access.
/// </summary>
internal sealed record DirectAccessOptions(
    bool AllowRemoteControl,
    bool ShareObservatoryLocation,
    DirectCommandPermissions AllowedCommands = DirectCommandPermissions.None,
    bool AllowSkipSequenceValidation = false)
{
    internal static DirectAccessOptions Default { get; } = new(
        AllowRemoteControl: false,
        ShareObservatoryLocation: false);

    internal DirectCommandPermissions EffectiveAllowedCommands =>
        AllowRemoteControl ? AllowedCommands : DirectCommandPermissions.None;

    internal bool CommandsEnabled =>
        EffectiveAllowedCommands != DirectCommandPermissions.None;

    internal bool SequenceValidationBypassEnabled =>
        AllowRemoteControl
        && AllowedCommands.HasFlag(DirectCommandPermissions.StartSequence)
        && AllowSkipSequenceValidation;
}

/// <summary>
/// A live, atomically replaced local trust boundary. Transport capabilities
/// are only advisory; every hardware command must consult this policy again.
/// </summary>
internal sealed class DirectAccessPolicy(DirectAccessOptions initial)
{
    private DirectAccessOptions current = initial;

    internal DirectAccessOptions Current => Volatile.Read(ref current);

    internal void Update(DirectAccessOptions options) =>
        Volatile.Write(ref current, options);

    internal void RequireRemoteControl()
    {
        var access = Current;
        if (!access.AllowRemoteControl)
        {
            throw new InvalidOperationException(
                "Remote telescope control is disabled in this N.I.N.A. profile. "
                + "Explicitly enable it in the Chatstronomy plugin's Security and privacy settings.");
        }

        if (!access.CommandsEnabled)
        {
            throw new InvalidOperationException(
                "Remote telescope control has no individually approved commands in this "
                + "N.I.N.A. profile. Enable an individual command in the Chatstronomy plugin.");
        }
    }

    internal void RequireRemoteControl(DirectRigCommand command)
    {
        var access = Current;
        if (!access.AllowRemoteControl)
        {
            throw new InvalidOperationException(
                "Remote telescope control is disabled in this N.I.N.A. profile. "
                + "Explicitly enable it in the Chatstronomy plugin's Security and privacy settings.");
        }

        var permission = PermissionFor(command.Kind);
        if (!access.AllowedCommands.HasFlag(permission))
        {
            throw new InvalidOperationException(
                $"Remote control for {DisplayName(command.Kind)} is disabled in this "
                + "N.I.N.A. profile. Enable its individual permission in the Chatstronomy plugin.");
        }

        if (command.Kind == DirectRigCommandKind.StartSequence
            && command.SkipValidation == true
            && !access.SequenceValidationBypassEnabled)
        {
            throw new InvalidOperationException(
                "Skipping sequence safety validation is disabled in this N.I.N.A. profile. "
                + "Explicitly allow validation bypass in the Chatstronomy plugin.");
        }
    }

    internal static DirectCommandPermissions PermissionFor(DirectRigCommandKind command) =>
        command switch
        {
            DirectRigCommandKind.UnparkMount => DirectCommandPermissions.UnparkMount,
            DirectRigCommandKind.HomeMount => DirectCommandPermissions.HomeMount,
            DirectRigCommandKind.ChangeFilter => DirectCommandPermissions.ChangeFilter,
            DirectRigCommandKind.StartGuiding => DirectCommandPermissions.StartGuiding,
            DirectRigCommandKind.StopGuiding => DirectCommandPermissions.StopGuiding,
            DirectRigCommandKind.CoolCamera => DirectCommandPermissions.CoolCamera,
            DirectRigCommandKind.WarmCamera => DirectCommandPermissions.WarmCamera,
            DirectRigCommandKind.StartAutofocus => DirectCommandPermissions.StartAutofocus,
            DirectRigCommandKind.CancelAutofocus => DirectCommandPermissions.CancelAutofocus,
            DirectRigCommandKind.ParkMount => DirectCommandPermissions.ParkMount,
            DirectRigCommandKind.AbortExposure => DirectCommandPermissions.AbortExposure,
            DirectRigCommandKind.StopSequence => DirectCommandPermissions.StopSequence,
            DirectRigCommandKind.StartSequence => DirectCommandPermissions.StartSequence,
            _ => throw new NotSupportedException(
                $"Direct command '{command}' has no local permission."),
        };

    private static string DisplayName(DirectRigCommandKind command) => command switch
    {
        DirectRigCommandKind.UnparkMount => "unparking the mount",
        DirectRigCommandKind.HomeMount => "homing the mount",
        DirectRigCommandKind.ChangeFilter => "changing the filter",
        DirectRigCommandKind.StartGuiding => "starting guiding",
        DirectRigCommandKind.StopGuiding => "stopping guiding",
        DirectRigCommandKind.CoolCamera => "cooling the camera",
        DirectRigCommandKind.WarmCamera => "warming the camera",
        DirectRigCommandKind.StartAutofocus => "starting autofocus",
        DirectRigCommandKind.CancelAutofocus => "cancelling autofocus",
        DirectRigCommandKind.ParkMount => "parking the mount",
        DirectRigCommandKind.AbortExposure => "aborting an exposure",
        DirectRigCommandKind.StopSequence => "stopping the sequence",
        DirectRigCommandKind.StartSequence => "starting the sequence",
        _ => command.ToString(),
    };
}

/// <summary>
/// Removes sensitive values from projected dictionaries before they can be
/// serialized into either Direct transport.
/// </summary>
internal static class DirectPrivacyProjection
{
    private static readonly HashSet<string> AlwaysPrivateFields = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "DeviceId",
        "DriverInfo",
        "DriverVersion",
        "FilePath",
        "FullPath",
        "LocalPath",
        "OutputPath",
        "Directory",
        "OutputDirectory",
        "Script",
        "CommandLine",
        "Arguments",
    };

    private static readonly HashSet<string> ObservatoryLocationFields = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "SiteLatitude",
        "SiteLongitude",
        "SiteElevation",
        "Latitude",
        "Longitude",
        "Elevation",
        "SiderealTime",
        "SiderealTimeString",
        "LocalSiderealTime",
        "Altitude",
        "AltitudeString",
        "CurrentAltitude",
        "HorizonAltitude",
        "Azimuth",
        "AzimuthString",
        "AzimuthDegrees",
        "TimeToMeridianFlip",
        "TimeToMeridianFlipString",
        "HoursToMeridianString",
        "TimeToFlip",
        "ExpectedTime",
        "ExpectedDateTime",
    };

    internal static void Redact(
        IDictionary<string, object?> snapshot,
        DirectAccessOptions access)
    {
        foreach (var field in snapshot.Keys.ToArray())
        {
            if (AlwaysPrivateFields.Contains(field)
                || !access.ShareObservatoryLocation
                    && ObservatoryLocationFields.Contains(field))
            {
                snapshot.Remove(field);
                continue;
            }

            RedactChildren(snapshot[field], access);
        }
    }

    /// Clone before projection: bounded event history must retain its original
    /// coordinates if the user later explicitly re-enables location sharing.
    internal static Dictionary<string, object?> RedactedCopy(
        IReadOnlyDictionary<string, object?> snapshot,
        DirectAccessOptions access)
    {
        var copy = snapshot.ToDictionary(
            field => field.Key,
            field => CloneValue(field.Value, access),
            StringComparer.Ordinal);
        Redact(copy, access);
        return copy;
    }

    private static object? CloneValue(object? value, DirectAccessOptions access)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            return RedactedCopy(readOnly, access);
        }
        if (value is IDictionary<string, object?> dictionary)
        {
            return RedactedCopy(
                dictionary.ToDictionary(field => field.Key, field => field.Value),
                access);
        }
        if (value is JsonElement json)
        {
            return Redact(json, access);
        }
        if (value is JsonNode node)
        {
            var copy = node.DeepClone();
            RedactJson(copy, access);
            return copy;
        }
        if (value is not string && value is IEnumerable children)
        {
            return children.Cast<object?>()
                .Select(child => CloneValue(child, access))
                .ToArray();
        }
        return value;
    }

    internal static JsonElement Redact(JsonElement snapshot, DirectAccessOptions access)
    {
        var node = JsonNode.Parse(snapshot.GetRawText());
        RedactJson(node, access);
        return JsonSerializer.SerializeToElement(node);
    }

    /// Older bundled runtimes require every historical mount field, so omit
    /// nothing there: replace private values with harmless sentinels instead.
    /// The additive flag lets newer consumers suppress redacted display rows.
    internal static void RedactMount(
        IDictionary<string, object?> snapshot,
        DirectAccessOptions access)
    {
        snapshot["DeviceId"] = string.Empty;
        snapshot["LocationRedacted"] = !access.ShareObservatoryLocation;
        if (access.ShareObservatoryLocation)
        {
            return;
        }

        foreach (var field in snapshot.Keys.ToArray())
        {
            if (!ObservatoryLocationFields.Contains(field))
            {
                continue;
            }

            snapshot[field] = snapshot[field] switch
            {
                string => string.Empty,
                int => 0,
                long => 0L,
                float => 0f,
                decimal => 0m,
                _ => 0d,
            };
        }
    }

    private static void RedactChildren(object? value, DirectAccessOptions access)
    {
        if (value is IDictionary<string, object?> nested)
        {
            Redact(nested, access);
            return;
        }

        if (value is string || value is not IEnumerable children)
        {
            return;
        }

        foreach (var child in children)
        {
            RedactChildren(child, access);
        }
    }

    private static void RedactJson(JsonNode? node, DirectAccessOptions access)
    {
        if (node is JsonObject properties)
        {
            foreach (var property in properties.ToArray())
            {
                if (AlwaysPrivateFields.Contains(property.Key)
                    || !access.ShareObservatoryLocation
                        && ObservatoryLocationFields.Contains(property.Key))
                {
                    properties.Remove(property.Key);
                }
                else
                {
                    RedactJson(property.Value, access);
                }
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                RedactJson(item, access);
            }
        }
    }
}
