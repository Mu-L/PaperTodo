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
using AppAuthority = PaperTodoApp::PaperTodo.EdgeCapsuleVisualAuthority;

namespace PaperTodo;

internal static class Program
{
    private static int Main()
    {
        CheckNativeBoundsFlags();
        CheckQueueProxyPolicy();
        CheckCompactRealHostLayout();
        QueueProxyAdmissionRegression.Run();
        QueueProxyBarrierRegression.Run();
        QueueProxyConcealHandoffRegression.Run();
        QueueProxyNativeClipRegression.Run();
        QueueProxyWallAnchorRegression.Run();

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
        const int wall = 1000;
        var compact = Frame(
            top: 100,
            visibleWidth: 86,
            visibleHeight: 58,
            hostWidth: 260,
            hostHeight: 180,
            wall: wall);
        var preview = compact with
        {
            Surface = AppSurface.DockedPreview,
            Bounds = new AppRect(
                wall - 220,
                100,
                wall,
                240),
            InteractiveBounds = new AppRect(
                wall - 220,
                100,
                wall,
                240)
        };

        Assert(
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    Candidate(
                        "paper-a",
                        "DISPLAY|right",
                        compact,
                        preview,
                        AppMotion.Animate(
                            AppTransitionReason.Preview,
                            200))
                }) == null,
            "pure bounded-host morph must stay in WPF");

        var moved = compact with
        {
            Bounds = new AppRect(
                compact.Bounds.Left,
                300,
                compact.Bounds.Right,
                358),
            HostBounds = new AppRect(
                compact.HostBounds.Left,
                300,
                compact.HostBounds.Right,
                480),
            InteractiveBounds = new AppRect(
                compact.Bounds.Left,
                300,
                compact.Bounds.Right,
                358)
        };
        var movePlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                Candidate(
                    "paper-a",
                    "DISPLAY|right",
                    compact,
                    moved,
                    AppMotion.Animate(
                        AppTransitionReason.Placement,
                        200))
            });
        Assert(
            movePlan is { Members.Count: 1 } &&
            movePlan.Members[0].Role ==
                AppProxyRole.MovingSource,
            "stable host translation must enter DComp");

        var movingAndMorphing = moved with
        {
            Surface = AppSurface.DockedPreview,
            Bounds = new AppRect(
                wall - 220,
                300,
                wall,
                440),
            InteractiveBounds = new AppRect(
                wall - 220,
                300,
                wall,
                440)
        };
        var combinedPlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                Candidate(
                    "paper-a",
                    "DISPLAY|right",
                    compact,
                    movingAndMorphing,
                    AppMotion.Animate(
                        AppTransitionReason.Preview,
                        200))
            });
        Assert(
            combinedPlan is { Members.Count: 1 },
            "translation plus WPF morph must keep one live surface");

        var directOwner = Candidate(
            "owner",
            "DISPLAY|right",
            compact,
            moved,
            AppMotion.Animate(
                AppTransitionReason.Drag,
                200),
            retained: true,
            authority: AppAuthority.FloatingDrag);
        var peerStart = compact with
        {
            Bounds = new AppRect(
                compact.Bounds.Left,
                500,
                compact.Bounds.Right,
                558),
            HostBounds = new AppRect(
                compact.HostBounds.Left,
                500,
                compact.HostBounds.Right,
                680),
            InteractiveBounds = new AppRect(
                compact.Bounds.Left,
                500,
                compact.Bounds.Right,
                558)
        };
        var peerTarget = peerStart with
        {
            Bounds = new AppRect(
                peerStart.Bounds.Left,
                620,
                peerStart.Bounds.Right,
                678),
            HostBounds = new AppRect(
                peerStart.HostBounds.Left,
                620,
                peerStart.HostBounds.Right,
                800),
            InteractiveBounds = new AppRect(
                peerStart.Bounds.Left,
                620,
                peerStart.Bounds.Right,
                678)
        };
        var mixedPlan = AppProxyPolicy.TryCreate(
            "DISPLAY|right",
            new[]
            {
                directOwner,
                Candidate(
                    "peer",
                    "DISPLAY|right",
                    peerStart,
                    peerTarget,
                    AppMotion.Animate(
                        AppTransitionReason.Placement,
                        200),
                    retained: true,
                    authority:
                        AppAuthority.QueueTranslation)
            });
        Assert(
            mixedPlan is { Members.Count: 1 } &&
            mixedPlan.Members[0].PaperId == "peer",
            "retained direct owner must be partially revealed " +
            "without rejecting peer translation");

        var changedCapacity = moved with
        {
            HostBounds = new AppRect(
                moved.HostBounds.Left - 10,
                moved.HostBounds.Top,
                moved.HostBounds.Right,
                moved.HostBounds.Bottom)
        };
        Assert(
            AppProxyPolicy.TryCreate(
                "DISPLAY|right",
                new[]
                {
                    Candidate(
                        "paper-a",
                        "DISPLAY|right",
                        compact,
                        changedCapacity,
                        AppMotion.Animate(
                            AppTransitionReason.Preview,
                            200))
                }) == null,
            "live surface capacity changes must remain direct");

        var leftStart = compact with
        {
            Bounds = new AppRect(0, 120, 86, 178),
            HostBounds = new AppRect(0, 120, 260, 300),
            InteractiveBounds =
                new AppRect(0, 120, 86, 178),
            Edge = AppEdge.Left,
            WallDeviceX = 0,
            DpiScaleX = 1.5,
            DpiScaleY = 1.5
        };
        var leftTarget = leftStart with
        {
            Bounds = new AppRect(0, 320, 86, 378),
            HostBounds = new AppRect(0, 320, 260, 500),
            InteractiveBounds =
                new AppRect(0, 320, 86, 378)
        };
        var leftPlan = AppProxyPolicy.TryCreate(
            "DISPLAY|left",
            new[]
            {
                Candidate(
                    "paper-left",
                    "DISPLAY|left",
                    leftStart,
                    leftTarget,
                    AppMotion.Animate(
                        AppTransitionReason.Placement,
                        200))
            });
        Assert(
            leftPlan is
                { Edge: AppEdge.Left, WallDeviceX: 0 },
            "left-wall translation must remain wall pinned");
    }

    private static AppFrame Frame(
        int top,
        int visibleWidth,
        int visibleHeight,
        int hostWidth,
        int hostHeight,
        int wall)
    {
        var bounds = new AppRect(
            wall - visibleWidth,
            top,
            wall,
            top + visibleHeight);
        var host = new AppRect(
            wall - hostWidth,
            top,
            wall,
            top + hostHeight);
        return new AppFrame(
            true,
            AppSurface.DockedResting,
            bounds,
            host,
            bounds,
            AppEdge.Right,
            Math.Max(1, visibleWidth - 18),
            wall,
            1,
            1,
            18,
            1,
            1,
            false,
            true,
            false);
    }

    private static AppProxyCandidate Candidate(
        string paperId,
        string queueKey,
        AppFrame start,
        AppFrame target,
        AppMotion motion,
        bool hostReady = true,
        bool topmost = true,
        bool retained = false,
        AppFrame? source = null,
        AppAuthority authority =
            AppAuthority.RealDocked) => new(
        paperId,
        queueKey,
        start,
        source ?? start,
        target,
        motion,
        hostReady,
        topmost,
        retained,
        authority);



    private static void CheckCompactRealHostLayout()
    {
        var facts = new AppLayoutFacts(
            new AppMonitor(
                "DISPLAY-V3-LITE",
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
            ForcedContentOpacity: null,
            HostCapacityWidthDip: 260,
            HostCapacityHeightDip: 180);
        var layout = AppLayoutService.Calculate(facts);
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

        var resting =
            AppTargetPlanner.Calculate(model, layout).Docked;
        var hover = AppTargetPlanner.Calculate(
            model with
            {
                State = model.State with
                {
                    Visual = AppVisualState.Hovered
                }
            },
            layout).Docked;
        var preview = AppTargetPlanner.Calculate(
            model with
            {
                Preview = AppPreviewState.Open
            },
            layout).Docked;
        Assert(
            resting.HostBounds.Width == 260 &&
            resting.HostBounds.Height == 180 &&
            resting.Bounds.Width < resting.HostBounds.Width &&
            resting.Bounds.Height < resting.HostBounds.Height,
            "resting shape must live inside bounded host");
        Assert(
            hover.HostBounds.Width ==
                resting.HostBounds.Width &&
            hover.HostBounds.Height ==
                resting.HostBounds.Height &&
            preview.HostBounds.Width ==
                resting.HostBounds.Width &&
            preview.HostBounds.Height ==
                resting.HostBounds.Height,
            "Rest/Hover/Preview must preserve host capacity");
        Assert(
            resting.Bounds.Right ==
                resting.HostBounds.Right &&
            preview.Bounds.Right ==
                preview.HostBounds.Right,
            "right-wall host and shape must share the wall");

        var displacedLayout = AppLayoutService.Calculate(
            facts with
            {
                Placement = facts.Placement with
                {
                    TopOffsetDip = 180
                }
            });
        var displaced = AppTargetPlanner.Calculate(
            model,
            displacedLayout).Docked;
        Assert(
            displaced.HostBounds.Width ==
                resting.HostBounds.Width &&
            displaced.HostBounds.Top >
                resting.HostBounds.Top,
            "queue placement changes only bounded-host position");

        var leftFacts = facts with
        {
            Monitor = new AppMonitor(
                "DISPLAY-V3-LITE-LEFT",
                new AppRect(-2560, 0, 0, 1440),
                1.25,
                1.25),
            Edge = AppEdge.Left
        };
        var left = AppTargetPlanner.Calculate(
            model,
            AppLayoutService.Calculate(leftFacts)).Docked;
        Assert(
            left.Bounds.Left == left.WallDeviceX &&
            left.HostBounds.Left == left.WallDeviceX,
            "scaled left-wall bounded host must stay pinned");
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
