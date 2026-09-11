using Chatstronomy.NINA.Sequencing;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using System.Reflection;
using System.Runtime.CompilerServices;
using static Chatstronomy.NINA.Tests.CommandSafetyTests;

namespace Chatstronomy.NINA.Tests;

internal static class NativeTargetCommandTests
{
    internal static async Task RunAsync()
    {
        foreach (var kind in new[] { SequenceCommandKind.CenterTarget, SequenceCommandKind.CenterRotateTarget })
        foreach (var wasGuiding in new[] { false, true })
        {
            var target = (InputTarget)RuntimeHelpers.GetUninitializedObject(typeof(InputTarget));
            typeof(InputTarget).GetField("inputCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target,
                new InputCoordinates { Coordinates = new Coordinates(6, 30, Epoch.J2000, Coordinates.RAType.Hours) });
            typeof(InputTarget).GetField("deepSkyObject", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, Mock<IDeepSkyObject>());
            target.PositionAngle = 45;
            var context = new TargetContainer { Target = target };
            var centerCalls = 0;
            var solveCalls = 0;
            var slewCalls = 0;
            var rotationCalls = 0;
            var guideStarts = 0;
            var windowCloses = 0;
            var settings = Mock<IPlateSolveSettings>((method, _) => method.Name switch
            {
                "get_Binning" => (short)1,
                "get_NumberOfAttempts" => 1,
                "get_ExposureTime" => 2d,
                "get_RotationTolerance" => 0.1d,
                _ => null,
            });
            var profile = Mock<IProfile>((method, _) => method.Name switch
            {
                "get_PlateSolveSettings" => settings,
                "get_TelescopeSettings" => Mock<ITelescopeSettings>(),
                "get_CameraSettings" => Mock<ICameraSettings>(),
                "get_RotatorSettings" => Mock<IRotatorSettings>(),
                _ => null,
            });
            var telescope = Mock<ITelescopeMediator>((method, args) =>
            {
                if (method.Name == "GetInfo") return new TelescopeInfo { Connected = true };
                if (method.Name == "SlewToCoordinatesAsync")
                {
                    CheckPosition((Coordinates)args![0]!);
                    slewCalls++;
                    return Task.FromResult(true);
                }
                return null;
            });
            var center = Mock<ICenteringSolver>((method, args) =>
            {
                if (method.Name != "Center") return null;
                CheckPosition(((CenterSolveParameter)args![1]!).Coordinates);
                centerCalls++;
                return Task.FromResult(new PlateSolveResult { Success = true });
            });
            var capture = Mock<ICaptureSolver>((method, args) =>
            {
                if (method.Name != "Solve") return null;
                CheckPosition(((CaptureSolverParameter)args![1]!).Coordinates);
                solveCalls++;
                return Task.FromResult(new PlateSolveResult { Success = true, PositionAngle = solveCalls == 1 ? 30 : 45 });
            });
            var solverFactory = Mock<IPlateSolverFactory>((method, _) => method.Name switch
            {
                "GetCenteringSolver" => center,
                "GetCaptureSolver" => capture,
                _ => null,
            });
            var rotator = Mock<IRotatorMediator>((method, args) =>
            {
                if (method.Name == "GetInfo") return new RotatorInfo { Connected = true };
                if (method.Name == "GetTargetPosition") return args![0];
                if (method.Name == "MoveRelative")
                {
                    if (Math.Abs((float)args![0]! - 15f) > 0.001f) throw new Exception("Rotation did not use the local target angle");
                    rotationCalls++;
                    return Task.FromResult(45f);
                }
                return null;
            });
            var guider = Mock<IGuiderMediator>((method, _) =>
            {
                if (method.Name == "StopGuiding") return Task.FromResult(wasGuiding);
                if (method.Name == "StartGuiding") { guideStarts++; return Task.FromResult(true); }
                return null;
            });
            var window = Mock<IWindowService>((method, _) =>
            {
                if (method.Name == "DelayedClose") windowCloses++;
                return null;
            });
            var helper = new NativeTargetCommands(
                Mock<IProfileService>((method, _) => method.Name == "get_ActiveProfile" ? profile : null),
                telescope, Mock<IImagingMediator>(), rotator, Mock<IFilterWheelMediator>(), guider,
                Mock<IDomeMediator>((method, _) => method.Name == "GetInfo" ? new DomeInfo() : null),
                Mock<IDomeFollower>(), solverFactory,
                Mock<IWindowServiceFactory>((method, _) => method.Name == "Create" ? window : null));
            await helper.ExecuteAsync(kind, context, new Progress<ApplicationStatus>(), CancellationToken.None);
            var rotate = kind == SequenceCommandKind.CenterRotateTarget;
            if (centerCalls != 1 || slewCalls != (rotate ? 2 : 1)
                || rotationCalls != (rotate ? 1 : 0) || solveCalls != (rotate ? 2 : 0)
                || guideStarts != (wasGuiding ? 1 : 0) || windowCloses != 1)
                throw new Exception($"Native {kind} did not complete its solver, guiding and window lifecycle");
        }
    }

    private static void CheckPosition(Coordinates position)
    {
        if (position.RA != 6 || position.Dec != 30 || position.Epoch != Epoch.J2000)
            throw new Exception("Native centering did not use the locally snapshotted target");
    }

    private sealed class TargetContainer : SequentialContainer, IDeepSkyObjectContainer
    {
        public InputTarget Target { get; set; } = null!;
        public NighttimeData NighttimeData => null!;
    }
}
