using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NINA.Plugin;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Chatstronomy.NINA.Settings;

internal sealed class ChatstronomySettings
{
    internal static readonly Guid PluginId =
        Guid.Parse("5e7c25c4-f654-4e22-9e21-3127048221c0");
    internal const string DefaultHostedServiceUrl = "https://hub.chatstronomy.com/";

    private const string CredentialPrefix = "Chatstronomy.NINA";
    private const string LegacyHostedServiceOrigin = "https://chatstronomy.com";
    private readonly IProfileService profileService;
    private readonly PluginOptionsAccessor options;

    public ChatstronomySettings(IProfileService profileService)
    {
        this.profileService = profileService;
        options = new PluginOptionsAccessor(profileService, PluginId);
    }

    public ChatDeliveryMode DeliveryMode
    {
        get
        {
            var value = options.GetValueString(
                nameof(DeliveryMode),
                ChatDeliveryMode.DiscordWebhook.ToString());
            return Enum.TryParse<ChatDeliveryMode>(value, out var mode)
                ? mode
                : ChatDeliveryMode.DiscordWebhook;
        }
        set => options.SetValueString(nameof(DeliveryMode), value.ToString());
    }

    public string DiscordWebhookUrl
    {
        get => WindowsCredentialStore.Read(CredentialTarget("discord-webhook")) ?? string.Empty;
        set => WindowsCredentialStore.Write(CredentialTarget("discord-webhook"), value?.Trim());
    }

    public string DiscordBotToken
    {
        get => WindowsCredentialStore.Read(CredentialTarget("discord-bot-token")) ?? string.Empty;
        set => WindowsCredentialStore.Write(CredentialTarget("discord-bot-token"), value?.Trim());
    }

    public string DiscordApplicationId
    {
        get => options.GetValueString(nameof(DiscordApplicationId), string.Empty);
        set => options.SetValueString(nameof(DiscordApplicationId), value?.Trim() ?? string.Empty);
    }

    public string DiscordChannelId
    {
        get => options.GetValueString(nameof(DiscordChannelId), string.Empty);
        set => options.SetValueString(nameof(DiscordChannelId), value?.Trim() ?? string.Empty);
    }

    public bool UseLocalMatrix
    {
        get => options.GetValueBoolean(nameof(UseLocalMatrix), false);
        set => options.SetValueBoolean(nameof(UseLocalMatrix), value);
    }

    public string MatrixHomeserverUrl
    {
        get => options.GetValueString(nameof(MatrixHomeserverUrl), "https://matrix.org/");
        set => options.SetValueString(nameof(MatrixHomeserverUrl), value?.Trim() ?? string.Empty);
    }

    public string MatrixUsername
    {
        get => options.GetValueString(nameof(MatrixUsername), string.Empty);
        set => options.SetValueString(nameof(MatrixUsername), value?.Trim() ?? string.Empty);
    }

    public string MatrixPassword
    {
        get => WindowsCredentialStore.Read(CredentialTarget("matrix-password")) ?? string.Empty;
        set => WindowsCredentialStore.Write(CredentialTarget("matrix-password"), value);
    }

    public string MatrixRoomId
    {
        get => options.GetValueString(nameof(MatrixRoomId), string.Empty);
        set => options.SetValueString(nameof(MatrixRoomId), value?.Trim() ?? string.Empty);
    }

    public string HostedServiceUrl
    {
        get
        {
            var stored = options.GetValueString(
                nameof(HostedServiceUrl),
                DefaultHostedServiceUrl);
            var normalized = NormalizeHostedServiceUrl(stored);
            if (!string.Equals(stored, normalized, StringComparison.Ordinal))
            {
                options.SetValueString(nameof(HostedServiceUrl), normalized);
            }
            return normalized;
        }
        set => options.SetValueString(nameof(HostedServiceUrl), value?.Trim() ?? string.Empty);
    }

    internal static string NormalizeHostedServiceUrl(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.TrimEnd('/').Equals(
            LegacyHostedServiceOrigin,
            StringComparison.OrdinalIgnoreCase)
            ? DefaultHostedServiceUrl
            : trimmed;
    }

