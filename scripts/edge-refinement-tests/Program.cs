extern alias PaperTodoApp;

using NativeBoundsPolicy = PaperTodoApp::PaperTodo.WindowNativeBoundsPolicy;
using AppEdge = PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using AppFrame = PaperTodoApp::PaperTodo.EdgeCapsulePresentationFrame;
using AppLayoutFacts = PaperTodoApp::PaperTodo.EdgeCapsuleLayoutFacts;
using AppLayoutService = PaperTodoApp::PaperTodo.EdgeCapsuleLayoutService;
using AppMonitor = PaperTodoApp::PaperTodo.MonitorGeometry;
using AppModel = PaperTodoApp::PaperTodo.EdgeCapsuleModel;
using AppPlacement = PaperTodoApp::PaperTodo.EdgeCapsulePlacement;
using AppProxyCandidate = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyCandidate;
using AppProxyPolicy = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using AppProxyRole = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyMemberRole;
using AppMotion = PaperTodoApp::PaperTodo.EdgeCapsuleMotion;
using AppRect = PaperTodoApp::PaperTodo.DeviceScreenRect;
using AppSlotState = PaperTodoApp::PaperTodo.EdgeCapsuleSlotState;
using AppState = PaperTodoApp::PaperTodo.EdgeCapsuleState;
using AppSurface = PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;
using AppTargetPlanner = PaperTodoApp::PaperTodo.EdgeCapsuleTargetPlanner;
using AppTransitionReason = PaperTodoApp::PaperTodo.EdgeCapsuleTransitionReason;
using AppVisualState = PaperTodoApp::PaperTodo.EdgeCapsuleVisualState;
using AppGestureState = PaperTodoApp::PaperTodo.EdgeCapsuleGestureState;
using AppOpenOrigin = PaperTodoApp::PaperTodo.EdgeCapsuleOpenOrigin;
using AppPreviewState = PaperTodoApp::PaperTodo.EdgeCapsulePreviewState;

namespace PaperTodo;

