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
        var hovered = Frame(
            AppSurface.DockedHovered,
            new AppRect(5000, 100, 5120, 158),
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
        Assert(plan.Members[0].RequiresStartSnapshot,
            "preview opening must freeze its start shell before endpoint mutation");

        // Real preview transactions stage Preview first and queue placement immediately after it.
        // The per-window merged Motion may therefore say Placement even though its immutable visual
        // contract is Resting->Preview. Admission must use that visual transition, not stale reason
        // metadata, or the entire V2.5 session silently falls back to direct HWND animation.
        var previewAfterPlacementMerge = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "opening-merged",
                    queue,
                    compact,
                    preview,
                    AppMotion.Animate(AppReason.Placement, 180),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(previewAfterPlacementMerge != null,
            "a visual preview opening must survive a later Placement motion merge");
        Assert(previewAfterPlacementMerge!.Members[0].RequiresStartSnapshot,
            "merged preview opening must still use snapshot morph ownership");

        var closeAfterPlacementMerge = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "closing-merged",
                    queue,
                    preview,
                    compact,
                    AppMotion.Animate(AppReason.Placement, 180),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(closeAfterPlacementMerge is { Members.Count: 1 } &&
               closeAfterPlacementMerge.Members[0].DefersRealEndpoint,
            "a visual preview close must survive a later Placement motion merge");

        var hoverPlan = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "hover",
                    queue,
                    compact,
                    hovered,
                    AppMotion.Animate(AppReason.Pointer, 120),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(hoverPlan != null, "pointer-driven compact resize was not admitted");
        Assert(hoverPlan!.Members.Count == 1, "hover morph must own exactly one shell");
        Assert(hoverPlan.Members[0].RequiresStartSnapshot,
            "hover resize must use the snapshot/endpoint morph path");
        Assert(hoverPlan.Members[0].UsesEndpointLayer,
            "hover resize must prepare a separate live endpoint layer");

        var ordinaryPlacement = AppPolicy.TryCreate(
            queue,
            new[]
            {
                new AppCandidate(
                    "placement",
                    queue,
                    compact,
                    compact with
                    {
                        Bounds = new AppRect(5020, 220, 5120, 278),
                        HostBounds = new AppRect(5020, 220, 5120, 278),
                        InteractiveBounds = new AppRect(5020, 220, 5120, 278)
                    },
                    AppMotion.Animate(AppReason.Placement, 180),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(ordinaryPlacement == null,
            "ordinary placement without preview pixels must stay on the existing backend");

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
