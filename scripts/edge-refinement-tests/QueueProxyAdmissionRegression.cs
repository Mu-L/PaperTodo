using PaperTodo;

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
        var start = Frame("a", 100, 86, 58, 100);
        var target = Frame("a", 220, 86, 58, 220);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
  "queue",
  new[] { Candidate("a", start, target) });
        Require(plan is { Members.Count: 1 }, "stable translation must enter DComp");
        Require(plan!.Members[0].Role == EdgeCapsuleQueueProxyMemberRole.MovingSource,
  "V3 Lite only permits MovingSource");
    }

    private static void PureMorphStaysInWpf()
    {
        var start = Frame("a", 100, 86, 58, 100);
        var target = Frame(
  "a",
  100,
  260,
  180,
  100,
  EdgeCapsuleSurfaceKind.DockedPreview);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
  "queue",
  new[] { Candidate("a", start, target) });
        Require(plan == null, "pure size/shape morph must stay in bounded WPF host");
    }

    private static void DragOwnerCanRemainDirectWhilePeerMoves()
    {
        var ownerStart = Frame("owner", 100, 86, 58, 100);
        var ownerTarget = Frame("owner", 180, 86, 58, 180);
        var peerStart = Frame("peer", 300, 86, 58, 300);
        var peerTarget = Frame("peer", 420, 86, 58, 420);
        var owner = Candidate(
  "owner",
  ownerStart,
  ownerTarget,
  EdgeCapsuleGestureState.DockedReordering,
  floatingCoverActive: true);
        var peer = Candidate("peer", peerStart, peerTarget);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate("queue", new[] { owner, peer });
        Require(plan is { Members.Count: 1 }, "peer translation must not be queue-wide rejected");
        Require(plan!.Members[0].PaperId == "peer", "drag owner must remain direct");
    }

    private static void FloatingCoverBlocksSecondOwner()
    {
        var start = Frame("a", 100, 86, 58, 100);
        var target = Frame("a", 220, 86, 58, 220);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
  "queue",
  new[]
  {
      Candidate(
          "a",
          start,
          target,
          EdgeCapsuleGestureState.Idle,
          floatingCoverActive: true)
  });
        Require(plan == null, "Gesture=Idle cannot override an active floating authority");
    }

    private static void HostCapacityChangeIsRejected()
    {
        var start = Frame("a", 100, 86, 58, 100, hostWidth: 480);
        var target = Frame("a", 220, 86, 58, 220, hostWidth: 500);
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
  "queue",
  new[] { Candidate("a", start, target) });
        Require(plan == null, "translation proxy must never resize its live surface");
    }

    private static EdgeCapsuleQueueProxyCandidate Candidate(
        string id,
        EdgeCapsulePresentationFrame start,
        EdgeCapsulePresentationFrame target,
        EdgeCapsuleGestureState gesture = EdgeCapsuleGestureState.Idle,
        bool floatingCoverActive = false) => new(
  id,
  "queue",
  start,
  start,
  target,
  EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Placement, 200),
  HostReady: true,
  Topmost: true,
  RetainedByCurrentProxy: false,
  gesture,
  floatingCoverActive);

    private static EdgeCapsulePresentationFrame Frame(
        string id,
        int top,
        int width,
        int height,
        int hostTop,
        EdgeCapsuleSurfaceKind surface = EdgeCapsuleSurfaceKind.DockedResting,
        int hostWidth = 480,
        int hostHeight = 420)
    {
        const int wall = 5120;
        var bounds = new DeviceScreenRect(wall - width, top, wall, top + height);
        var host = new DeviceScreenRect(
  wall - hostWidth,
  hostTop,
  wall,
  hostTop + hostHeight);
        return new EdgeCapsulePresentationFrame(
  true,
  surface,
  bounds,
  host,
  bounds,
  EdgeCapsuleEdge.Right,
  Math.Max(1, width - 18),
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
  throw new InvalidOperationException(message);
        }
    }
}
