using System.Text.Json;
using System.Text.Json.Serialization;
using Chatstronomy.NINA.Security;

namespace Chatstronomy.NINA.Protocol;

internal static class DirectProtocol
{
    internal const ushort CurrentVersion = 1;
    internal const ushort LegacyPayloadVersion = 1;
    internal const ushort CurrentPayloadVersion = 3;
    internal const long ExpiryClockSkewGraceSeconds = 120;
    internal const long CommandExpiryClockSkewGraceSeconds = 5;
    private const string PipePrefix = "chatstronomy-agent-v1";
    internal const string WebSocketPath = "/v1/direct";

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    static DirectProtocol()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    internal static string LocalPipeName(Guid nodeId) => $"{PipePrefix}-{nodeId:N}";

    internal static DirectQuery ParseQuery(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (RequiredString(root, "type") != "query")
        {
            throw new DirectProtocolException("Expected a Direct query message.");
        }

        var payload = RequiredObject(root, "payload");
        var id = RequiredGuid(payload, "id");
        var expiresAt = OptionalInt64(payload, "expires_at");
        var wireKind = RequiredString(payload, "kind");
        return wireKind switch
        {
            "event_history" => new DirectQuery(id, DirectQueryKind.EventHistory, ExpiresAt: expiresAt),
            "image_history" => new DirectQuery(id, DirectQueryKind.ImageHistory, ExpiresAt: expiresAt),
            "sequence" => new DirectQuery(id, DirectQueryKind.Sequence, ExpiresAt: expiresAt),
            "thumbnail" => new DirectQuery(
                id,
                DirectQueryKind.Thumbnail,
                Index: RequiredUInt32(payload, "index"),
                ExpiresAt: expiresAt),
            "last_autofocus" => new DirectQuery(id, DirectQueryKind.LastAutofocus, ExpiresAt: expiresAt),
            "mount_info" => new DirectQuery(id, DirectQueryKind.MountInfo, ExpiresAt: expiresAt),
            "camera_info" => new DirectQuery(id, DirectQueryKind.CameraInfo, ExpiresAt: expiresAt),
            "filterwheel_info" => new DirectQuery(id, DirectQueryKind.FilterwheelInfo, ExpiresAt: expiresAt),
            "guider_info" => new DirectQuery(id, DirectQueryKind.GuiderInfo, ExpiresAt: expiresAt),
            "guider_graph" => new DirectQuery(id, DirectQueryKind.GuiderGraph, ExpiresAt: expiresAt),
            "rotator_info" => new DirectQuery(id, DirectQueryKind.RotatorInfo, ExpiresAt: expiresAt),
            "focuser_info" => new DirectQuery(id, DirectQueryKind.FocuserInfo, ExpiresAt: expiresAt),
            "command" => new DirectQuery(
                id,
                DirectQueryKind.Command,
                Command: ParseCommand(RequiredObject(payload, "command")),
                ExpiresAt: expiresAt),
            _ => throw new DirectProtocolException($"Unsupported Direct query kind '{wireKind}'."),
        };
    }

    internal static string SerializeSuccess(Guid id, object? payload) =>
        JsonSerializer.Serialize(
            new DirectWireMessage<QueryResultPayload>(
                "query_result",
                new QueryResultPayload(
                    id,
                    Ok: true,
                    JsonSerializer.SerializeToElement(payload, JsonOptions),
                    Error: null,
                    ErrorCode: null)),
            JsonOptions);

    internal static string SerializeFailure(
        Guid id,
        string error,
        string? errorCode = null) =>
        JsonSerializer.Serialize(
            new DirectWireMessage<QueryResultPayload>(
                "query_result",
                new QueryResultPayload(
                    id,
                    Ok: false,
                    JsonSerializer.SerializeToElement<object?>(null, JsonOptions),
                    string.IsNullOrWhiteSpace(error) ? "Direct query failed." : error,
                    errorCode)),
            JsonOptions);

    internal static string? FailureCode(Exception exception) =>
        exception is DirectQueryFailureException failure ? failure.Code : null;

    internal static string SerializePair(string pairingToken, ClientHello hello) =>
        JsonSerializer.Serialize(
            new DirectWireMessage<PairRequestPayload>(
                "pair",
                new PairRequestPayload(pairingToken, hello)),
            JsonOptions);

    internal static string SerializeAuth(string credential, ClientHello hello) =>
        JsonSerializer.Serialize(
            new DirectWireMessage<AuthRequestPayload>(
                "auth",
                new AuthRequestPayload(credential, hello)),
            JsonOptions);

    internal static string SerializeHeartbeat(ulong sequence) =>
        JsonSerializer.Serialize(
            new DirectWireMessage<HeartbeatPayload>(
                "heartbeat",
                new HeartbeatPayload(sequence)),
            JsonOptions);

