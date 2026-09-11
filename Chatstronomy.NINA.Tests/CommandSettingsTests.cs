using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Settings;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Chatstronomy.NINA.Tests;

internal static class CommandSettingsTests
{
    private static readonly (string WireName, DirectRigCommandKind Kind,
        DirectCommandPermissions Permission, string Setting, string Label)[] TargetCommands =
    [
        ("slew_to_target", DirectRigCommandKind.SlewToTarget,
            DirectCommandPermissions.SlewToTarget, nameof(ChatstronomySettings.AllowSlewToTarget),
            "Slew to current target (/slew-target)"),
        ("center_target", DirectRigCommandKind.CenterTarget,
            DirectCommandPermissions.CenterTarget, nameof(ChatstronomySettings.AllowCenterTarget),
            "Center current target (/center-target)"),
        ("center_rotate_target", DirectRigCommandKind.CenterRotateTarget,
            DirectCommandPermissions.CenterRotateTarget, nameof(ChatstronomySettings.AllowCenterRotateTarget),
            "Center and rotate to current target (/center-rotate-target)"),
    ];

    internal static void Run()
    {
        TargetCommandCapabilityIsAdditiveAndSeparateFromConsent();
        TargetCommandsParseWithoutRemoteCoordinates();
        TargetCommandsRequireIndependentLocalConsent();
        TargetCommandSettingsPersistPerProfile();
        TargetCommandControlsAreVisibleAndMasterGated();
    }

    private static void TargetCommandCapabilityIsAdditiveAndSeparateFromConsent()
    {
        var legacy = JsonSerializer.SerializeToElement(DirectCapabilities.None, DirectProtocol.JsonOptions);
        Require(!legacy.TryGetProperty("target_commands", out _),
            "Legacy capability constructors must not imply target-command support.");
        var decodedLegacy = legacy.Deserialize<DirectCapabilities>(DirectProtocol.JsonOptions)!;
        Require(!decodedLegacy.TargetCommands,
            "A missing target-command capability must default to unsupported.");

        var readOnly = DirectCapabilities.None with { TargetCommands = true };
        var advertised = JsonSerializer.SerializeToElement(readOnly, DirectProtocol.JsonOptions);
        Require(advertised.GetProperty("target_commands").GetBoolean(),
            "Supporting peers must advertise the additive target-command flag.");
        Require(!advertised.GetProperty("commands").GetBoolean(),
            "Target-command implementation support must not imply local control consent.");
        var decoded = advertised.Deserialize<DirectCapabilities>(DirectProtocol.JsonOptions)!;
        Require(decoded.TargetCommands && !decoded.Commands,
            "Target-command capability must round-trip independently of consent.");
    }

    private static void TargetCommandsParseWithoutRemoteCoordinates()
    {
        foreach (var item in TargetCommands)
        {
            var id = Guid.NewGuid();
            var json = JsonSerializer.Serialize(new
            {
                type = "query",
                payload = new
                {
                    id,
                    kind = "command",
                    command = new
                    {
                        kind = item.WireName,
                        // Untrusted extensions must never select a remote coordinate
                        // or rotation: these commands resolve their target in N.I.N.A.
                        ra = 12.5,
                        dec = -30.0,
                        rotation = 90.0,
                    },
                },
            });
            var query = DirectProtocol.ParseQuery(json);
            Require(query.Id == id && query.Kind == DirectQueryKind.Command,
                $"Incorrect query envelope for {item.WireName}.");
            Require(query.Command == new DirectRigCommand(item.Kind),
                $"Target command {item.WireName} must be parameterless.");
        }
    }

    private static void TargetCommandsRequireIndependentLocalConsent()
    {
        var allPermissions = DirectCommandPermissions.None;
        foreach (var kind in Enum.GetValues<DirectRigCommandKind>())
        {
            var permission = DirectAccessPolicy.PermissionFor(kind);
            Require(permission != DirectCommandPermissions.None
                && (allPermissions & permission) == DirectCommandPermissions.None,
                $"Permission for {kind} is missing or overlaps another command.");
            allPermissions |= permission;
        }
        Require((ushort)allPermissions == ushort.MaxValue,
            "The sixteen independent command permissions should fill the ushort mask.");

        foreach (var item in TargetCommands)
        {
            var command = new DirectRigCommand(item.Kind);
            var policy = new DirectAccessPolicy(DirectAccessOptions.Default);
            RequireRejected(() => policy.RequireRemoteControl(command), item.Setting);
            policy.Update(DirectAccessOptions.Default with { AllowRemoteControl = true });
            RequireRejected(() => policy.RequireRemoteControl(command), item.Setting);
            policy.Update(DirectAccessOptions.Default with { AllowedCommands = item.Permission });
            RequireRejected(() => policy.RequireRemoteControl(command), item.Setting);
            policy.Update(DirectAccessOptions.Default with
            {
                AllowRemoteControl = true,
                AllowedCommands = item.Permission,
            });
            policy.RequireRemoteControl(command);
            foreach (var other in TargetCommands.Where(other => other.Kind != item.Kind))
            {
                RequireRejected(() => policy.RequireRemoteControl(new DirectRigCommand(other.Kind)),
                    other.Setting);
            }
            policy.Update(policy.Current with { AllowedCommands = DirectCommandPermissions.None });
            RequireRejected(() => policy.RequireRemoteControl(command), item.Setting);
        }
    }

