extern alias PaperTodoApp;

namespace PaperTodo;

internal static class Program
{
    private static int Main()
    {
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
                new DeviceScreenPoint(5, 50),
                10,
                10),
            "owner rectangle should remain inside");
        Assert(
            EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(375, 120),
                10,
                10),
            "adjacent wall-side bridge should connect");
        Assert(
            !EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(100, 130),
                10,
                10),
            "empty area inside the old bounding box must close");
        Assert(
            !EdgeCapsulePreviewCorridor.Contains(
                nodes,
                new DeviceScreenPoint(330, 120),
                10,
                10),
            "bridge must stay narrow");

        var separatedNodes = new[]
        {
            nodes[0],
            nodes[1] with { ConnectToPrevious = false }
        };
        Assert(
            !EdgeCapsulePreviewCorridor.Contains(
                separatedNodes,
                new DeviceScreenPoint(375, 120),
                10,
                10),
            "a skipped non-interactive item must break corridor adjacency");

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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
