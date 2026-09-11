using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Chatstronomy.NINA.Sequencing;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace Chatstronomy.NINA.Tests;

internal static class TargetSchedulerSequencingTests
{
    private static readonly Lazy<SchedulerTypes> Types = new(BuildTypes);

    internal static async Task RunAsync()
    {
        await ReplacementPlansAwaitTheSameTargetCommand();
        await ChangedTargetCannotConsumeThePreviousTargetsRequest();
        await FilterDeduplicationCannotCrossSchedulerTargets();
        await CachedRegistrationCannotAuthorizeInactiveOrParallelPlans();
    }

    private static async Task ReplacementPlansAwaitTheSameTargetCommand()
    {
        var fixture = new Fixture();
        var firstStarted = Signal();
        var finishFirst = Signal();
        var commandStarted = Signal();
        var finishCommand = Signal();
        var secondStarted = Signal();
        SchedulerFixtureContainer? first = null;
        SchedulerFixtureContainer? second = null;
        fixture.Scheduler.ExecuteBody = async (progress, token) =>
        {
            first = fixture.Plan();
            first.Add(new Exposure(async cancellation => { firstStarted.TrySetResult(); await finishFirst.Task.WaitAsync(cancellation); }));
            await first.Execute(progress, token); // TS deliberately bypasses PlanContainer.Run.
            second = fixture.Plan();
            second.Add(new Exposure(_ => { secondStarted.TrySetResult(); return Task.CompletedTask; }));
            await second.Execute(progress, token);
        };
        var running = fixture.Root.Run(new Progress<ApplicationStatus>(), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(first!.Status == SequenceEntityStatus.CREATED, "Actual scheduler plans remain CREATED while their native leaf runs.");
        Check(fixture.Trigger.Status == SequenceEntityStatus.CREATED, "TS has not initialized its ancestor trigger through the base strategy.");
        var completion = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, first, () => true,
            async (context, _, token) =>
            {
                Check(ReferenceEquals(context, second), "The callback must receive the new plan's exposure context.");
                Check(fixture.Root.GetCurrentRunningItems().Count == 0, "Trigger eligibility must not require the next exposure to be running already.");
                commandStarted.TrySetResult();
                await finishCommand.Task.WaitAsync(token);
            });
        finishFirst.TrySetResult();
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Target, second!.Target), "TS creates new plan and target instances for each exposure.");
        Check(!secondStarted.Task.IsCompleted && !completion.IsCompleted, "The next TS exposure awaits the entire queued command.");
        finishCommand.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Check(secondStarted.Task.IsCompleted && !fixture.Coordinator.IsBusy, "The scheduler resumes after the command finishes.");
        Check(!fixture.Coordinator.HasActiveTrigger(SequenceCommandKind.Autofocus, fixture.Root, second!),
            "A finished scheduler cannot keep a dynamically registered trigger active.");
    }

    private static async Task ChangedTargetCannotConsumeThePreviousTargetsRequest()
    {
        foreach (var change in new[] { "target", "project", "coordinates", "rotation" })
        {
            var fixture = new Fixture();
            var first = fixture.Plan();
            var active = new Exposure(_ => Task.CompletedTask) { Status = SequenceEntityStatus.RUNNING };
            first.Add(active);
            fixture.Root.Status = fixture.Scheduler.Status = SequenceEntityStatus.RUNNING;
            fixture.Root.AddRunningItem(active);
            var invoked = false;
            var completion = fixture.Coordinator.Queue(SequenceCommandKind.Autofocus, fixture.Root, first, () => true,
                (_, _, _) => { invoked = true; return Task.CompletedTask; });
            fixture.Root.RemoveRunningItem(active);
            var nextPlan = fixture.Plan(change == "project" ? 12 : 11, change == "target" ? 23 : 22,
                change == "coordinates" ? 7 : 6, change == "rotation" ? 30 : 0);
            var next = new Exposure(_ => Task.CompletedTask);
            nextPlan.Add(next);
            Check(!fixture.Trigger.ShouldTrigger(null!, next), "Changed scheduler identity must revoke the pending command.");
            await ExpectFailure(completion);
            Check(!invoked && !fixture.Coordinator.IsBusy, "Target replacement cannot redirect the pending operation.");
        }
    }

    private static async Task FilterDeduplicationCannotCrossSchedulerTargets()
    {
        var fixture = new Fixture(filterChange: true);
        fixture.Root.Status = fixture.Scheduler.Status = SequenceEntityStatus.RUNNING;
        var first = fixture.Plan();
        var active = new Exposure(_ => Task.CompletedTask) { Status = SequenceEntityStatus.RUNNING };
        first.Add(active);
        fixture.Root.AddRunningItem(active);
        Task Queue(ISequenceContainer scope) => fixture.Coordinator.Queue(SequenceCommandKind.ChangeFilter,
            fixture.Root, scope, () => true, (_, _, _) => throw new Exception("A stale filter request executed."),
            deduplicationKey: "2");
        var completion = Queue(first);
        fixture.Root.RemoveRunningItem(active);
        var replacement = fixture.Plan();
        var current = new Exposure(_ => Task.CompletedTask) { Status = SequenceEntityStatus.RUNNING };
        replacement.Add(current);
        fixture.Root.AddRunningItem(current);
        Check(ReferenceEquals(completion, Queue(replacement)), "The same target and filter shares the existing request across scheduler plans.");
        fixture.Root.RemoveRunningItem(current);
        var differentTarget = fixture.Plan(targetId: 23);
        var next = new Exposure(_ => Task.CompletedTask) { Status = SequenceEntityStatus.RUNNING };
        differentTarget.Add(next);
        fixture.Root.AddRunningItem(next);
        Reject(() => Queue(differentTarget));
        fixture.Root.RemoveRunningItem(next);
        Check(!fixture.Trigger.ShouldTrigger(null!, next), "A matching filter key does not preserve a request for the previous scheduler target.");
        await ExpectFailure(completion);
    }

    private static async Task CachedRegistrationCannotAuthorizeInactiveOrParallelPlans()
    {
        var fixture = new Fixture();
        var activePlan = fixture.Plan();
        var active = new Exposure(_ => Task.CompletedTask) { Status = SequenceEntityStatus.RUNNING };
        activePlan.Add(active);
        fixture.Root.Status = fixture.Scheduler.Status = SequenceEntityStatus.RUNNING;
        fixture.Root.AddRunningItem(active);
        Check(fixture.Coordinator.HasActiveTrigger(SequenceCommandKind.Autofocus, fixture.Root, activePlan), "Discover an actual running scheduler's trigger.");
        var inactivePlan = fixture.Plan();
        Reject(() => fixture.Queue(inactivePlan));
        fixture.Scheduler.Target = activePlan.Target;
        fixture.Trigger.Status = SequenceEntityStatus.DISABLED;
        Reject(() => fixture.Queue(activePlan));
        fixture.Trigger.Status = SequenceEntityStatus.CREATED;
        var parallel = new ParallelContainer();
        activePlan.Add(parallel);
        activePlan.Remove(active);
        parallel.Add(active);
        Reject(() => fixture.Queue(activePlan));
        parallel.Remove(active);
        activePlan.Add(active);
        var nestedTarget = new SchedulerFixtureContainer { Target = activePlan.Target };
        activePlan.Add(nestedTarget);
        activePlan.Remove(active);
        nestedTarget.Add(active);
        Reject(() => fixture.Queue(nestedTarget));
        nestedTarget.Remove(active);
        activePlan.Remove(nestedTarget);
        activePlan.Add(active);
        var completion = fixture.Queue(activePlan);
        fixture.Coordinator.Invalidate("Sequence profile changed.");
        await ExpectFailure(completion);
        fixture.Root.RemoveRunningItem(active);
        Reject(() => fixture.Queue(activePlan));
        fixture.Root.AddRunningItem(active);
        completion = fixture.Queue(activePlan);
        fixture.Trigger.AttachNewParent(null!);
        await ExpectFailure(completion);
        Check(!fixture.Coordinator.IsBusy, "A detached dynamically discovered trigger revokes its request.");
    }

    private sealed class Fixture
    {
        internal SequenceRootContainer Root { get; } = new();
        internal SchedulerFixtureContainer Scheduler { get; }
        internal ChatstronomyCommandTrigger Trigger { get; }
        internal SequenceCommandCoordinator Coordinator { get; }
        private readonly IProfileService profileService;
        private readonly IProfile profile;

        internal Fixture(bool filterChange = false)
        {
            var profileId = Guid.NewGuid();
            profile = Fake<IProfile>((method, _) => method.Name == "get_Id" ? profileId : null);
            profileService = Fake<IProfileService>((method, _) => method.Name == "get_ActiveProfile" ? profile : null);
            Scheduler = (SchedulerFixtureContainer)Activator.CreateInstance(Types.Value.Scheduler)!;
            Field(Scheduler, "profileService", profileService);
            Root.Add(Scheduler);
            Trigger = filterChange ? new ChatstronomyFilterChangeTrigger(profileService) : new ChatstronomyAutofocusTrigger(profileService);
            Scheduler.Add(Trigger);
            Coordinator = SequenceCommandCoordinator.For(profileService);
        }

        internal SchedulerFixtureContainer Plan(int projectId = 11, int targetId = 22, double ra = 6, double angle = 0)
        {
            var target = (InputTarget)RuntimeHelpers.GetUninitializedObject(typeof(InputTarget));
            typeof(InputTarget).GetField("inputCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target,
                new InputCoordinates { Coordinates = new Coordinates(ra, 30, Epoch.J2000, Coordinates.RAType.Hours) });
            typeof(InputTarget).GetField("deepSkyObject", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, Fake<IDeepSkyObject>((_, _) => null));
            target.TargetName = "Scheduler target";
            target.PositionAngle = angle;
            Scheduler.Target = target;
            var project = Data(Types.Value.Project, ("DatabaseId", projectId));
            var planTarget = Data(Types.Value.Target, ("DatabaseId", targetId), ("Project", project),
                ("Name", target.TargetName), ("Coordinates", target.InputCoordinates.Coordinates.Clone()), ("Rotation", angle));
            var plan = (SchedulerFixtureContainer)Activator.CreateInstance(Types.Value.Plan)!;
            plan.Target = target;
            Field(plan, "parentContainer", Scheduler);
            Field(plan, "profileService", profileService);
            Field(plan, "activeProfile", profile);
            Field(plan, "plan", Data(Types.Value.SchedulerPlan, ("PlanTarget", planTarget)));
            plan.AttachNewParent(Scheduler);
            return plan;
        }

        internal Task Queue(ISequenceContainer scope) => Coordinator.Queue(SequenceCommandKind.Autofocus, Root, scope,
            () => true, (_, _, _) => Task.CompletedTask);
    }

    private static SchedulerTypes BuildTypes()
    {
        // Reproduce the inspected Target Scheduler 5.9.6 reflection contract
        // without distributing or running any third-party plugin binary.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NINA.Plugin.TargetScheduler"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("SchedulerFixture");
        var projectContract = Contract(module, "NINA.Plugin.TargetScheduler.Planning.Interfaces.IProject", [("DatabaseId", typeof(int))]);
        var targetFields = new (string, Type)[] { ("DatabaseId", typeof(int)), ("Project", projectContract), ("Name", typeof(string)),
            ("Coordinates", typeof(Coordinates)), ("Rotation", typeof(double)) };
        var targetContract = Contract(module, "NINA.Plugin.TargetScheduler.Planning.Interfaces.ITarget", targetFields);
        var project = DataType(module, "FixtureProject", [("DatabaseId", typeof(int))], projectContract);
        var target = DataType(module, "FixtureTarget", targetFields, targetContract);
        var schedulerPlan = DataType(module, "NINA.Plugin.TargetScheduler.Planning.SchedulerPlan", [("PlanTarget", targetContract)]);
        var schedulerBuilder = module.DefineType("NINA.Plugin.TargetScheduler.Sequencer.TargetSchedulerContainer", TypeAttributes.Public, typeof(SchedulerFixtureContainer));
        schedulerBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        schedulerBuilder.DefineField("profileService", typeof(IProfileService), FieldAttributes.Private);
        var scheduler = schedulerBuilder.CreateType()!;
        var planBuilder = module.DefineType("NINA.Plugin.TargetScheduler.Sequencer.PlanContainer", TypeAttributes.Public, typeof(SchedulerFixtureContainer));
        planBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        planBuilder.DefineField("parentContainer", scheduler, FieldAttributes.Private);
        planBuilder.DefineField("profileService", typeof(IProfileService), FieldAttributes.Private);
        planBuilder.DefineField("activeProfile", typeof(IProfile), FieldAttributes.Private);
        planBuilder.DefineField("plan", schedulerPlan, FieldAttributes.Private);
        return new(scheduler, planBuilder.CreateType()!, schedulerPlan, target, project);
    }

    private static Type Contract(ModuleBuilder module, string name, (string Name, Type Type)[] properties)
    {
        var builder = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        foreach (var property in properties)
        {
            var getter = builder.DefineMethod("get_" + property.Name, MethodAttributes.Public | MethodAttributes.SpecialName
                | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot, property.Type, Type.EmptyTypes);
            builder.DefineProperty(property.Name, PropertyAttributes.None, property.Type, null).SetGetMethod(getter);
        }
        return builder.CreateType()!;
    }

    private static Type DataType(ModuleBuilder module, string name, (string Name, Type Type)[] properties, Type? contract = null)
    {
        var builder = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        if (contract is not null) builder.AddInterfaceImplementation(contract);
        foreach (var property in properties)
        {
            var field = builder.DefineField("_" + property.Name, property.Type, FieldAttributes.Private);
            var getter = builder.DefineMethod("get_" + property.Name, MethodAttributes.Public | MethodAttributes.SpecialName
                | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot, property.Type, Type.EmptyTypes);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var setter = builder.DefineMethod("set_" + property.Name, MethodAttributes.Public | MethodAttributes.SpecialName, null, [property.Type]);
            il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            var definition = builder.DefineProperty(property.Name, PropertyAttributes.None, property.Type, null);
            definition.SetGetMethod(getter); definition.SetSetMethod(setter);
            if (contract is not null) builder.DefineMethodOverride(getter, contract.GetProperty(property.Name)!.GetMethod!);
        }
        return builder.CreateType()!;
    }

    private sealed record SchedulerTypes(Type Scheduler, Type Plan, Type SchedulerPlan, Type Target, Type Project);
    private static void Field(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object Data(Type type, params (string Name, object Value)[] values)
    {
        var result = Activator.CreateInstance(type)!;
        foreach (var value in values) type.GetProperty(value.Name)!.SetValue(result, value.Value);
        return result;
    }
    private sealed class Exposure(Func<CancellationToken, Task> execute) : SequenceItem, IExposureItem
    {
        public double ExposureTime { get; set; } = 1;
        public int Gain { get; set; }
        public int Offset { get; set; }
        public string ImageType { get; set; } = "LIGHT";
        public BinningMode Binning { get; set; } = new(1, 1);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => execute(token);
        public override object Clone() => new Exposure(execute);
    }
    public class Proxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args)
            ?? (method!.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
    }
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Func<Task> operation)
    {
        try { operation(); } catch (InvalidOperationException) { return; }
        throw new Exception("Inactive or ambiguous scheduler context was accepted.");
    }
    private static async Task ExpectFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is not TimeoutException || task.IsFaulted) { return; }
        throw new Exception("A replaced scheduler target did not receive a terminal failure.");
    }
}

// Public base so Reflection.Emit can derive known-named TS stand-ins. Only
// native sequencer execution is exercised; no hardware/services are started.
public class SchedulerFixtureContainer : SequentialContainer, IDeepSkyObjectContainer
{
    public InputTarget Target { get; set; } = null!;
    public NighttimeData NighttimeData => null!;
    public Func<IProgress<ApplicationStatus>, CancellationToken, Task>? ExecuteBody { get; set; }
    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        ExecuteBody?.Invoke(progress, token) ?? base.Execute(progress, token);
}
