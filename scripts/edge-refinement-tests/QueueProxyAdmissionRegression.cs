extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppCandidate = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyCandidate;
using AppEdge = PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppFrame = PaperTodoApp::PaperTodo.EdgeCapsulePresentationFrame;
using AppMotion = PaperTodoApp::PaperTodo.EdgeCapsuleMotion;
using AppPolicy = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using AppRect = PaperTodoApp::PaperTodo.DeviceScreenRect;
using AppReason = PaperTodoApp::PaperTodo.EdgeCapsuleTransitionReason;
using AppSurface = PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;

namespace PaperTodo;

internal static class QueueProxyAdmissionRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string queue = "display|right";
        var compact = Frame(
            AppSurface.DockedResting,
            new AppRect(5020, 100, 5120, 158),
            bodyWidth: 100);
        var preview = Frame(
            AppSurface.DockedPreview,
            new AppRect(4800, 100, 5120, 300),
            bodyWidth: 280);

        var plan = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "opening",
                    queue,
                    compact,
                    preview,
                    AppMotion.Animate(AppReason.Preview, 180),
                    HostReady: true,
                    Topmost: true),
                // This member has no visual change. Its no-op bookkeeping must not disable the
                // compositor for the opening member.
                new AppCandidate(
                    "unchanged",
                    queue,
                    compact,
                    compact,
                    AppMotion.Snap(AppReason.Placement),
                    HostReady: false,
                    Topmost: false)
            });
        Assert(plan != null, "unchanged queue bookkeeping vetoed a valid preview proxy");
        Assert(plan!.Members.Count == 1, "unchanged member entered the compositor ownership set");
        Assert(plan.Members[0].PaperId == "opening", "opening member was not retained");

        var rejected = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "opening",
                    queue,
                    compact,
                    preview,
                    AppMotion.Snap(AppReason.Preview),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(rejected == null, "a changed snap member must not enter compositor animation");
    }

    private static AppFrame Frame(
        AppSurface surface,
        AppRect bounds,
        int bodyWidth) => new(
        Visible: true,
        Surface: surface,
        Bounds: bounds,
        HostBounds: bounds,
        InteractiveBounds: bounds,
        Edge: AppEdge.Right,
        BodyWindowWidthDevice: bodyWidth,
        WallDeviceX: 5120,
        DpiScaleX: 1,
        DpiScaleY: 1,
        MaximumCloseWidthDip: 40,
        Opacity: 1,
        ContentOpacity: 1,
        OutlineVisible: false,
        IsHitTestVisible: true,
        CloseSegmentActsAsContent: false);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