    internal static HubMessage ParseHubMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = RequiredString(root, "type");
        var payload = RequiredObject(root, "payload");
        return type switch
        {
            "agent_hello" => new HubAgentHelloMessage(ParseAgentHello(payload)),
            "pair_result" => new HubPairResultMessage(
                RequiredString(payload, "credential"),
                ParseAgentHello(RequiredObject(payload, "agent_hello"))),
            "query" => new HubQueryMessage(ParseQuery(json)),
            "heartbeat_ack" => new HubHeartbeatAckMessage(RequiredUInt64(payload, "seq")),
            "error" => new HubErrorMessage(
                RequiredString(payload, "message"),
                OptionalBoolean(payload, "retryable") ?? false),
            _ => new HubUnknownMessage(type),
        };
    }

    private static AgentHello ParseAgentHello(JsonElement payload)
    {
        var rigId = RequiredObject(payload, "rig_id");
        return new AgentHello(
            RequiredUInt16(payload, "protocol_version"),
            OptionalUInt16(payload, "payload_version") ?? LegacyPayloadVersion,
            RequiredGuid(payload, "connection_id"),
            RequiredGuid(rigId, "node_id"),
            RequiredGuid(rigId, "profile_id"));
    }

    private static DirectRigCommand ParseCommand(JsonElement command)
    {
        var wireKind = RequiredString(command, "kind");
        return wireKind switch
        {
            "unpark_mount" => new DirectRigCommand(DirectRigCommandKind.UnparkMount),
            "home_mount" => new DirectRigCommand(DirectRigCommandKind.HomeMount),
            "change_filter" => new DirectRigCommand(
                DirectRigCommandKind.ChangeFilter,
                FilterId: RequiredInt32(command, "filter_id")),
            "start_guiding" => new DirectRigCommand(
                DirectRigCommandKind.StartGuiding,
                Calibrate: RequiredBoolean(command, "calibrate")),
            "stop_guiding" => new DirectRigCommand(DirectRigCommandKind.StopGuiding),
            "cool_camera" => new DirectRigCommand(
                DirectRigCommandKind.CoolCamera,
                Temperature: RequiredDouble(command, "temperature"),
                Minutes: RequiredDouble(command, "minutes")),
            "warm_camera" => new DirectRigCommand(
                DirectRigCommandKind.WarmCamera,
                Minutes: RequiredDouble(command, "minutes")),
            "start_autofocus" => new DirectRigCommand(DirectRigCommandKind.StartAutofocus),
            "cancel_autofocus" => new DirectRigCommand(DirectRigCommandKind.CancelAutofocus),
            "park_mount" => new DirectRigCommand(DirectRigCommandKind.ParkMount),
            "abort_exposure" => new DirectRigCommand(DirectRigCommandKind.AbortExposure),
            "stop_sequence" => new DirectRigCommand(DirectRigCommandKind.StopSequence),
            "start_sequence" => new DirectRigCommand(
                DirectRigCommandKind.StartSequence,
                SkipValidation: RequiredBoolean(command, "skip_validation")),
            _ => throw new DirectProtocolException(
                $"Unsupported Direct command kind '{wireKind}'."),
        };
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an object.");
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be a string.");
        }
        return value.GetString()!;
    }

    private static Guid RequiredGuid(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name);
        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be a non-empty UUID.");
        }
        return result;
    }

    private static uint RequiredUInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetUInt32(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an unsigned integer.");
        }
        return result;
    }

    private static ushort RequiredUInt16(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetUInt16(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an unsigned 16-bit integer.");
        }
        return result;
    }

    private static ushort? OptionalUInt16(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!value.TryGetUInt16(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an unsigned 16-bit integer.");
        }
        return result;
    }

    private static ulong RequiredUInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetUInt64(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an unsigned integer.");
        }
        return result;
    }

    private static long? OptionalInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!value.TryGetInt64(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an integer.");
        }
        return result;
    }

    private static bool? OptionalBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be an integer.");
        }
        return result;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static double RequiredDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !value.TryGetDouble(out var result)
            || !double.IsFinite(result))
        {
            throw new DirectProtocolException($"Direct message field '{name}' must be a finite number.");
        }
        return result;
    }
}

internal enum DirectQueryKind
{
    EventHistory,
    ImageHistory,
    Sequence,
    Thumbnail,
    LastAutofocus,
    MountInfo,
    CameraInfo,
    FilterwheelInfo,
    GuiderInfo,
    GuiderGraph,
    RotatorInfo,
    FocuserInfo,
    Command,
}

internal sealed record DirectQuery(
    Guid Id,
    DirectQueryKind Kind,
    uint? Index = null,
    DirectRigCommand? Command = null,
    long? ExpiresAt = null)
{
    internal bool IsExpiredAt(long unixTimeSeconds)
    {
        if (!ExpiresAt.HasValue)
        {
            return false;
        }

        var deadline = ExpiresAt.Value;
        var clockSkewGrace = Kind == DirectQueryKind.Command
            ? DirectProtocol.CommandExpiryClockSkewGraceSeconds
            : DirectProtocol.ExpiryClockSkewGraceSeconds;
        return deadline <= long.MaxValue - clockSkewGrace
            && unixTimeSeconds > deadline + clockSkewGrace;
    }
}