    private static void TargetCommandSettingsPersistPerProfile()
    {
        var service = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfileService,
            TestProfileService>();
        var serviceState = (TestProfileService)(object)service;
        var first = CreateProfile();
        serviceState.ActiveProfile = first;
        var settings = new ChatstronomySettings(service);
        foreach (var item in TargetCommands)
        {
            var property = typeof(ChatstronomySettings).GetProperty(item.Setting)!;
            Require(Equals(false, property.GetValue(settings)), $"{item.Setting} must start off.");
            property.SetValue(settings, true);
            var reopened = new ChatstronomySettings(service);
            Require(Equals(true, property.GetValue(reopened)), $"{item.Setting} was not persisted.");
            Require(reopened.AccessOptions.AllowedCommands.HasFlag(item.Permission),
                $"{item.Setting} did not reach the local policy.");
            Require(!reopened.AccessOptions.CommandsEnabled,
                "Setting an individual command must not bypass the master switch.");
            property.SetValue(settings, false);
        }

        settings.AllowCenterRotateTarget = true;
        settings.AllowRemoteControl = true;
        Require(settings.AccessOptions.EffectiveAllowedCommands == DirectCommandPermissions.CenterRotateTarget,
            "Only the selected command should be enabled.");
        serviceState.ActiveProfile = CreateProfile();
        foreach (var item in TargetCommands)
        {
            Require(Equals(false, typeof(ChatstronomySettings).GetProperty(item.Setting)!.GetValue(settings)),
                $"{item.Setting} leaked into another profile.");
        }
        Require(!settings.AccessOptions.CommandsEnabled, "Master permission leaked into another profile.");
        serviceState.ActiveProfile = first;
        Require(settings.AllowCenterRotateTarget && settings.AllowRemoteControl,
            "Returning to the first profile must retain its own consent.");
    }

    private static void TargetCommandControlsAreVisibleAndMasterGated()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Options.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var item in TargetCommands)
        {
            var checkbox = document.Descendants(presentation + "CheckBox").Single(element =>
                (string?)element.Attribute("IsChecked") == $"{{Binding {item.Setting}}}");
            Require(checkbox.Parent?.Elements(presentation + "TextBlock").Single()
                .Attribute("Text")?.Value == item.Label, $"{item.Setting} has no visible label.");
            Require(checkbox.Ancestors(presentation + "UniformGrid").Any(element =>
                (string?)element.Attribute("IsEnabled") == "{Binding AllowRemoteControl}"),
                $"{item.Setting} is not gated by the master switch in the UI.");
            var viewModelProperty = typeof(ChatstronomyPlugin).GetProperty(item.Setting);
            Require(viewModelProperty?.CanRead == true && viewModelProperty.CanWrite,
                $"{item.Setting} has no writable view-model property.");
        }
    }

    private static global::NINA.Profile.Interfaces.IProfile CreateProfile()
    {
        var profile = DispatchProxy.Create<global::NINA.Profile.Interfaces.IProfile, TestProfile>();
        var state = (TestProfile)(object)profile;
        state.Id = Guid.NewGuid();
        state.PluginSettings = new global::NINA.Profile.PluginSettings();
        return profile;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireRejected(Action operation, string setting)
    {
        try
        {
            operation();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"Command executed without {setting} and master consent.");
    }

    private class TestProfile : DispatchProxy
    {
        internal Guid Id { get; set; }
        internal global::NINA.Profile.Interfaces.IPluginSettings PluginSettings { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_Id" => Id,
            "get_PluginSettings" => PluginSettings,
            "Dispose" => null,
            _ => throw new NotSupportedException($"Unexpected profile operation {method?.Name}."),
        };
    }

    private class TestProfileService : DispatchProxy
    {
        internal global::NINA.Profile.Interfaces.IProfile ActiveProfile { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_ActiveProfile" => ActiveProfile,
            _ => throw new NotSupportedException($"Unexpected profile-service operation {method?.Name}."),
        };
    }
}