    public string ReadHostedCredential(Guid profileId, Uri serviceUrl)
    {
        var target = HostedCredentialTarget(profileId, serviceUrl);
        var credential = WindowsCredentialStore.Read(target);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            return credential;
        }

        // Early development builds called this an opaque reference. If one
        // actually contains a hub credential, migrate it out of profile JSON.
        var legacy = options.GetValueString("HostedCredentialReference", string.Empty);
        if (profileId == profileService.ActiveProfile.Id
            && legacy.StartsWith("csrc_", StringComparison.Ordinal))
        {
            WindowsCredentialStore.Write(target, legacy);
            options.SetValueString("HostedCredentialReference", string.Empty);
            return legacy;
        }

        return string.Empty;
    }

    public void WriteHostedCredential(Guid profileId, Uri serviceUrl, string? credential) =>
        WindowsCredentialStore.Write(
            HostedCredentialTarget(profileId, serviceUrl),
            credential?.Trim());

    public string ReadHostedPairingToken(Guid profileId, Uri serviceUrl) =>
        WindowsCredentialStore.Read(HostedSecretTarget(
            profileId,
            serviceUrl,
            "hosted-pairing-token"))
        ?? string.Empty;

    public void WriteHostedPairingToken(
        Guid profileId,
        Uri serviceUrl,
        string? pairingToken) =>
        WindowsCredentialStore.Write(
            HostedSecretTarget(profileId, serviceUrl, "hosted-pairing-token"),
            pairingToken?.Trim());

    public string LocalRuntimePath
    {
        get => options.GetValueString(nameof(LocalRuntimePath), DefaultRuntimePath());
        set => options.SetValueString(nameof(LocalRuntimePath), value?.Trim() ?? string.Empty);
    }

    public string AdvancedApiBaseUrl
    {
        get => options.GetValueString(nameof(AdvancedApiBaseUrl), "http://127.0.0.1:1888/");
        set => options.SetValueString(nameof(AdvancedApiBaseUrl), value?.Trim() ?? string.Empty);
    }

    public RuntimeSourceMode RuntimeSourceMode
    {
        get
        {
            var value = options.GetValueString(
                nameof(RuntimeSourceMode),
                global::Chatstronomy.NINA.Settings.RuntimeSourceMode.Direct.ToString());
            return Enum.TryParse<RuntimeSourceMode>(value, out var mode)
                ? mode
                : global::Chatstronomy.NINA.Settings.RuntimeSourceMode.Direct;
        }
        set => options.SetValueString(nameof(RuntimeSourceMode), value.ToString());
    }

    public string PollingIntervalSeconds
    {
        get => options.GetValueString(nameof(PollingIntervalSeconds), "5");
        set => options.SetValueString(nameof(PollingIntervalSeconds), value?.Trim() ?? string.Empty);
    }

    public bool StartLocalRuntime
    {
        get => options.GetValueBoolean(nameof(StartLocalRuntime), true);
        set => options.SetValueBoolean(nameof(StartLocalRuntime), value);
    }

    public bool StopLocalRuntimeWithNina
    {
        get => options.GetValueBoolean(nameof(StopLocalRuntimeWithNina), true);
        set => options.SetValueBoolean(nameof(StopLocalRuntimeWithNina), value);
    }

    public bool SendImageEvents
    {
        get => options.GetValueBoolean(nameof(SendImageEvents), true);
        set => options.SetValueBoolean(nameof(SendImageEvents), value);
    }

    public bool SendAutofocusEvents
    {
        get => options.GetValueBoolean(nameof(SendAutofocusEvents), true);
        set => options.SetValueBoolean(nameof(SendAutofocusEvents), value);
    }

    public bool SendGuidingEvents
    {
        get => options.GetValueBoolean(nameof(SendGuidingEvents), true);
        set => options.SetValueBoolean(nameof(SendGuidingEvents), value);
    }

    public bool SendMountEvents
    {
        get => options.GetValueBoolean(nameof(SendMountEvents), true);
        set => options.SetValueBoolean(nameof(SendMountEvents), value);
    }

    public bool SendSequenceEvents
    {
        get => options.GetValueBoolean(nameof(SendSequenceEvents), true);
        set => options.SetValueBoolean(nameof(SendSequenceEvents), value);
    }

    public bool SendTargetSchedulerEvents
    {
        get => options.GetValueBoolean(nameof(SendTargetSchedulerEvents), true);
        set => options.SetValueBoolean(nameof(SendTargetSchedulerEvents), value);
    }

    public bool SendFilterFocuserRotatorEvents
    {
        get => options.GetValueBoolean(nameof(SendFilterFocuserRotatorEvents), true);
        set => options.SetValueBoolean(nameof(SendFilterFocuserRotatorEvents), value);
    }

    public bool SendEquipmentConnectionEvents
    {
        get => options.GetValueBoolean(nameof(SendEquipmentConnectionEvents), true);
        set => options.SetValueBoolean(nameof(SendEquipmentConnectionEvents), value);
    }

    public bool SendOtherEvents
    {
        get => options.GetValueBoolean(nameof(SendOtherEvents), true);
        set => options.SetValueBoolean(nameof(SendOtherEvents), value);
    }

    public bool SendNinaNotifications
    {
        get => options.GetValueBoolean(nameof(SendNinaNotifications), true);
        set => options.SetValueBoolean(nameof(SendNinaNotifications), value);
    }

    public bool SendNinaLogErrors
    {
        get => options.GetValueBoolean(nameof(SendNinaLogErrors), false);
        set => options.SetValueBoolean(nameof(SendNinaLogErrors), value);
    }

    public bool SendNinaLogWarnings
    {
        get => options.GetValueBoolean(nameof(SendNinaLogWarnings), false);
        set => options.SetValueBoolean(nameof(SendNinaLogWarnings), value);
    }

    public bool SendNinaLogInformation
    {
        get => options.GetValueBoolean(nameof(SendNinaLogInformation), false);
        set => options.SetValueBoolean(nameof(SendNinaLogInformation), value);
    }

    public bool SendNinaLogDebug
    {
        get => options.GetValueBoolean(nameof(SendNinaLogDebug), false);
        set => options.SetValueBoolean(nameof(SendNinaLogDebug), value);
    }

    public bool SendNinaLogTrace
    {
        get => options.GetValueBoolean(nameof(SendNinaLogTrace), false);
        set => options.SetValueBoolean(nameof(SendNinaLogTrace), value);
    }

    internal DirectEventDeliveryOptions EventDeliveryOptions => new(
        Images: SendImageEvents,
        Autofocus: SendAutofocusEvents,
        Guiding: SendGuidingEvents,
        Mount: SendMountEvents,
        Sequence: SendSequenceEvents,
        TargetScheduler: SendTargetSchedulerEvents,
        FilterFocuserRotator: SendFilterFocuserRotatorEvents,
        EquipmentConnections: SendEquipmentConnectionEvents,
        OtherEvents: SendOtherEvents,
        NinaNotifications: SendNinaNotifications,
        NinaLogErrors: SendNinaLogErrors,
        NinaLogWarnings: SendNinaLogWarnings,
        NinaLogInformation: SendNinaLogInformation,
        NinaLogDebug: SendNinaLogDebug,
        NinaLogTrace: SendNinaLogTrace);

    private string CredentialTarget(string kind) =>
        CredentialTarget(profileService.ActiveProfile.Id, kind);

    private static string CredentialTarget(Guid profileId, string kind) =>
        $"{CredentialPrefix}/{profileId:D}/{kind}";

    private static string HostedCredentialTarget(Guid profileId, Uri serviceUrl)
        => HostedSecretTarget(profileId, serviceUrl, "hosted-rig-credential");

    internal static string HostedSecretTarget(
        Guid profileId,
        Uri serviceUrl,
        string kind)
    {
        var effectivePort = serviceUrl.IsDefaultPort ? 443 : serviceUrl.Port;
        var origin = $"{serviceUrl.IdnHost.ToLowerInvariant()}:{effectivePort}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))
            .ToLowerInvariant();
        return CredentialTarget(profileId, $"{kind}/{digest}");
    }

    private static string DefaultRuntimePath()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, "runtime", "chatstronomy.exe");
    }
}
