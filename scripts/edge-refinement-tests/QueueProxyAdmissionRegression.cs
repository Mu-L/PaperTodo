extern alias PaperTodoApp;

using EdgeCapsuleEdge = PaperTodoApp::PaperTodo.EdgeCapsuleEdge;
using EdgeCapsuleMotion = PaperTodoApp::PaperTodo.EdgeCapsuleMotion;
using EdgeCapsulePresentationFrame = PaperTodoApp::PaperTodo.EdgeCapsulePresentationFrame;
using EdgeCapsuleQueueProxyCandidate = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyCandidate;
using EdgeCapsuleQueueProxyPolicy = PaperTodoApp::PaperTodo.EdgeCapsuleQueueProxyPolicy;
using EdgeCapsuleSurfaceKind = PaperTodoApp::PaperTodo.EdgeCapsuleSurfaceKind;
using EdgeCapsuleTransitionReason = PaperTodoApp::PaperTodo.EdgeCapsuleTransitionReason;
using EdgeCapsuleVisualAuthority = PaperTodoApp::PaperTodo.EdgeCapsuleVisualAuthority;
using DeviceScreenRect = PaperTodoApp::PaperTodo.DeviceScreenRect;

internal static class QueueProxyAdmissionRegression
{
    public static void Run()
    {
        StableTranslationIsAdmitted();
        PureMorphStaysInWpf();
        DragOwnerCanRemainDirectWhilePeerMoves();
        FloatingCoverBlocksSecondOwner();
        HostCapacityChangeIsRejected();
    }

    private static void StableTranslationIsAdmitted()
    {
        var start = Frame(100, 100);
        var target = Frame(220, 220);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
            "queue",
            new[] { Candidate("a", start, target) });
        Require(
            plan is { Members.Count: 1 },
            "stable translation must enter DComp");
    }

    private static void PureMorphStaysInWpf()
    {
        var start = Frame(100, 100);
        var target = start with
        {
            Surface = EdgeCapsuleSurfaceKind.DockedPreview,
            Bounds = new DeviceScreenRect(
                4860,
                100,
                5120,
                280),
            InteractiveBounds = new DeviceScreenRect(
                4860,
                100,
                5120,
                280)
        };
        Require(
            EdgeCapsuleQueueProxyPolicy.TryCreate(
                "queue",
                new[] { Candidate("a", start, target) }) ==
            null,
            "pure morph must stay in WPF");
    }

    private static void
        DragOwnerCanRemainDirectWhilePeerMoves()
    {
        var ownerStart = Frame(100, 100);
        var ownerTarget = Frame(180, 180);
        var peerStart = Frame(300, 300);
        var peerTarget = Frame(420, 420);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
            "queue",
            new[]
            {
                Candidate(
                    "owner",
                    ownerStart,
                    ownerTarget,
                    retained: true,
                    authority:
                        EdgeCapsuleVisualAuthority.FloatingDrag),
                Candidate(
                    "peer",
                    peerStart,
                    peerTarget,
                    retained: true,
                    authority:
                        EdgeCapsuleVisualAuthority.QueueTranslation)
            });
        Require(
            plan is { Members.Count: 1 } &&
            plan.Members[0].PaperId == "peer",
            "direct owner must not reject peer translation");
    }

    private static void FloatingCoverBlocksSecondOwner()
    {
        Require(
            !EdgeCapsuleQueueProxyPolicy
                .AllowsQueueProxyOwnership(
                    EdgeCapsuleVisualAuthority.FloatingDrag) &&
            !EdgeCapsuleQueueProxyPolicy
                .AllowsQueueProxyOwnership(
                    EdgeCapsuleVisualAuthority.DockingOverlap),
            "floating/docking cover must block a second owner");
    }

    private static void HostCapacityChangeIsRejected()
    {
        var start = Frame(100, 100);
        var target = Frame(220, 220) with
        {
            HostBounds = new DeviceScreenRect(
                4600,
                220,
                5120,
                640)
        };
        Require(
            EdgeCapsuleQueueProxyPolicy.TryCreate(
                "queue",
                new[] { Candidate("a", start, target) }) ==
            null,
            "proxy must never resize live surface capacity");
    }

    private static EdgeCapsuleQueueProxyCandidate Candidate(
        string id,
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target,
        bool retained = false,
        EdgeCapsuleVisualAuthority authority =
            EdgeCapsuleVisualAuthority.RealDocked) => new(
        id,
        "queue",
        start,
        start,
        target,
        EdgeCapsuleMotion.Animate(
            EdgeCapsuleTransitionReason.Placement,
            200),
        HostReady: true,
        Topmost: true,
        retained,
        authority);

    private static EdgeCapsulePresentationFrame Frame(
        int top,
        int hostTop)
    {
        const int wall = 5120;
        var bounds = new DeviceScreenRect(
            wall - 86,
            top,
            wall,
            top + 58);
        var host = new DeviceScreenRect(
            wall - 480,
            hostTop,
            wall,
            hostTop + 420);
        return new EdgeCapsulePresentationFrame(
            true,
            EdgeCapsuleSurfaceKind.DockedResting,
            bounds,
            host,
            bounds,
            EdgeCapsuleEdge.Right,
            68,
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

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
