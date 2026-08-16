using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NINA.Core.Utility.Notification;
using ToastNotifications;
using ToastNotifications.Lifetime;
using NinaNotification = NINA.Core.Utility.Notification.Notification;

namespace Chatstronomy.NINA.Direct;

internal sealed record NinaNotificationRecord(
    DateTime Time,
    string Level,
    string Header,
    string Message);

/// <summary>
/// Observes the same lifetime supervisor N.I.N.A. uses for its popup toasts.
/// N.I.N.A. does not expose this as a plugin service, so reflection is isolated
/// here and failure simply disables popup forwarding without affecting N.I.N.A.
/// </summary>
internal sealed class NinaNotificationWatcher : IDisposable
{
    private readonly Action<NinaNotificationRecord> onNotification;
    private INotificationsLifetimeSupervisor? supervisor;

    internal NinaNotificationWatcher(Action<NinaNotificationRecord> onNotification)
    {
        this.onNotification = onNotification;
    }

    internal bool Start()
    {
        if (supervisor is not null)
        {
            return true;
        }
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(NinaNotification).TypeHandle);
            var notifier = typeof(NinaNotification).GetField(
                "notifier",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Notifier;
            if (notifier is null)
            {
                return false;
            }

            var field = typeof(Notifier).GetField(
                "_lifetimeSupervisor",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var lifetime = field?.GetValue(notifier) as INotificationsLifetimeSupervisor;
            if (lifetime is null)
            {
                typeof(Notifier).GetMethod(
                    "Configure",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(notifier, null);
                lifetime = field?.GetValue(notifier) as INotificationsLifetimeSupervisor;
            }
            if (lifetime is null)
            {
                return false;
            }

            lifetime.ShowNotificationRequested += NotificationShown;
            supervisor = lifetime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void Stop()
    {
        var lifetime = supervisor;
        supervisor = null;
        if (lifetime is not null)
        {
            lifetime.ShowNotificationRequested -= NotificationShown;
        }
    }

    public void Dispose() => Stop();

    private void NotificationShown(object? sender, ShowNotificationEventArgs args)
    {
        if (args.Notification is not CustomNotification notification
            || string.IsNullOrWhiteSpace(notification.Message))
        {
            return;
        }
        try
        {
            onNotification(new NinaNotificationRecord(
                notification.DateTime,
                Classify(notification.Color),
                notification.Header?.Trim() ?? string.Empty,
                notification.Message.Trim()));
        }
        catch
        {
            // Popup rendering must remain independent of chat forwarding.
        }
    }

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
