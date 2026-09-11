using System.Reflection;
using NINA.Astrometry;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;

namespace Chatstronomy.NINA.Sequencing;

/// <summary>
/// Target Scheduler 5.9 creates a new PlanContainer and InputTarget for each
/// exposure. Only its persistent scheduler container and database target IDs
/// identify the same target across those native exposure boundaries.
/// </summary>
internal static class TargetSchedulerCommandContext
{
    private const string AssemblyName = "NINA.Plugin.TargetScheduler";
    private const string PlanTypeName = "NINA.Plugin.TargetScheduler.Sequencer.PlanContainer";
    private const string SchedulerTypeName = "NINA.Plugin.TargetScheduler.Sequencer.TargetSchedulerContainer";
    private const string SchedulerPlanTypeName = "NINA.Plugin.TargetScheduler.Planning.SchedulerPlan";
    private const string TargetInterfaceName = "NINA.Plugin.TargetScheduler.Planning.Interfaces.ITarget";
    private const string ProjectInterfaceName = "NINA.Plugin.TargetScheduler.Planning.Interfaces.IProject";

    internal static bool IsSchedulerScope(ISequenceContainer scope) => IsKnownType(scope.GetType(), SchedulerTypeName);

    internal static ISequenceContainer NormalizeScope(ISequenceContainer context)
    {
        var plan = FindPlan(context);
        return plan is null ? context : GetOwner(plan);
    }

    /// <summary>
    /// Does not decide whether a plan is running. Admission separately proves a
    /// live root-owned instruction; execution is called by the native trigger
    /// before that next instruction enters the running-items list.
    /// </summary>
    internal static Snapshot? TryCapture(ISequenceContainer context)
    {
        var plan = FindPlan(context);
        if (plan is null)
        {
            if (SequenceCommandCoordinator.Ancestors(context).Any(IsSchedulerScope))
                throw Unavailable();
            return null;
        }

        try
        {
            var owner = GetOwner(plan);
            var assembly = plan.GetType().Assembly;
            var schedulerPlan = ReadField(plan, "plan");
            if (schedulerPlan is null || !IsKnownType(schedulerPlan.GetType(), SchedulerPlanTypeName))
                throw Unavailable();
            var planTarget = schedulerPlan.GetType().GetProperty("PlanTarget", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(schedulerPlan);
            var project = ReadInterfaceProperty(planTarget, assembly, TargetInterfaceName, "Project");
            var targetId = ReadInterfaceProperty(planTarget, assembly, TargetInterfaceName, "DatabaseId");
            var projectId = ReadInterfaceProperty(project, assembly, ProjectInterfaceName, "DatabaseId");
            var plannedCoordinates = ReadInterfaceProperty(planTarget, assembly, TargetInterfaceName, "Coordinates") as Coordinates;
            var plannedRotation = ReadInterfaceProperty(planTarget, assembly, TargetInterfaceName, "Rotation");
            var plannedName = ReadInterfaceProperty(planTarget, assembly, TargetInterfaceName, "Name") as string;
            var profileService = ReadField(owner, "profileService") as IProfileService;
            if (targetId is not int targetKey || targetKey <= 0 || projectId is not int projectKey || projectKey <= 0
                || profileService is null || !ReferenceEquals(profileService, ReadField(plan, "profileService"))
                || ReadField(plan, "activeProfile") is not IProfile planProfile
                || !ReferenceEquals(planProfile, profileService.ActiveProfile)
                || owner is not IDeepSkyObjectContainer targetOwner || plan is not IDeepSkyObjectContainer planOwner)
                throw Unavailable();

            var target = targetOwner.Target;
            var coordinates = target?.InputCoordinates?.Coordinates;
            if (target is null || !ReferenceEquals(target, planOwner.Target) || target.DeepSkyObject is null
                || plannedCoordinates is null || coordinates is null || plannedRotation is not double rotation
                || !double.IsFinite(coordinates.RA) || coordinates.RA < 0 || coordinates.RA >= 24
                || !double.IsFinite(coordinates.Dec) || coordinates.Dec < -90 || coordinates.Dec > 90
                || !double.IsFinite(rotation) || !coordinates.RA.Equals(plannedCoordinates.RA)
                || !coordinates.Dec.Equals(plannedCoordinates.Dec) || coordinates.Epoch != plannedCoordinates.Epoch
                // InputTarget's public setter normalizes to [0, 360).
                || !target.PositionAngle.Equals(AstroUtil.EuclidianModulus(rotation, 360))
                || !string.Equals(target.TargetName, plannedName, StringComparison.Ordinal))
                throw Unavailable();

            return new Snapshot(owner, profileService, planProfile.Id, projectKey, targetKey, target);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or MemberAccessException or TargetInvocationException)
        {
            throw Unavailable(exception);
        }
    }

    private static ISequenceContainer? FindPlan(ISequenceContainer context)
    {
        var nearerTarget = false;
        foreach (var ancestor in SequenceCommandCoordinator.Ancestors(context))
        {
            if (IsKnownType(ancestor.GetType(), PlanTypeName))
            {
                if (nearerTarget)
                    throw new InvalidOperationException(
                        "A nested target overrides the Target Scheduler plan. Commands cannot be queued across that target boundary.");
                return ancestor;
            }
            // Do not silently replace a user's more specific native target
            // with the scheduler's target, even when the coordinates match.
            nearerTarget |= ancestor is IDeepSkyObjectContainer;
        }
        return null;
    }

    private static ISequenceContainer GetOwner(ISequenceContainer plan)
    {
        var owner = ReadField(plan, "parentContainer") as ISequenceContainer;
        if (owner is null || !IsSchedulerScope(owner) || !ReferenceEquals(plan.Parent, owner)
            || owner.GetType().Assembly != plan.GetType().Assembly)
            throw Unavailable();
        return owner;
    }

    private static bool IsKnownType(Type type, string name) =>
        string.Equals(type.FullName, name, StringComparison.Ordinal)
        && string.Equals(type.Assembly.GetName().Name, AssemblyName, StringComparison.Ordinal);

    private static object? ReadField(object source, string name) =>
        source.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(source);

    private static object? ReadInterfaceProperty(object? source, Assembly assembly, string interfaceName, string propertyName)
    {
        var contract = assembly.GetType(interfaceName, throwOnError: false);
        if (source is null || source.GetType().Assembly != assembly || contract is null || !contract.IsInstanceOfType(source))
            return null;
        return contract.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
    }

    private static InvalidOperationException Unavailable(Exception? exception = null) =>
        new("The active Target Scheduler plan cannot be identified safely. Wait for a target exposure and try again.", exception);

    internal sealed record Snapshot(ISequenceContainer Owner, IProfileService ProfileService, Guid ProfileId,
        int ProjectId, int TargetId, InputTarget Target)
    {
        internal bool HasSameIdentity(Snapshot other) => ReferenceEquals(Owner, other.Owner)
            && ReferenceEquals(ProfileService, other.ProfileService) && ProfileId == other.ProfileId
            && ProjectId == other.ProjectId && TargetId == other.TargetId;
    }
}
