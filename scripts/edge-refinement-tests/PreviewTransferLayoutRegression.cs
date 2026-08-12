extern alias PaperTodoApp;

using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static class PreviewTransferLayoutRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var papers = new[]
        {
            new PaperTodoApp::PaperTodo.PaperData { Id = "A" },
            new PaperTodoApp::PaperTodo.PaperData { Id = "B" },
            new PaperTodoApp::PaperTodo.PaperData { Id = "C" },
            new PaperTodoApp::PaperTodo.PaperData { Id = "D" },
            new PaperTodoApp::PaperTodo.PaperData { Id = "E" },
            new PaperTodoApp::PaperTodo.PaperData { Id = "F" }
        };

        var queue = new PaperTodoApp::PaperTodo.EdgeCapsuleQueue(
            "|right",
            papers,
            HasMaster: false);
        var placements = new Dictionary<
            string,
            PaperTodoApp::PaperTodo.EdgeCapsulePlacement>(StringComparer.Ordinal);
        for (var index = 0; index < papers.Length; index++)
        {
            placements[papers[index].Id] =
                new PaperTodoApp::PaperTodo.EdgeCapsulePlacement(
                    index,
                    VisualOffset: 0,
                    SlotCount: papers.Length);
        }

        var plan = new PaperTodoApp::PaperTodo.EdgeCapsuleQueuePlan(
            new[] { queue },
            placements);
        const double compactHeight =
            PaperTodoApp::PaperTodo.PaperLayoutDefaults.CapsuleHeight;
        const double gap = 4;

        var dSession =
            PaperTodoApp::PaperTodo.EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                plan,
                previous: null,
                queueKey: "|right",
                ownerPaperId: "D",
                size: new PaperTodoApp::PaperTodo.EdgeCapsulePreviewSize(220, 190),
                compactHeightDip: compactHeight,
                gapDip: gap)
            ?? throw new InvalidOperationException("D preview layout should open.");

        var dTailOffset = dSession.TopOffsetsDip["E"];
        if (dTailOffset <= 0 ||
            Math.Abs(dSession.TopOffsetsDip["F"] - dTailOffset) > 0.001)
        {
            throw new InvalidOperationException(
                "D preview should displace E/F together before the upward-transfer check.");
        }

        var cSession =
            PaperTodoApp::PaperTodo.EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                plan,
                previous: dSession,
                queueKey: "|right",
                ownerPaperId: "C",
                size: new PaperTodoApp::PaperTodo.EdgeCapsulePreviewSize(220, 120),
                compactHeightDip: compactHeight,
                gapDip: gap)
            ?? throw new InvalidOperationException("C preview layout should open.");

        var compactedOffset = cSession.TopOffsetsDip["D"];
        if (compactedOffset >= dTailOffset - 0.001)
        {
            throw new InvalidOperationException(
                "D→C must reclaim space released by the old D preview.");
        }
        if (Math.Abs(cSession.TopOffsetsDip["E"] - compactedOffset) > 0.001 ||
            Math.Abs(cSession.TopOffsetsDip["F"] - compactedOffset) > 0.001)
        {
            throw new InvalidOperationException(
                "D→C must compact E/F with D instead of retaining the old D-preview gap.");
        }
    }
}
