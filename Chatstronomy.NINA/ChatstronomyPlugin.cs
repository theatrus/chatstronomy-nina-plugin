using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Core.Utility.WindowService;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Chatstronomy.NINA.Configuration;
using Chatstronomy.NINA.Direct;
using Chatstronomy.NINA.Protocol;
using Chatstronomy.NINA.Remote;
using Chatstronomy.NINA.Runtime;
using Chatstronomy.NINA.Settings;
using Chatstronomy.NINA.UI;

namespace Chatstronomy.NINA;

/// <summary>
/// N.I.N.A. lifecycle entry point for Chatstronomy.
///
/// The local Direct provider and supervised runtime live behind this manifest;
/// the remote transport can reuse the same protocol and native data provider.
/// Keeping the manifest in the main Chatstronomy repository allows the C# and
/// Rust sides to be released and tested together.
/// </summary>
[Export(typeof(IPluginManifest))]
public sealed class ChatstronomyPlugin : PluginBase, INotifyPropertyChanged
{
    private readonly IProfileService profileService;
    private readonly ChatstronomySettings settings;
    private readonly DirectEventDeliveryPolicy eventDelivery;
    private readonly IChatstronomyRuntimeController runtimeController;
    private readonly INinaDirectDataProvider directDataProvider;
    private readonly ChatstronomyHubClient hubClient;
    private readonly AsyncCommand startRuntimeCommand;
    private readonly AsyncCommand stopRuntimeCommand;
    private readonly AsyncCommand connectHostedCommand;
    private readonly AsyncCommand disconnectHostedCommand;
    private readonly AsyncCommand forgetHostedCredentialCommand;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Guid nodeId = NodeIdentityStore.LoadOrCreate();
    private readonly Guid sessionId = Guid.NewGuid();
    private string? hostedOperationError;
    private bool initialized;