internal static class Program
{
    private static int Main()
    {
        CheckNativeBoundsFlags();
        CheckQueueProxyPolicy();
        CheckCompactRealHostLayout();

        var nodes = new[]
        {
            new EdgeCapsulePreviewCorridorNode(
                new DeviceScreenRect(0, 0, 400, 100),
                ConnectToPrevious: false),
            new EdgeCapsulePreviewCorridorNode(
                new DeviceScreenRect(350, 140, 400, 190),
                ConnectToPrevious: true)
        };

        Assert(
            EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(5, 50)),
            "owner rectangle should remain inside");
        Assert(
            EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(375, 120)),
            "the gap between adjacent real selections should stay in the transfer rectangle");
        Assert(
            EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(100, 130)),
            "ordinary empty space inside the transfer rectangle should receive the linger policy");
        Assert(
            !EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(400, 130)),
            "leaving the transfer rectangle must be an absolute exit");

        var separatedNodes = new[]
        {
            nodes[0],
            nodes[1] with { ConnectToPrevious = false }
        };
        Assert(
            !EdgeCapsulePreviewCorridor.Contains(
                separatedNodes,
                new DeviceScreenPoint(375, 120)),
            "a skipped non-interactive item must split transfer rectangles");

        Assert(
            EdgeCapsulePreviewExitPolicy.Resolve(
                transferRectangleContains: false,
                predictiveIntentEnabled: true) ==
            EdgeCapsulePreviewExitPolicyDecision.ImmediateClose,
            "prediction must never veto the transfer rectangle's hard boundary");
        Assert(
            EdgeCapsulePreviewExitPolicy.Resolve(
                transferRectangleContains: false,
                predictiveIntentEnabled: false) ==
            EdgeCapsulePreviewExitPolicyDecision.ImmediateClose,
            "fixed mode must close at the same hard boundary");
        Assert(
            EdgeCapsulePreviewExitPolicy.Resolve(
                transferRectangleContains: true,
                predictiveIntentEnabled: true) ==
            EdgeCapsulePreviewExitPolicyDecision.PredictiveWait,
            "prediction should run only inside the empty transfer rectangle");
        Assert(
            EdgeCapsulePreviewExitPolicy.Resolve(
                transferRectangleContains: true,
                predictiveIntentEnabled: false) ==
            EdgeCapsulePreviewExitPolicyDecision.FixedWait,
            "disabled prediction should use the fixed empty-rectangle wait");
        Assert(
            Math.Abs(EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds - 1000) < 0.001,
            "disabled prediction should wait exactly one second inside the empty rectangle");
        Assert(
            EdgeCapsulePreviewExitPolicy.StrongerCloseReason(
                EdgeCapsulePreviewCloseReason.NoTargetIntent,
                EdgeCapsulePreviewCloseReason.OutsideTransferRectangle) ==
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle,
            "a hard boundary must upgrade a queued no-target close");
        Assert(
            EdgeCapsulePreviewExitPolicy.StrongerCloseReason(
                EdgeCapsulePreviewCloseReason.OutsideTransferRectangle,
                EdgeCapsulePreviewCloseReason.NoTargetIntent) ==
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle,
            "a no-target deadline must never downgrade a queued hard-boundary close");
        Assert(
            !EdgeCapsulePreviewExitPolicy.EmptyRegionCanCancelQueuedClose(
                EdgeCapsulePreviewCloseReason.OutsideTransferRectangle,
                predictiveIntentEnabled: true,
                hasTargetIntent: true),
            "empty space and prediction must never revoke a confirmed hard boundary");
        Assert(
            !EdgeCapsulePreviewExitPolicy.EmptyRegionCanCancelQueuedClose(
                EdgeCapsulePreviewCloseReason.NoTargetIntent,
                predictiveIntentEnabled: false,
                hasTargetIntent: true),
            "fixed mode must not restart its completed one-second wait");
        Assert(
            EdgeCapsulePreviewExitPolicy.EmptyRegionCanCancelQueuedClose(
                EdgeCapsulePreviewCloseReason.NoTargetIntent,
                predictiveIntentEnabled: true,
                hasTargetIntent: true),
            "fresh target-directed intent may revoke only a predictive no-target close");

        CheckCorridorIntentPrediction();

        var overflowArea = new System.Windows.Rect(0, 0, 400, 300);
        const int overflowSlotCount = 12;
        const double overflowGap = 6;
        var overflowTop = PaperTodoApp::PaperTodo.EdgeCapsuleLayout.TopForIndex(
            10,
            PaperTodoApp::PaperTodo.EdgeCapsuleLayout.StartTopMargin,
            overflowArea,
            overflowSlotCount,
            overflowGap);
        var nextOverflowTop = PaperTodoApp::PaperTodo.EdgeCapsuleLayout.TopForIndex(
            11,
            PaperTodoApp::PaperTodo.EdgeCapsuleLayout.StartTopMargin,
            overflowArea,
            overflowSlotCount,
            overflowGap);
        Assert(
            overflowTop > overflowArea.Bottom,
            "overflow capsules must be allowed below the work area");
        Assert(
            Math.Abs(
                (nextOverflowTop - overflowTop) -
                PaperTodoApp::PaperTodo.EdgeCapsuleLayout.SlotHeight(overflowGap)) < 0.001,
            "overflow capsules must preserve normal slot spacing");

        Assert(
            !EdgeCapsuleNativeTransactionPolicy.RequiresCrossQueueGroup(
                new[] { "monitor-a:right", "monitor-a:right" }),
            "one physical queue must not receive a transaction group");
        Assert(
            EdgeCapsuleNativeTransactionPolicy.RequiresCrossQueueGroup(
                new[] { "monitor-a:right", "monitor-b:left" }),
            "two related physical queues must receive one transaction group");
        Assert(
            EdgeCapsuleNativeTransactionPolicy.ParticipatesInBatchOutcome(
                9,
                applyAttempted: false,
                retryWasPending: false,
                deferred: false),
            "an idle member of a cross-queue transaction must share failure");
        Assert(
            !EdgeCapsuleNativeTransactionPolicy.ParticipatesInBatchOutcome(
                0,
                applyAttempted: false,
                retryWasPending: false,
                deferred: false),
            "an unrelated idle presenter must not be charged for another queue");
        Assert(
            EdgeCapsuleNativeTransactionPolicy.CanRelease(
                9,
                transitionActive: false,
                retryPending: false,
                applyActive: false,
                hasPresentationWork: false),
            "a fully settled transaction group should release");
        Assert(
            !EdgeCapsuleNativeTransactionPolicy.CanRelease(
                9,
                transitionActive: true,
                retryPending: false,
                applyActive: false,
                hasPresentationWork: false),
            "an active transition must retain its transaction group");
        Assert(
            !EdgeCapsuleNativeTransactionPolicy.CanRelease(
                9,
                transitionActive: false,
                retryPending: true,
                applyActive: false,
                hasPresentationWork: false),
            "a retrying member must retain its transaction group");

        Console.WriteLine("Edge refinement checks passed.");
        return 0;
    }

    private static void CheckNativeBoundsFlags()
    {
        const uint baseFlags = 0x0214;
        var moveOnly = NativeBoundsPolicy.FlagsForChanges(
            baseFlags,
            positionChanged: true,
            sizeChanged: false);
        Assert(
            moveOnly == (baseFlags | NativeBoundsPolicy.SwpNoSize),
            "position-only bounds must explicitly preserve the current native size");

        var sizeOnly = NativeBoundsPolicy.FlagsForChanges(
            baseFlags,
            positionChanged: false,
            sizeChanged: true);
        Assert(
            sizeOnly == (baseFlags | NativeBoundsPolicy.SwpNoMove),
            "size-only bounds must explicitly preserve the current native position");

        var moveAndSize = NativeBoundsPolicy.FlagsForChanges(
            baseFlags,
            positionChanged: true,
            sizeChanged: true);
        Assert(
            moveAndSize == baseFlags,
            "a full bounds change must keep both native axes writable");

        var unchanged = NativeBoundsPolicy.FlagsForChanges(
            baseFlags,
            positionChanged: false,
            sizeChanged: false);
        Assert(
            unchanged == (baseFlags |
                NativeBoundsPolicy.SwpNoMove |
                NativeBoundsPolicy.SwpNoSize),
            "an unchanged rectangle must preserve both native axes");
    }

    private static void CheckQueueProxyPolicy()
    {
        const int rightWall = 1000;
        var startBounds = new AppRect(920, 100, rightWall, 180);
        var startHost = startBounds;
        var targetBounds = new AppRect(900, 320, rightWall, 440);
        var targetHost = targetBounds;

        var start = new AppFrame(
            true,
            AppSurface.DockedResting,
            startBounds,
            startHost,
            startBounds,
            AppEdge.Right,
            80,
            rightWall,
            1,
            1,
            20,
            1,
            1,
            false,
            true,
            false);
        var target = new AppFrame(
            true,
            AppSurface.DockedPreview,
            targetBounds,
            targetHost,
            targetBounds,
            AppEdge.Right,
            80,
            rightWall,
            1,
            1,
            20,
            1,
            1,
            false,
            true,
            false);
        var motion = AppMotion.Animate(AppTransitionReason.Preview, 200);
        var plan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                new AppProxyCandidate(
                    "paper-a",
                    "DISPLAY|right",
                    start,
                    target,
                    motion,
                    HostReady: true,
                    Topmost: true)
            });
        Assert(plan != null, "a preview geometry change should create one queue proxy plan");
        Assert(
            plan!.Envelope == new AppRect(900, 100, rightWall, 440),
            "the proxy envelope should be only the source/target union, not the work area");
        Assert(
            plan.Members.Count == 1 &&
            plan.Members[0].Role == AppProxyRole.OpeningPreview &&
            !plan.Members[0].DefersRealEndpoint,
            "preview opening should be represented as one queue member transition");
        var openingStart = AppProxyPolicy.SampleLogicalFrame(
            plan.Members[0],
            startedAtTimestamp: 0,
            durationMilliseconds: 200,
            nowTimestamp: 0);
        var openingMiddle = AppProxyPolicy.SampleLogicalFrame(
            plan.Members[0],
            startedAtTimestamp: 0,
            durationMilliseconds: 200,
            nowTimestamp: System.Diagnostics.Stopwatch.Frequency / 10);
        var openingEnd = AppProxyPolicy.SampleLogicalFrame(
            plan.Members[0],
            startedAtTimestamp: 0,
            durationMilliseconds: 200,
            nowTimestamp: System.Diagnostics.Stopwatch.Frequency / 5);
        Assert(
            openingStart.Bounds == start.Bounds &&
            openingStart.HostBounds == openingStart.Bounds &&
            openingMiddle.Bounds.Top > start.Bounds.Top &&
            openingMiddle.Bounds.Top < target.Bounds.Top &&
            openingMiddle.HostBounds == openingMiddle.Bounds &&
            openingEnd.HostBounds == openingEnd.Bounds &&
            openingEnd == target,
            "proxy samples must keep compact host geometry while easing exactly from source to target");

        var movingStart = start with
        {
            Bounds = new AppRect(920, 190, rightWall, 270),
            HostBounds = new AppRect(920, 190, rightWall, 270),
            InteractiveBounds = new AppRect(920, 190, rightWall, 270)
        };
        var movingTarget = movingStart with
        {
            Bounds = new AppRect(920, 450, rightWall, 530),
            HostBounds = new AppRect(920, 450, rightWall, 530),
            InteractiveBounds = new AppRect(920, 450, rightWall, 530)
        };
        var queuePlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                new AppProxyCandidate(
                    "paper-a",
                    "DISPLAY|right",
                    start,
                    target,
                    motion,
                    HostReady: true,
                    Topmost: true),
                new AppProxyCandidate(
                    "paper-b",
                    "DISPLAY|right",
                    movingStart,
                    movingTarget,
                    motion,
                    HostReady: true,
                    Topmost: true)
            });
        Assert(
            queuePlan is { Members.Count: 2 } &&
            queuePlan.Members[1].Role == AppProxyRole.Moving &&
            !queuePlan.Members[1].DefersRealEndpoint &&
            queuePlan.Envelope == new AppRect(900, 100, rightWall, 530),
            "one queue proxy should group the preview owner and every translation-only peer");
        var unchangedPeerPlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                new AppProxyCandidate(
                    "paper-a",
                    "DISPLAY|right",
                    start,
                    target,
                    AppMotion.Animate(AppTransitionReason.Preview, 240),
                    HostReady: true,
                    Topmost: true),
                new AppProxyCandidate(
                    "paper-c",
                    "DISPLAY|right",
                    movingStart,
                    movingStart,
                    AppMotion.Animate(AppTransitionReason.Preview, 180),
                    HostReady: true,
                    Topmost: true)
            });
        Assert(
            unchangedPeerPlan is { Members.Count: 1, DurationMilliseconds: 240, Topmost: true },
            "unchanged queue members should be excluded while duration and z-order aggregate safely");

        var closePlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                new AppProxyCandidate(
                    "paper-a",
                    "DISPLAY|right",
                    target,
                    start,
                    motion,
                    HostReady: true,
                    Topmost: true)
            });
        Assert(
            closePlan is { Members.Count: 1 } &&
            closePlan.Members[0].Role == AppProxyRole.ClosingPreview &&
            closePlan.Members[0].DefersRealEndpoint,
            "a closing preview must retain its live real source until compositor handoff");
        var closingMiddle = AppProxyPolicy.SampleLogicalFrame(
            closePlan!.Members[0],
            startedAtTimestamp: 0,
            durationMilliseconds: 200,
            nowTimestamp: System.Diagnostics.Stopwatch.Frequency / 10);
        Assert(
            !closingMiddle.IsHitTestVisible &&
            closingMiddle.InteractiveBounds.IsEmpty,
            "an outgoing preview must stop owning input immediately while it animates out");

        Assert(
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    new AppProxyCandidate(
                        "paper-a",
                        "DISPLAY|right",
                        start,
                        target,
                        motion,
                        HostReady: true,
                        Topmost: true),
                    new AppProxyCandidate(
                        "paper-b",
                        "DISPLAY|right",
                        movingStart,
                        movingTarget with
                        {
                            Bounds = new AppRect(900, 450, rightWall, 550),
                            HostBounds = new AppRect(900, 450, rightWall, 550)
                        },
                        motion,
                        HostReady: true,
                        Topmost: true)
                }) == null,
            "a peer that changes shape cannot be wrapped as a translation-only live surface");
        Assert(
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    new AppProxyCandidate(
                        "paper-a",
                        "DISPLAY|right",
                        start,
                        target,
                        AppMotion.Snap(AppTransitionReason.Preview),
                        HostReady: true,
                        Topmost: true)
                }) == null,
            "snap transactions must never allocate a compositor proxy");
        Assert(
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    new AppProxyCandidate(
                        "paper-a",
                        "DISPLAY|right",
                        start,
                        target,
                        AppMotion.Animate(AppTransitionReason.Placement, 200),
                        HostReady: true,
                        Topmost: true)
                }) == null,
            "a transaction without a preview reason must stay on the existing presentation backend");

        var oversizedHostFrame = start with
        {
            HostBounds = new AppRect(
                start.Bounds.Left,
                start.Bounds.Top,
                start.Bounds.Right,
                start.Bounds.Bottom + 1)
        };
        Assert(
            !oversizedHostFrame.IsUsable &&
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    new AppProxyCandidate(
                        "paper-a", "DISPLAY|right", oversizedHostFrame, target,
                        motion, HostReady: true, Topmost: true)
                }) == null,
            "a real HostBounds envelope larger than Bounds must be structurally rejected");

        foreach (var rejected in new[]
                 {
                     new AppProxyCandidate(
                         "paper-a", "DISPLAY|right", start, target, motion,
                         HostReady: false, Topmost: true),
                     new AppProxyCandidate(
                         "paper-a", "OTHER|right", start, target, motion,
                         HostReady: true, Topmost: true),
                     new AppProxyCandidate(
                         "paper-a", "DISPLAY|right", start, target, motion,
                         HostReady: true, Topmost: false),
                     new AppProxyCandidate(
                         "paper-a", "DISPLAY|right", start,
                         target with { DpiScaleX = 1.25 }, motion,
                         HostReady: true, Topmost: true),
                     new AppProxyCandidate(
                         "paper-a", "DISPLAY|right", start,
                         target with { WallDeviceX = rightWall + 1 }, motion,
                         HostReady: true, Topmost: true),
                     new AppProxyCandidate(
                         "paper-a", "DISPLAY|right", start,
                         target with { Edge = AppEdge.Left }, motion,
                         HostReady: true, Topmost: true)
                 })
        {
            Assert(
                AppProxyPolicy.TryCreate("DISPLAY|right", new[] { rejected }) == null,
                "an incompatible host/queue/z-order/DPI/edge candidate must fall back safely");
        }

        var leftStart = start with
        {
            Bounds = new AppRect(0, 120, 80, 200),
            HostBounds = new AppRect(0, 120, 80, 200),
            InteractiveBounds = new AppRect(0, 120, 80, 200),
            Edge = AppEdge.Left,
            WallDeviceX = 0,
            DpiScaleX = 1.5,
            DpiScaleY = 1.5
        };
        var leftTarget = target with
        {
            Bounds = new AppRect(0, 260, 150, 440),
            HostBounds = new AppRect(0, 260, 150, 440),
            InteractiveBounds = new AppRect(0, 260, 150, 440),
            Edge = AppEdge.Left,
            WallDeviceX = 0,
            DpiScaleX = 1.5,
            DpiScaleY = 1.5
        };
        var leftPlan = AppProxyPolicy.TryCreate(
            "DISPLAY|left",
            new[]
            {
                new AppProxyCandidate(
                    "paper-left", "DISPLAY|left", leftStart, leftTarget,
                    motion, HostReady: true, Topmost: true)
            });
        Assert(
            leftPlan is { Edge: AppEdge.Left, WallDeviceX: 0 } &&
            leftPlan.Envelope == new AppRect(0, 120, 150, 440) &&
            Math.Abs(leftPlan.DpiScaleX - 1.5) < 0.001,
            "left-edge proxy geometry must remain wall-pinned at non-100% DPI");
    }

    private static void CheckCompactRealHostLayout()
    {
        var facts = new AppLayoutFacts(
            new AppMonitor(
                "DISPLAY-V2",
                new AppRect(0, 0, 1920, 1080),
                1,
                1),
            AppEdge.Right,
            new AppPlacement(
                Index: 2,
                VisualOffset: 0,
                SlotCount: 6),
            QueueStartTopMarginDip: 48,
            GapDip: 4,
            RestingWidthDip: 86,
            MaximumCloseWidthDip: 28,
            HeightDip: 58,
            PreviewWidthDip: 220,
            PreviewHeightDip: 140,
            CloseSegmentActsAsContent: false,
            RestingContentOpacity: 1,
            ForcedContentOpacity: null);
        var resting = AppLayoutService.Calculate(facts);
        var model = new AppModel(
            new AppState(
                AppSlotState.CollapsedDocked,
                AppVisualState.Resting,
                AppGestureState.Idle,
                AppOpenOrigin.Normal),
            facts.Placement,
            DragSession: null,
            ContextMenuOpen: false,
            PeerReorderActive: false,
            AppPreviewState.Closed,
            PointerOverSurface: false,
            DockedDragTopDipOverride: null);
        var target = AppTargetPlanner.Calculate(model, resting).Docked;
        Assert(
            target.HostBounds == target.Bounds,
            "the production planner must give the real HWND only its current visible endpoint");
        var previewTarget = AppTargetPlanner.Calculate(
            model with { Preview = AppPreviewState.Open },
            resting).Docked;
        var hoverTarget = AppTargetPlanner.Calculate(
            model with
            {
                State = model.State with { Visual = AppVisualState.Hovered }
            },
            resting).Docked;
        var displacedLayout = AppLayoutService.Calculate(
            facts with
            {
                Placement = facts.Placement with { TopOffsetDip = 180 }
            });
        var displacedTarget = AppTargetPlanner.Calculate(model, displacedLayout).Docked;
        Assert(
            previewTarget.HostBounds == previewTarget.Bounds &&
            hoverTarget.HostBounds == hoverTarget.Bounds &&
            displacedTarget.HostBounds == displacedTarget.Bounds &&
            displacedTarget.Bounds.Top > target.Bounds.Top,
            "preview, hover, and displaced peers must all keep endpoint-sized real hosts");

        var leftFacts = facts with
        {
            Monitor = new AppMonitor(
                "DISPLAY-V2-LEFT",
                new AppRect(-2560, 0, 0, 1440),
                1.25,
                1.25),
            Edge = AppEdge.Left
        };
        var leftLayout = AppLayoutService.Calculate(leftFacts);
        var leftTarget = AppTargetPlanner.Calculate(model, leftLayout).Docked;
        Assert(
            leftTarget.HostBounds == leftTarget.Bounds &&
            leftTarget.Bounds.Left == leftTarget.WallDeviceX,
            "compact endpoint hosts must remain correct on a scaled left-side monitor");
    }

    private static void CheckCorridorIntentPrediction()
    {
        var predictor = new EdgeCapsuleHoverIntentPredictor();
        var sensitivities = new[]
        {
            EdgeCapsuleHoverIntentSensitivities.VeryHigh,
            EdgeCapsuleHoverIntentSensitivities.High,
            EdgeCapsuleHoverIntentSensitivities.Medium,
            EdgeCapsuleHoverIntentSensitivities.Low,
            EdgeCapsuleHoverIntentSensitivities.VeryLow
        };
        var expectedWaits = new[] { 200.0, 350.0, 500.0, 650.0, 800.0 };
        for (var index = 0; index < sensitivities.Length; index++)
        {
            var wait = predictor.CorridorNoTargetIntentCloseMilliseconds(
                sensitivities[index]);
            Assert(
                Math.Abs(wait - expectedWaits[index]) < 0.001,
                $"unexpected no-target wait for {sensitivities[index]}");

            predictor.Reset(
                new DeviceScreenPoint(200, 120),
                TimestampAtMilliseconds(0),
                1,
                1);
            var target = new[] { new DeviceScreenRect(350, 140, 400, 190) };
            Assert(
                predictor.EvaluateCorridorExit(
                    sensitivities[index],
                    target,
                    new DeviceScreenPoint(200, 120),
                    wait - 1) ==
                EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent,
                "a settled empty-region pointer should wait for its full sensitivity deadline");
            Assert(
                predictor.EvaluateCorridorExit(
                    sensitivities[index],
                    target,
                    new DeviceScreenPoint(200, 120),
                    wait) ==
                EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent,
                "a settled empty-region pointer should close at its sensitivity deadline");
        }

        var bounds = new[] { new DeviceScreenRect(350, 100, 400, 160) };
        predictor.Reset(
            new DeviceScreenPoint(100, 120),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(200, 120),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(200, 120),
                500) == EdgeCapsuleCorridorExitDecision.KeepAlive,
            "a coherent trajectory toward a real capsule should keep the preview alive");

        var distantBounds = new[]
        {
            new DeviceScreenRect(1000, 100, 1050, 160)
        };
        predictor.Reset(
            new DeviceScreenPoint(0, 120),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(5, 120),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                distantBounds,
                new DeviceScreenPoint(5, 120),
                500) == EdgeCapsuleCorridorExitDecision.KeepAlive,
            "a slow coherent ray toward a distant capsule must not be limited by a time horizon");

        predictor.Reset(
            new DeviceScreenPoint(300, 120),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(200, 120),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(200, 120),
                500) ==
            EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent,
            "motion that does not point toward a capsule must not extend the deadline");

        predictor.Reset(
            new DeviceScreenPoint(360, 120),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(340, 120),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(340, 120),
                0) ==
            EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent,
            "leaving a real capsule must start the no-target timer even inside target padding");
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(340, 120),
                500) ==
            EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent,
            "target padding must not keep an away-moving pointer alive past the deadline");

        predictor.Reset(
            new DeviceScreenPoint(341, 110),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(340, 120),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(340, 120),
                500) ==
            EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent,
            "a diagonal ray leaving the real target must not be kept by padding at t=0");

        predictor.Reset(
            new DeviceScreenPoint(300, 89),
            TimestampAtMilliseconds(0),
            1,
            1);
        predictor.Observe(
            new DeviceScreenPoint(400, 90),
            TimestampAtMilliseconds(50),
            1,
            1);
        Assert(
            predictor.EvaluateCorridorExit(
                EdgeCapsuleHoverIntentSensitivities.Medium,
                bounds,
                new DeviceScreenPoint(400, 90),
                500) ==
            EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent,
            "the exclusive right edge plus padding must not turn an outward ray into target intent");
    }

    private static long TimestampAtMilliseconds(double milliseconds) =>
        (long)Math.Round(
            milliseconds / 1000 * System.Diagnostics.Stopwatch.Frequency,
            MidpointRounding.AwayFromZero);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
