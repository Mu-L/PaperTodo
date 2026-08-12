extern alias PaperTodoApp;

using NativeBoundsPolicy = PaperTodoApp::PaperTodo.WindowNativeBoundsPolicy;

namespace PaperTodo;

internal static class Program
{
    private static int Main()
    {
        CheckNativeBoundsFlags();

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
