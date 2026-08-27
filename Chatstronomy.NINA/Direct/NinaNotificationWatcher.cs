using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NinaNotification = NINA.Core.Utility.Notification.Notification;

namespace Chatstronomy.NINA.Direct;

internal sealed record NinaNotificationRecord(
    DateTime Time,
    string Level,
    string Header,
    string Message);

/// <summary>
/// Observes the notification implementation used by the running N.I.N.A.
/// version. N.I.N.A. does not expose popup notifications as a plugin service,
/// so reflection is isolated here and failure simply disables popup forwarding
/// without affecting N.I.N.A.
/// </summary>
internal sealed class NinaNotificationWatcher : IDisposable
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticMembers =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Action<NinaNotificationRecord, long> onNotification;
    private INotifyCollectionChanged? nativeNotifications;
    private NotifyCollectionChangedEventHandler? nativeHandler;
    private object? legacyEventSource;
    private EventInfo? legacyEvent;
    private Delegate? legacyHandler;

    internal NinaNotificationWatcher(Action<NinaNotificationRecord, long> onNotification)
    {
        this.onNotification = onNotification;
    }

    internal bool Start(long captureGeneration)
    {
        if (IsStarted)
        {
            return true;
        }
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(NinaNotification).TypeHandle);

            // N.I.N.A. 3.3 replaced ToastNotifications with its own WPF
            // NotificationManager. Observe its collection without naming the
            // internal manager or notification implementation at compile time.
            var manager = typeof(NinaNotification).GetField(
                "manager",
                StaticMembers)?.GetValue(null);
            if (manager is not null
                && TryObserveNativeManager(manager, captureGeneration))
            {
                return true;
            }

            // N.I.N.A. 3.2 and earlier expose the ToastNotifications lifetime
            // event only through private fields. Keep this path reflection-only
            // so loading the plugin in 3.3 never requires ToastNotifications.dll.
            var notifier = typeof(NinaNotification).GetField(
                "notifier",
                StaticMembers)?.GetValue(null);
            return notifier is not null
                && TryObserveLegacyNotifier(notifier, captureGeneration);
        }
        catch
        {
            Stop();
            return false;
        }
    }

    internal bool TryObserveNativeManager(object manager, long captureGeneration = 0)
    {
        if (IsStarted)
        {
            return true;
        }
        try
        {
            var notifications = manager.GetType().GetProperty(
                "Notifications",
                InstanceMembers)?.GetValue(manager) as INotifyCollectionChanged;
            if (notifications is null)
            {
                return false;
            }

            NotifyCollectionChangedEventHandler handler = (sender, args) =>
                NotificationsChanged(sender, args, captureGeneration);
            notifications.CollectionChanged += handler;
            nativeNotifications = notifications;
            nativeHandler = handler;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool TryObserveLegacyNotifier(object notifier, long captureGeneration = 0)
    {
        if (IsStarted)
        {
            return true;
        }
        try
        {
            var notifierType = notifier.GetType();
            var lifetimeField = notifierType.GetField(
                "_lifetimeSupervisor",
                InstanceMembers);
            var lifetime = lifetimeField?.GetValue(notifier);
            if (lifetime is null)
            {
                notifierType.GetMethod(
                    "Configure",
                    InstanceMembers,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)?.Invoke(notifier, null);
                lifetime = lifetimeField?.GetValue(notifier);
            }
            if (lifetime is null)
            {
                return false;
            }

            var eventInfo = lifetime.GetType().GetEvent(
                "ShowNotificationRequested",
                InstanceMembers);
            var handlerType = eventInfo?.EventHandlerType;
            if (eventInfo is null || handlerType is null)
            {
                return false;
            }

            var handler = CreateLegacyHandler(handlerType, captureGeneration);
            eventInfo.AddEventHandler(lifetime, handler);
            legacyEventSource = lifetime;
            legacyEvent = eventInfo;
            legacyHandler = handler;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void Stop()
    {
        var notifications = nativeNotifications;
        nativeNotifications = null;
        if (notifications is not null)
        {
            var nativeHandlerToRemove = nativeHandler;
            nativeHandler = null;
            if (nativeHandlerToRemove is not null)
            {
                notifications.CollectionChanged -= nativeHandlerToRemove;
            }
        }

        var source = legacyEventSource;
        var eventInfo = legacyEvent;
        var legacyHandlerToRemove = legacyHandler;
        legacyEventSource = null;
        legacyEvent = null;
        legacyHandler = null;
        if (source is not null
            && eventInfo is not null
            && legacyHandlerToRemove is not null)
        {
            try
            {
                eventInfo.RemoveEventHandler(source, legacyHandlerToRemove);
            }
            catch
            {
                // N.I.N.A. may already be disposing its notification host.
            }
        }
    }

    public void Dispose() => Stop();

    private bool IsStarted => nativeNotifications is not null || legacyEventSource is not null;

    private Delegate CreateLegacyHandler(Type handlerType, long captureGeneration)
    {
        var invoke = handlerType.GetMethod("Invoke")
            ?? throw new InvalidOperationException("Notification event has no invoke method.");
        var parameters = invoke.GetParameters();
        if (invoke.ReturnType != typeof(void) || parameters.Length != 2)
        {
            throw new InvalidOperationException("Unsupported notification event signature.");
        }

        var sender = Expression.Parameter(parameters[0].ParameterType, "sender");
        var args = Expression.Parameter(parameters[1].ParameterType, "args");
        var callback = typeof(NinaNotificationWatcher).GetMethod(
            nameof(LegacyNotificationShown),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(LegacyNotificationShown));
        var body = Expression.Call(
            Expression.Constant(this),
            callback,
            Expression.Convert(sender, typeof(object)),
            Expression.Convert(args, typeof(object)),
            Expression.Constant(captureGeneration));
        return Expression.Lambda(handlerType, body, sender, args).Compile();
    }

    private void LegacyNotificationShown(
        object? sender,
        object args,
        long captureGeneration)
    {
        var notification = GetProperty(args, "Notification");
        if (notification is not null)
        {
            Publish(notification, captureGeneration);
        }
    }

    private void NotificationsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args,
        long captureGeneration)
    {
        if (args.NewItems is null)
        {
            return;
        }
        foreach (var notification in args.NewItems)
        {
            if (notification is not null)
            {
                Publish(notification, captureGeneration);
            }
        }
    }

    private void Publish(object notification, long captureGeneration)
    {
        try
        {
            var message = GetProperty(notification, "Message") as string;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var timestamp = GetProperty(notification, "DateTime") is DateTime time
                ? time
                : DateTime.Now;
            var header = GetProperty(notification, "Header") as string;
            var color = GetProperty(notification, "Color") as Brush;
            onNotification(new NinaNotificationRecord(
                timestamp,
                Classify(color),
                header?.Trim() ?? string.Empty,
                message.Trim()), captureGeneration);
        }
        catch
        {
            // Popup rendering must remain independent of chat forwarding.
        }
    }

    private static object? GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers)?.GetValue(instance);

    internal static string Classify(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return "INFORMATION";
        }
        var color = solid.Color;
        if (color.R >= 200 && color.G < 100)
        {
            return "ERROR";
        }
        if (color.R >= 200 && color.G >= 150 && color.B < 100)
        {
            return "WARNING";
        }
        if (color.G >= 150 && color.R < 100)
        {
            return "SUCCESS";
        }
        return "INFORMATION";
    }
}