internal enum DirectRigCommandKind
{
    UnparkMount,
    HomeMount,
    ChangeFilter,
    StartGuiding,
    StopGuiding,
    CoolCamera,
    WarmCamera,
    StartAutofocus,
    CancelAutofocus,
    ParkMount,
    AbortExposure,
    StopSequence,
    StartSequence,
}

internal sealed record DirectRigCommand(
    DirectRigCommandKind Kind,
    int? FilterId = null,
    bool? Calibrate = null,
    double? Temperature = null,
    double? Minutes = null,
    bool? SkipValidation = null);

internal sealed class DirectProtocolException(string message) : Exception(message);

internal sealed class DirectQueryFailureException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    internal string Code { get; } = code;

    internal static DirectQueryFailureException ResourceNotReady(
        string message,
        Exception? innerException = null) =>
        new("resource_not_ready", message, innerException);
}

internal sealed record DirectWireMessage<T>(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] T Payload);

internal sealed record QueryResultPayload(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

internal sealed record PairRequestPayload(
    [property: JsonPropertyName("pairing_token")] string PairingToken,
    [property: JsonPropertyName("hello")] ClientHello Hello)
{
    public override string ToString() =>
        $"{nameof(PairRequestPayload)} {{ "
        + $"{nameof(PairingToken)} = {SensitiveDiagnostic.Secret(PairingToken)}, "
        + $"{nameof(Hello)} = {Hello} }}";
}

internal sealed record AuthRequestPayload(
    [property: JsonPropertyName("credential")] string Credential,
    [property: JsonPropertyName("hello")] ClientHello Hello)
{
    public override string ToString() =>
        $"{nameof(AuthRequestPayload)} {{ "
        + $"{nameof(Credential)} = {SensitiveDiagnostic.Secret(Credential)}, "
        + $"{nameof(Hello)} = {Hello} }}";
}

internal sealed record HeartbeatPayload(
    [property: JsonPropertyName("seq")] ulong Sequence);

internal sealed record AgentHello(
    ushort ProtocolVersion,
    ushort PayloadVersion,
    Guid ConnectionId,
    Guid NodeId,
    Guid ProfileId);

internal abstract record HubMessage;

internal sealed record HubAgentHelloMessage(AgentHello Hello) : HubMessage;

internal sealed record HubPairResultMessage(string Credential, AgentHello Hello) : HubMessage
{
    public override string ToString() =>
        $"{nameof(HubPairResultMessage)} {{ "
        + $"{nameof(Credential)} = {SensitiveDiagnostic.Secret(Credential)}, "
        + $"{nameof(Hello)} = {Hello} }}";
}

internal sealed record HubQueryMessage(DirectQuery Query) : HubMessage;

internal sealed record HubHeartbeatAckMessage(ulong Sequence) : HubMessage;

internal sealed record HubErrorMessage(string Message, bool Retryable) : HubMessage;

internal sealed record HubUnknownMessage(string Type) : HubMessage;

internal sealed record DirectApiEnvelope<T>(
    [property: JsonPropertyName("Response")] T Response,
    [property: JsonPropertyName("Error")] string Error,
    [property: JsonPropertyName("StatusCode")] int StatusCode,
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("Type")] string Type)
{
    internal static DirectApiEnvelope<T> Ok(T response) =>
        new(response, string.Empty, 200, true, "API");

    /// A command was accepted by N.I.N.A., but its asynchronous hardware
    /// operation has not completed. Keeping the existing envelope shape lets
    /// older consumers read the response while newer bots avoid claiming
    /// successful completion prematurely.
    internal static DirectApiEnvelope<T> Accepted(T response) =>
        new(response, string.Empty, 202, true, "API");
}

internal sealed record DirectCapabilities(
    [property: JsonPropertyName("event_history")] bool EventHistory,
    [property: JsonPropertyName("image_history")] bool ImageHistory,
    [property: JsonPropertyName("thumbnails")] bool Thumbnails,
    [property: JsonPropertyName("sequence")] bool Sequence,
    [property: JsonPropertyName("equipment_snapshots")] bool EquipmentSnapshots,
    [property: JsonPropertyName("autofocus_details")] bool AutofocusDetails,
    [property: JsonPropertyName("guider_graph")] bool GuiderGraph,
    [property: JsonPropertyName("commands")] bool Commands)
{
    internal static DirectCapabilities None { get; } = new(
        EventHistory: false,
        ImageHistory: false,
        Thumbnails: false,
        Sequence: false,
        EquipmentSnapshots: false,
        AutofocusDetails: false,
        GuiderGraph: false,
        Commands: false);
}

internal sealed record ClientHello(
    [property: JsonPropertyName("protocol_version")] ushort ProtocolVersion,
    [property: JsonPropertyName("payload_version")] ushort PayloadVersion,
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("profile_name")] string ProfileName,
    [property: JsonPropertyName("plugin_version")] string PluginVersion,
    [property: JsonPropertyName("nina_version")] string NinaVersion,
    [property: JsonPropertyName("capabilities")] DirectCapabilities Capabilities);
