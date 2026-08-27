using System.Linq.Expressions;
using System.Reflection;

namespace Chatstronomy.NINA.Direct;

/// <summary>
/// Optional bridge for the ImageSaveFailed event added after N.I.N.A. 3.2.
/// Reflection keeps the plugin loadable against the 3.2 contract while still
/// subscribing when the runtime mediator exposes the newer event.
/// </summary>
internal sealed record NinaImageSaveFailureRecord(
    string Stage,
    bool DiskFull,
    string Error);

internal sealed class NinaImageSaveFailureWatcher : IDisposable
{
    private readonly Action<NinaImageSaveFailureRecord> record;
    private readonly object gate = new();
    private object? source;
    private EventInfo? eventInfo;
    private Delegate? handler;
    private long subscriptionVersion;

    internal NinaImageSaveFailureWatcher(
        Action<NinaImageSaveFailureRecord> record)
    {
        this.record = record;
    }

    internal bool Start(object candidate)
    {
        lock (gate)
        {
            StopCore();
            try
            {
                var discoveredEvent = candidate.GetType().GetEvent(
                    "ImageSaveFailed",
                    BindingFlags.Instance | BindingFlags.Public);
                var handlerType = discoveredEvent?.EventHandlerType;
                var invoke = handlerType?.GetMethod("Invoke");
                var parameters = invoke?.GetParameters();
                if (discoveredEvent is null
                    || handlerType is null
                    || invoke?.ReturnType != typeof(Task)
                    || parameters is null
                    || parameters.Length != 2)
                {
                    return false;
                }

                var subscription = ++subscriptionVersion;
                var lambdaParameters = parameters
                    .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                    .ToArray();
                var callback = typeof(NinaImageSaveFailureWatcher).GetMethod(
                    nameof(HandleFailure),
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(nameof(HandleFailure));
                var body = Expression.Call(
                    Expression.Constant(this),
                    callback,
                    Expression.Convert(lambdaParameters[1], typeof(object)),
                    Expression.Constant(subscription));
                var discoveredHandler = Expression.Lambda(
                    handlerType,
                    body,
                    lambdaParameters).Compile();

                discoveredEvent.AddEventHandler(candidate, discoveredHandler);
                source = candidate;
                eventInfo = discoveredEvent;
                handler = discoveredHandler;
                return true;
            }
            catch (Exception)
            {
                // N.I.N.A. 3.2 has no event, and future builds may change its
                // delegate shape. A missing optional hook must never affect
                // image saving or the rest of plugin startup.
                StopCore();
                return false;
            }
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    private void StopCore()
    {
        subscriptionVersion++;
        if (source is not null && eventInfo is not null && handler is not null)
        {
            try
            {
                eventInfo.RemoveEventHandler(source, handler);
            }
            catch (Exception)
            {
                // The source may be unloading. The subscription version still
                // makes a late callback inert.
            }
        }
        source = null;
        eventInfo = null;
        handler = null;
    }

    private Task HandleFailure(object args, long subscription)
    {
        lock (gate)
        {
            if (subscription != subscriptionVersion || source is null)
            {
                return Task.CompletedTask;
            }
            try
            {
                var type = args.GetType();
                var stage = type.GetProperty("FailureStage")?.GetValue(args)?.ToString()
                    ?? "Unknown";
                var diskFull = type.GetProperty("IsDiskFull")?.GetValue(args) is true;
                var exception = type.GetProperty("Exception")?.GetValue(args) as Exception;
                record(new NinaImageSaveFailureRecord(
                    stage,
                    diskFull,
                    exception?.Message ?? "Image save failed."));
            }
            catch (Exception)
            {
                // Observing a failure must never add another failure to
                // N.I.N.A.'s image-save callback chain.
            }
        }
        return Task.CompletedTask;
    }
}