    [ImportingConstructor]
    public ChatstronomyPlugin(
        IProfileService profileService,
        ITelescopeMediator telescope,
        ICameraMediator camera,
        IFilterWheelMediator filterWheel,
        IGuiderMediator guider,
        IRotatorMediator rotator,
        IFocuserMediator focuser,
        ISequenceMediator sequence,
        IImageSaveMediator imageSave,
        IApplicationStatusMediator applicationStatus,
        IAutoFocusVMFactory autoFocusFactory,
        IImageHistoryVM imageHistory,
        IWindowServiceFactory windowFactory,
        IMessageBroker messageBroker)
    {
        this.profileService = profileService;
        settings = new ChatstronomySettings(profileService);
        eventDelivery = new DirectEventDeliveryPolicy(settings.EventDeliveryOptions);
        directDataProvider = new NinaDirectDataProvider(
            profileService,
            telescope,
            camera,
            filterWheel,
            guider,
            rotator,
            focuser,
            sequence,
            imageSave,
            applicationStatus,
            autoFocusFactory,
            imageHistory,
            windowFactory,
            messageBroker,
            eventDelivery);
        runtimeController = new ChatstronomyRuntimeController(directDataProvider);
        hubClient = new ChatstronomyHubClient(directDataProvider);
        startRuntimeCommand = new AsyncCommand(
            RestartLocalRuntimeAsync,
            () => UsesLocalRuntime && IsConfigurationValid);
        stopRuntimeCommand = new AsyncCommand(
            StopLocalRuntimeAsync,
            () => runtimeController.IsRunning);
        connectHostedCommand = new AsyncCommand(
            RestartHostedConnectionAsync,
            () => UseHostedService && IsConfigurationValid);
        disconnectHostedCommand = new AsyncCommand(
            StopHostedConnectionAsync,
            () => hubClient.IsRunning);
        forgetHostedCredentialCommand = new AsyncCommand(
            ForgetHostedCredentialAsync,
            CanForgetHostedCredential);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool UseDiscordWebhook
    {
        get => settings.DeliveryMode == ChatDeliveryMode.DiscordWebhook;
        set
        {
            if (value)
            {
                SetDeliveryMode(ChatDeliveryMode.DiscordWebhook);
            }
        }
    }

    public bool UseDiscordBot
    {
        get => settings.DeliveryMode == ChatDeliveryMode.DiscordBot;
        set
        {
            if (value)
            {
                SetDeliveryMode(ChatDeliveryMode.DiscordBot);
            }
        }
    }

    public bool UseMatrixOnly
    {
        get => settings.DeliveryMode == ChatDeliveryMode.MatrixOnly;
        set
        {
            if (value)
            {
                SetDeliveryMode(ChatDeliveryMode.MatrixOnly);
            }
        }
    }

    public bool UseHostedService
    {
        get => settings.DeliveryMode == ChatDeliveryMode.HostedService;
        set
        {
            if (value)
            {
                SetDeliveryMode(ChatDeliveryMode.HostedService);
            }
        }
    }

    public bool UsesLocalRuntime => !UseHostedService;

    public bool CanToggleLocalMatrix => UsesLocalRuntime && !UseMatrixOnly;

    public string DiscordWebhookUrl
    {
        get => settings.DiscordWebhookUrl;
        set
        {
            settings.DiscordWebhookUrl = value;
            RefreshStatus();
        }
    }

    public string DiscordBotToken
    {
        get => settings.DiscordBotToken;
        set
        {
            settings.DiscordBotToken = value;
            RefreshStatus();
        }
    }

    public string DiscordApplicationId
    {
        get => settings.DiscordApplicationId;
        set
        {
            settings.DiscordApplicationId = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public string DiscordChannelId
    {
        get => settings.DiscordChannelId;
        set
        {
            settings.DiscordChannelId = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public bool UseLocalMatrix
    {
        get => UseMatrixOnly || settings.UseLocalMatrix;
        set
        {
            settings.UseLocalMatrix = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public string MatrixHomeserverUrl
    {
        get => settings.MatrixHomeserverUrl;
        set
        {
            settings.MatrixHomeserverUrl = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public string MatrixUsername
    {
        get => settings.MatrixUsername;
        set
        {
            settings.MatrixUsername = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public string MatrixPassword
    {
        get => settings.MatrixPassword;
        set
        {
            settings.MatrixPassword = value;
            RefreshStatus();
        }
    }

    public string MatrixRoomId
    {
        get => settings.MatrixRoomId;
        set
        {
            settings.MatrixRoomId = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public string HostedServiceUrl
    {
        get => settings.HostedServiceUrl;
        set
        {
            settings.HostedServiceUrl = value;
            ClearHostedOperationError();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HostedCredentialStatus));
            RefreshStatus();
            if (initialized && UseHostedService && hubClient.IsRunning)
            {
                _ = StopHostedAfterServiceChangeAsync();
            }
        }
    }

    public string HostedPairingToken
    {
        get
        {
            try
            {
                var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(
                    HostedServiceUrl);
                return settings.ReadHostedPairingToken(
                    profileService.ActiveProfile.Id,
                    serviceUrl);
            }
            catch (Exception exception) when (IsHostedConfigurationException(exception))
            {
                return string.Empty;
            }
        }
        set
        {
            try
            {
                var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(
                    HostedServiceUrl);
                settings.WriteHostedPairingToken(
                    profileService.ActiveProfile.Id,
                    serviceUrl,
                    value);
                ClearHostedOperationError();
            }
            catch (Exception exception) when (IsHostedConfigurationException(exception))
            {
                SetHostedOperationError("Could not store the hosted pairing code", exception);
                return;
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HostedCredentialStatus));
            RefreshStatus();
        }
    }

    public string HostedCredentialStatus
    {
        get
        {
            try
            {
                var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(
                    HostedServiceUrl);
                var profileId = profileService.ActiveProfile.Id;
                var hasCredential = !string.IsNullOrWhiteSpace(
                    settings.ReadHostedCredential(profileId, serviceUrl));
                var hasPairingToken = !string.IsNullOrWhiteSpace(
                    settings.ReadHostedPairingToken(profileId, serviceUrl));
                if (hasCredential)
                {
                    return hasPairingToken
                        ? "A secure credential is stored and will be used. Choose Forget credential before pairing with the new code."
                        : "A secure hub credential is stored for this profile and service.";
                }
                return hasPairingToken
                    ? "A one-time pairing code is ready. Choose Pair / reconnect."
                    : "Not paired. Paste the one-time code from the Chatstronomy hub.";
            }
            catch (Exception exception) when (IsHostedConfigurationException(exception))
            {
                return HostedErrorMessage("Hosted credential status is unavailable", exception);
            }
        }
    }

    public string LocalRuntimePath
    {
        get => settings.LocalRuntimePath;
        set
        {
            settings.LocalRuntimePath = value;
            RaisePropertyChanged();
            RefreshStatus();
        }
    }

    public bool SendImageEvents
    {
        get => settings.SendImageEvents;
        set => SetEventDeliveryOption(() => settings.SendImageEvents = value);
    }

    public bool SendAutofocusEvents
    {
        get => settings.SendAutofocusEvents;
        set => SetEventDeliveryOption(() => settings.SendAutofocusEvents = value);
    }

    public bool SendGuidingEvents
    {
        get => settings.SendGuidingEvents;
        set => SetEventDeliveryOption(() => settings.SendGuidingEvents = value);
    }

    public bool SendMountEvents
    {
        get => settings.SendMountEvents;
        set => SetEventDeliveryOption(() => settings.SendMountEvents = value);
    }

    public bool SendSequenceEvents
    {
        get => settings.SendSequenceEvents;
        set => SetEventDeliveryOption(() => settings.SendSequenceEvents = value);
    }

    public bool SendTargetSchedulerEvents
    {
        get => settings.SendTargetSchedulerEvents;
        set => SetEventDeliveryOption(() => settings.SendTargetSchedulerEvents = value);
    }

    public bool SendFilterFocuserRotatorEvents
    {
        get => settings.SendFilterFocuserRotatorEvents;
        set => SetEventDeliveryOption(() => settings.SendFilterFocuserRotatorEvents = value);
    }

    public bool SendEquipmentConnectionEvents
    {
        get => settings.SendEquipmentConnectionEvents;
        set => SetEventDeliveryOption(() => settings.SendEquipmentConnectionEvents = value);
    }

    public bool SendOtherEvents
    {
        get => settings.SendOtherEvents;
        set => SetEventDeliveryOption(() => settings.SendOtherEvents = value);
    }

    public bool SendNinaNotifications
    {
        get => settings.SendNinaNotifications;
        set => SetEventDeliveryOption(() => settings.SendNinaNotifications = value);
    }

    public bool SendNinaLogErrors
    {
        get => settings.SendNinaLogErrors;
        set => SetEventDeliveryOption(() => settings.SendNinaLogErrors = value);
    }

    public bool SendNinaLogWarnings
    {
        get => settings.SendNinaLogWarnings;
        set => SetEventDeliveryOption(() => settings.SendNinaLogWarnings = value);
    }

    public bool SendNinaLogInformation
    {
        get => settings.SendNinaLogInformation;
        set => SetEventDeliveryOption(() => settings.SendNinaLogInformation = value);
    }

    public bool SendNinaLogDebug
    {
        get => settings.SendNinaLogDebug;
        set => SetEventDeliveryOption(() => settings.SendNinaLogDebug = value);
    }

    public bool SendNinaLogTrace
    {
        get => settings.SendNinaLogTrace;
        set => SetEventDeliveryOption(() => settings.SendNinaLogTrace = value);
    }

    public ICommand StartRuntimeCommand => startRuntimeCommand;

    public ICommand StopRuntimeCommand => stopRuntimeCommand;

    public ICommand ConnectHostedCommand => connectHostedCommand;

    public ICommand DisconnectHostedCommand => disconnectHostedCommand;

    public ICommand ForgetHostedCredentialCommand => forgetHostedCredentialCommand;

    public bool IsConfigurationValid
    {
        get
        {
            try
            {
                _ = BuildConfiguration();
                return true;
            }
            catch (Exception exception) when (IsHostedConfigurationException(exception))
            {
                return false;
            }
        }
    }

    public string ConfigurationStatus
    {
        get
        {
            try
            {
                _ = BuildConfiguration();
                var hostedError = Volatile.Read(ref hostedOperationError);
                var status = UseHostedService
                    ? hostedError ?? hubClient.StatusMessage
                    : runtimeController.IsRunning
                        ? runtimeController.StatusMessage
                        : $"Configuration is ready. {runtimeController.StatusMessage}";
                return status;
            }
            catch (Exception exception) when (IsHostedConfigurationException(exception))
            {
                var status = HostedErrorMessage("Configuration is not ready", exception);
                return status;
            }
        }
    }

    public override async Task Initialize()
    {
        directDataProvider.Start();
        profileService.ProfileChanged += ProfileServiceProfileChanged;
        runtimeController.StateChanged += RuntimeControllerStateChanged;
        hubClient.StateChanged += HubClientStateChanged;
        hubClient.CredentialIssued += HubClientCredentialIssued;
        await base.Initialize();
        initialized = true;
        await StartConfiguredModeAsync(CancellationToken.None);
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileServiceProfileChanged;
        runtimeController.StateChanged -= RuntimeControllerStateChanged;
        hubClient.StateChanged -= HubClientStateChanged;
        hubClient.CredentialIssued -= HubClientCredentialIssued;
        initialized = false;
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            await hubClient.StopAsync(CancellationToken.None);
            if (runtimeController.IsRunning)
            {
                await runtimeController.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
        directDataProvider.Stop();
        await base.Teardown();
    }

    internal ChatstronomyConfiguration BuildConfiguration()
    {
        ChatDeliveryConfiguration? delivery = settings.DeliveryMode switch
        {
            ChatDeliveryMode.DiscordWebhook => new DiscordWebhookDeliveryConfiguration(
                ChatstronomyConfigurationValidator.RequireDiscordWebhook(DiscordWebhookUrl)),
            ChatDeliveryMode.DiscordBot => new DiscordBotDeliveryConfiguration(
                ChatstronomyConfigurationValidator.RequireSecret(
                    DiscordBotToken,
                    "Discord bot token"),
                ChatstronomyConfigurationValidator.OptionalDiscordSnowflake(
                    DiscordApplicationId,
                    "Discord application ID"),
                ChatstronomyConfigurationValidator.RequireDiscordSnowflake(
                    DiscordChannelId,
                    "Default Discord channel ID")),
            ChatDeliveryMode.HostedService => new HostedDeliveryConfiguration(
                BuildHostedConnectionConfiguration().ServiceUrl),
            ChatDeliveryMode.MatrixOnly => null,
            _ => throw new InvalidOperationException("Unknown Chatstronomy delivery mode."),
        };

        var localRuntime = UsesLocalRuntime
            ? ChatstronomyConfigurationValidator.BuildLocalRuntime(LocalRuntimePath)
            : null;
        var matrix = UsesLocalRuntime && UseLocalMatrix
            ? new MatrixDeliveryConfiguration(
                ChatstronomyConfigurationValidator.RequireMatrixHomeserver(
                    MatrixHomeserverUrl),
                ChatstronomyConfigurationValidator.RequireSecret(
                    MatrixUsername,
                    "Matrix username"),
                ChatstronomyConfigurationValidator.RequireSecret(
                    MatrixPassword,
                    "Matrix password"),
                ChatstronomyConfigurationValidator.RequireSecret(
                    MatrixRoomId,
                    "Default Matrix room ID"))
            : null;
        return new ChatstronomyConfiguration(delivery, matrix, localRuntime);
    }

    internal HubConnectionConfiguration BuildHostedConnectionConfiguration()
    {
        var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(HostedServiceUrl);
        var profileId = profileService.ActiveProfile.Id;
        var configuration = new HubConnectionConfiguration(
            serviceUrl,
            settings.ReadHostedCredential(profileId, serviceUrl),
            settings.ReadHostedPairingToken(profileId, serviceUrl),
            profileId);
        configuration.Validate();
        return configuration;
    }

    /// <summary>
    /// Build the identity handshake used when this N.I.N.A. process connects
    /// to a local or remote Chatstronomy hub. The node and profile GUIDs are
    /// stable; the session GUID changes each time the plugin is loaded.
    /// </summary>
    internal ClientHello CreateClientHello()
    {
        var activeProfile = profileService.ActiveProfile;
        var pluginVersion = typeof(ChatstronomyPlugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        var ninaVersion = typeof(IProfileService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new ClientHello(
            ProtocolVersion: DirectProtocol.CurrentVersion,
            PayloadVersion: DirectProtocol.CurrentPayloadVersion,
            NodeId: nodeId,
            SessionId: sessionId,
            ProcessId: Environment.ProcessId,
            ProfileId: activeProfile.Id,
            ProfileName: activeProfile.Name,
            PluginVersion: pluginVersion,
            NinaVersion: ninaVersion,
            Capabilities: directDataProvider.Capabilities);
    }

    private void SetDeliveryMode(ChatDeliveryMode mode)
    {
        if (settings.DeliveryMode == mode)
        {
            return;
        }

        settings.DeliveryMode = mode;
        ClearHostedOperationError();
        RefreshAllProperties();
        if (initialized)
        {
            _ = StartConfiguredModeAsync(CancellationToken.None);
        }
    }

    private async void ProfileServiceProfileChanged(object? sender, EventArgs args)
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            await hubClient.StopAsync(CancellationToken.None);
            if (runtimeController.IsRunning)
            {
                await runtimeController.StopAsync(CancellationToken.None);
            }
            eventDelivery.Update(settings.EventDeliveryOptions);
            directDataProvider.Reset();
            RefreshAllProperties();
            await StartConfiguredModeCoreAsync(CancellationToken.None);
        }
        catch
        {
            RefreshStatus();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StartConfiguredModeAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StartConfiguredModeCoreAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Incomplete settings are expected while the user is configuring
            // the plugin. ConfigurationStatus displays the validation error.
        }
        catch
        {
            // Runtime and hub clients retain their user-facing failure state.
        }
        finally
        {
            lifecycleGate.Release();
            RefreshStatus();
        }
    }

    private async Task StartConfiguredModeCoreAsync(CancellationToken cancellationToken)
    {
        if (UseHostedService)
        {
            ClearHostedOperationError();
            if (runtimeController.IsRunning)
            {
                await runtimeController.StopAsync(cancellationToken);
            }
            await StartHostedCoreAsync(cancellationToken);
            return;
        }

        await hubClient.StopAsync(cancellationToken);
        if (runtimeController.IsRunning)
        {
            await runtimeController.StopAsync(cancellationToken);
        }
        await StartLocalRuntimeCoreAsync(cancellationToken);
    }

    private async Task StartLocalRuntimeCoreAsync(CancellationToken cancellationToken)
    {
        var configuration = BuildConfiguration();
        var profile = profileService.ActiveProfile;
        await runtimeController.StartAsync(
            configuration,
            new LocalRuntimeIdentity(nodeId, profile.Id, profile.Name),
            cancellationToken);
    }

    private Task StartHostedCoreAsync(CancellationToken cancellationToken) =>
        hubClient.StartAsync(
            BuildHostedConnectionConfiguration(),
            CreateClientHello(),
            cancellationToken);

    private async Task RestartLocalRuntimeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (runtimeController.IsRunning)
            {
                await runtimeController.StopAsync(CancellationToken.None);
            }
            await StartLocalRuntimeCoreAsync(CancellationToken.None);
        }
        catch
        {
            // RuntimeController retains a user-facing status message.
        }
        finally
        {
            lifecycleGate.Release();
            RefreshStatus();
        }
    }

    private async Task StopLocalRuntimeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            await runtimeController.StopAsync(CancellationToken.None);
        }
        catch
        {
            // RuntimeController retains a user-facing status message when the
            // controlled process cannot be stopped cleanly.
        }
        finally
        {
            lifecycleGate.Release();
            RefreshStatus();
        }
    }

    private async Task RestartHostedConnectionAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            ClearHostedOperationError();
            await hubClient.StopAsync(CancellationToken.None);
            await StartHostedCoreAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetHostedOperationError("Could not start the hosted connection", exception);
        }
        finally
        {
            lifecycleGate.Release();
            RefreshStatus();
        }
    }

    private async Task StopHostedConnectionAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            ClearHostedOperationError();
            await hubClient.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetHostedOperationError("Could not stop the hosted connection", exception);
        }
        finally
        {
            lifecycleGate.Release();
            RefreshStatus();
        }
    }

    private async Task StopHostedAfterServiceChangeAsync()
    {
        try
        {
            await StopHostedConnectionAsync();
        }
        catch
        {
            RefreshStatus();
        }
    }

    private async Task ForgetHostedCredentialAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            ClearHostedOperationError();
            await hubClient.StopAsync(CancellationToken.None);
            var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(
                HostedServiceUrl);
            settings.WriteHostedCredential(
                profileService.ActiveProfile.Id,
                serviceUrl,
                credential: null);
        }
        catch (Exception exception)
        {
            SetHostedOperationError("Could not forget the hosted credential", exception);
        }
        finally
        {
            lifecycleGate.Release();
            RaisePropertyChanged(nameof(HostedCredentialStatus));
            RefreshStatus();
        }
    }

    private void RuntimeControllerStateChanged(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RefreshStatus));
            return;
        }
        RefreshStatus();
    }

    private void HubClientStateChanged(object? sender, EventArgs args) =>
        DispatchRefreshStatus();

    private void HubClientCredentialIssued(
        object? sender,
        HubCredentialIssuedEventArgs args)
    {
        var credentialStored = false;
        try
        {
            // Store the durable credential before removing the one-time token.
            // If token deletion fails, credential-first authentication still
            // makes the next restart safe.
            settings.WriteHostedCredential(args.ProfileId, args.ServiceUrl, args.Credential);
            credentialStored = true;
            settings.WriteHostedPairingToken(
                args.ProfileId,
                args.ServiceUrl,
                pairingToken: null);
            ClearHostedOperationError();
        }
        catch (Exception exception)
        {
            // Pairing has already completed at the hub, so keep the current
            // authenticated connection alive and make the persistence problem
            // visible instead of throwing through the WebSocket receive loop.
            SetHostedOperationError(
                credentialStored
                    ? "Connected and the credential was saved, but the one-time pairing code could not be cleared"
                    : "Connected, but the hosted credential could not be saved securely; generate a new pairing code before restarting N.I.N.A.",
                exception);
            return;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                RaisePropertyChanged(nameof(HostedPairingToken));
                RaisePropertyChanged(nameof(HostedCredentialStatus));
                RefreshStatus();
            }));
            return;
        }
        RaisePropertyChanged(nameof(HostedPairingToken));
        RaisePropertyChanged(nameof(HostedCredentialStatus));
        RefreshStatus();
    }

    private void DispatchRefreshStatus()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RefreshStatus));
            return;
        }
        RefreshStatus();
    }

    private bool HasHostedCredential()
    {
        var serviceUrl = ChatstronomyConfigurationValidator.RequireHostedUrl(HostedServiceUrl);
        return !string.IsNullOrWhiteSpace(
            settings.ReadHostedCredential(profileService.ActiveProfile.Id, serviceUrl));
    }

    private bool CanForgetHostedCredential()
    {
        if (!UseHostedService)
        {
            return false;
        }
        try
        {
            return HasHostedCredential();
        }
        catch (Exception exception) when (IsHostedConfigurationException(exception))
        {
            return false;
        }
    }

    private static bool IsHostedConfigurationException(Exception exception) =>
        exception is InvalidOperationException or Win32Exception;

    private static string HostedErrorMessage(string context, Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return $"{context}: {message}";
    }

    private void SetHostedOperationError(string context, Exception exception)
    {
        Volatile.Write(
            ref hostedOperationError,
            HostedErrorMessage(context, exception));
        DispatchRefreshStatus();
    }

    private void ClearHostedOperationError() =>
        Volatile.Write(ref hostedOperationError, null);

    private void RefreshAllProperties()
    {
        foreach (var propertyName in new[]
        {
            nameof(UseDiscordWebhook),
            nameof(UseDiscordBot),
            nameof(UseMatrixOnly),
            nameof(UseHostedService),
            nameof(UsesLocalRuntime),
            nameof(CanToggleLocalMatrix),
            nameof(DiscordWebhookUrl),
            nameof(DiscordBotToken),
            nameof(DiscordApplicationId),
            nameof(DiscordChannelId),
            nameof(UseLocalMatrix),
            nameof(MatrixHomeserverUrl),
            nameof(MatrixUsername),
            nameof(MatrixPassword),
            nameof(MatrixRoomId),
            nameof(HostedServiceUrl),
            nameof(HostedPairingToken),
            nameof(HostedCredentialStatus),
            nameof(LocalRuntimePath),
            nameof(SendImageEvents),
            nameof(SendAutofocusEvents),
            nameof(SendGuidingEvents),
            nameof(SendMountEvents),
            nameof(SendSequenceEvents),
            nameof(SendTargetSchedulerEvents),
            nameof(SendFilterFocuserRotatorEvents),
            nameof(SendEquipmentConnectionEvents),
            nameof(SendOtherEvents),
            nameof(SendNinaNotifications),
            nameof(SendNinaLogErrors),
            nameof(SendNinaLogWarnings),
            nameof(SendNinaLogInformation),
            nameof(SendNinaLogDebug),
            nameof(SendNinaLogTrace),
            nameof(IsConfigurationValid),
            nameof(ConfigurationStatus),
        })
        {
            RaisePropertyChanged(propertyName);
        }
    }

    private void RefreshStatus()
    {
        RaisePropertyChanged(nameof(IsConfigurationValid));
        RaisePropertyChanged(nameof(ConfigurationStatus));
        startRuntimeCommand.RaiseCanExecuteChanged();
        stopRuntimeCommand.RaiseCanExecuteChanged();
        connectHostedCommand.RaiseCanExecuteChanged();
        disconnectHostedCommand.RaiseCanExecuteChanged();
        forgetHostedCredentialCommand.RaiseCanExecuteChanged();
    }

    private void SetEventDeliveryOption(
        Action update,
        [CallerMemberName] string? propertyName = null)
    {
        update();
        eventDelivery.Update(settings.EventDeliveryOptions);
        RaisePropertyChanged(propertyName);
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
