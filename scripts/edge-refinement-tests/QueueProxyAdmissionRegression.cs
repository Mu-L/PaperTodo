extern alias PaperTodoApp;

using System.Runtime.CompilerServices;
using AppCandidate =
    PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyCandidate;
using AppEdge =
    PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppFrame =
    PaperTodoApp::PaperTodo.EdgeCapsulePresentationFrame;
using AppMotion =
    PaperTodoApp::PaperTodo.EdgeCapsuleMotion;
using AppPolicy =
    PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using AppRect =
    PaperTodoApp::PaperTodo.DeviceScreenRect;
using AppReason =
    PaperTodoApp::PaperTodo.EdgeCapsuleTransitionReason;
using AppSurface =
    PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;

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
            bodyWidth: 120);
        var preview = Frame(
            AppSurface.DockedPreview,
            new AppRect(4800, 100, 5120, 300),
            bodyWidth: 280);

        var plan = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "opening",
                    queue,
                    compact,
                    compact,
                    preview,
                    AppMotion.Animate(AppReason.Preview, 180)),
                // An unrelated no-op member cannot veto the first proxy.
                Candidate(
                    "unchanged",
                    queue,
                    compact,
                    compact,
                    compact,
                    AppMotion.Snap(AppReason.Placement),
                    hostReady: false,
                    topmost: false)
            });
        Assert(plan is { Members.Count: 1 },
            "unretained no-op bookkeeping vetoed a valid preview proxy");
        Assert(plan!.Members[0].RequiresStartSnapshot,
            "first native endpoint mutation must retain a 1:1 start cover");
        Assert(plan.Members[0].UsesTargetSurface,
            "opening must reveal the native target surface");

        var previewAfterPlacementMerge = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "opening-merged",
                    queue,
                    compact,
                    compact,
                    preview,
                    AppMotion.Animate(AppReason.Placement, 180))
            });
        Assert(previewAfterPlacementMerge is { Members.Count: 1 },
            "visual preview opening must survive Placement motion merge");
        Assert(previewAfterPlacementMerge!.Members[0]
                .RequiresStartSnapshot,
            "merged initial opening must retain a 1:1 start cover");

        var stagedPreviewCompact = Frame(
            AppSurface.DockedPreview,
            new AppRect(5026, 185, 5120, 243),
            bodyWidth: 94);
        var stagedPreviewTarget = Frame(
            AppSurface.DockedPreview,
            new AppRect(4745, 185, 5120, 423),
            bodyWidth: 375);
        var stagedSurfaceOpening = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "opening-staged-surface",
                    queue,
                    stagedPreviewCompact,
                    stagedPreviewCompact,
                    stagedPreviewTarget,
                    AppMotion.Animate(AppReason.Placement, 200))
            });
        Assert(stagedSurfaceOpening is { Members.Count: 1 },
            "staged Preview compact geometry must enter compositor");
        Assert(stagedSurfaceOpening!.Members[0]
                .RequiresStartSnapshot,
            "staged initial endpoint mutation needs a 1:1 start cover");
        Assert(!stagedSurfaceOpening.Members[0].DefersRealEndpoint,
            "compact-to-full is reveal, not conceal");

        // A successor starts from the predecessor's sampled clip while the real HWND already owns
        // the full preview endpoint. It must reveal that source directly without a bitmap.
        var successorReveal = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "successor-reveal",
                    queue,
                    stagedPreviewCompact,
                    stagedPreviewTarget,
                    stagedPreviewTarget,
                    AppMotion.Animate(AppReason.Placement, 200),
                    retained: true)
            });
        Assert(successorReveal is { Members.Count: 1 },
            "successor reveal was rejected");
        Assert(!successorReveal!.Members[0].RequiresStartSnapshot,
            "successor native endpoint must not scale/capture a bitmap");
        Assert(successorReveal.Members[0].UsesTargetSurface,
            "successor must reveal the native target surface");

        var closeAfterPlacementMerge = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "closing-merged",
                    queue,
                    preview,
                    preview,
                    compact,
                    AppMotion.Animate(AppReason.Placement, 180))
            });
        Assert(closeAfterPlacementMerge is { Members.Count: 1 } &&
               closeAfterPlacementMerge.Members[0].DefersRealEndpoint,
            "preview close must conceal source before endpoint mutation");

        var intermediate = preview with
        {
            Bounds = new AppRect(4920, 100, 5120, 240),
            HostBounds = new AppRect(4920, 100, 5120, 240),
            InteractiveBounds = new AppRect(4920, 100, 5120, 240)
        };
        var reverse = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "reverse",
                    queue,
                    intermediate,
                    preview,
                    compact,
                    AppMotion.Animate(AppReason.Placement, 180),
                    retained: true)
            });
        Assert(reverse is { Members.Count: 1 } &&
               reverse.Members[0].DefersRealEndpoint,
            "mid-flight reverse must conceal the full-resolution source");

        var hoverPlan = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "hover",
                    queue,
                    compact,
                    compact,
                    hovered,
                    AppMotion.Animate(AppReason.Pointer, 120))
            });
        Assert(hoverPlan is { Members.Count: 1 },
            "pointer-driven compact reveal was not admitted");
        Assert(hoverPlan!.Members[0].RequiresStartSnapshot,
            "first pointer endpoint mutation needs a 1:1 start cover");
        Assert(hoverPlan.Members[0].UsesTargetSurface,
            "pointer reveal must animate native endpoint clip");

        // Every predecessor-owned no-op member must remain in the successor root. Otherwise root
        // replacement would make the capsule disappear while its real HWND remains cloaked.
        var retainedNoOp = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "opening",
                    queue,
                    compact,
                    compact,
                    preview,
                    AppMotion.Animate(AppReason.Preview, 180)),
                Candidate(
                    "retained-stationary",
                    queue,
                    compact,
                    compact,
                    compact,
                    AppMotion.Preserve(AppReason.Placement),
                    retained: true)
            });
        Assert(retainedNoOp is { Members.Count: 2 },
            "predecessor-owned stationary member was dropped");

        var ordinaryPlacement = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "placement",
                    queue,
                    compact,
                    compact,
                    compact with
                    {
                        Bounds = new AppRect(5020, 220, 5120, 278),
                        HostBounds = new AppRect(5020, 220, 5120, 278),
                        InteractiveBounds =
                            new AppRect(5020, 220, 5120, 278)
                    },
                    AppMotion.Animate(AppReason.Placement, 180))
            });
        Assert(ordinaryPlacement == null,
            "ordinary placement without preview/pointer pixels must stay on existing backend");

        var rejected = AppPolicy.TryCreate(
            queue,
            new[]
            {
                Candidate(
                    "opening-snap",
                    queue,
                    compact,
                    compact,
                    preview,
                    AppMotion.Snap(AppReason.Preview))
            });
        Assert(rejected == null,
            "changed Snap member must not enter compositor animation");
    }

    private static AppCandidate Candidate(
        string paperId,
        string queue,
        AppFrame start,
        AppFrame source,
        AppFrame target,
        AppMotion motion,
        bool hostReady = true,
        bool topmost = true,
        bool retained = false) => new(
        paperId,
        queue,
        start,
        source,
        target,
        motion,
        hostReady,
        topmost,
        retained);

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
