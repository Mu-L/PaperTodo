namespace PaperTodo;

internal readonly record struct EdgeCapsuleQueueMember(
    PaperData Paper,
    string QueueKey);

internal readonly record struct EdgeCapsuleQueuePageRequest(
    int RequestedPage,
    int MaximumVisualSlots)
{
    public static EdgeCapsuleQueuePageRequest Unpaged => new(0, int.MaxValue);

    public EdgeCapsuleQueuePageRequest Normalize() => new(
        Math.Max(0, RequestedPage),
        Math.Max(1, MaximumVisualSlots));
}

internal sealed record EdgeCapsuleQueue(
    string Key,
    IReadOnlyList<PaperData> Papers,
    IReadOnlyList<PaperData> VisiblePapers,
    bool HasMaster,
    int PageIndex,
    int PageCount,
    int PageStart,
    int PageCapacity,
    double TopOffsetDip = 0);

internal sealed class EdgeCapsuleQueuePlan
{
    public EdgeCapsuleQueuePlan(
        IReadOnlyList<EdgeCapsuleQueue> queues,
        IReadOnlyDictionary<string, EdgeCapsulePlacement> placements)
    {
        Queues = queues;
        Placements = placements;
    }

    public IReadOnlyList<EdgeCapsuleQueue> Queues { get; }
    public IReadOnlyDictionary<string, EdgeCapsulePlacement> Placements { get; }
}

internal sealed class EdgeCapsuleArrangeGate
{
    public bool HasPending { get; private set; }
    private bool Animate { get; set; }

    public void Defer(bool animate)
    {
        HasPending = true;
        Animate |= animate;
    }

    public bool Consume(bool animate)
    {
        var result = animate || Animate;
        Clear();
        return result;
    }

    public void Clear()
    {
        HasPending = false;
        Animate = false;
    }
}

/// <summary>
/// Pure queue planner. AppController decides membership; this coordinator is the sole owner of
/// per-queue indices, master offsets and slot counts.
/// </summary>
internal static class EdgeCapsuleQueueCoordinator
{
    public static EdgeCapsuleQueuePlan Build(
        IEnumerable<EdgeCapsuleQueueMember> members,
        bool showMaster) =>
        Build(
            members,
            showMaster,
            pageRequests: null);

    public static EdgeCapsuleQueuePlan Build(
        IEnumerable<EdgeCapsuleQueueMember> members,
        bool showMaster,
        IReadOnlyDictionary<string, EdgeCapsuleQueuePageRequest>? pageRequests)
    {
        var queueMembers = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var queueOrder = new List<string>();
        foreach (var member in members)
        {
            if (!queueMembers.TryGetValue(member.QueueKey, out var papers))
            {
                papers = new List<PaperData>();
                queueMembers[member.QueueKey] = papers;
                queueOrder.Add(member.QueueKey);
            }
            papers.Add(member.Paper);
        }

        var queues = new List<EdgeCapsuleQueue>(queueOrder.Count);
        var placements = new Dictionary<string, EdgeCapsulePlacement>(StringComparer.Ordinal);
        foreach (var key in queueOrder)
        {
            var papers = queueMembers[key];
            var request = pageRequests != null &&
                pageRequests.TryGetValue(key, out var requestedPage)
                    ? requestedPage.Normalize()
                    : EdgeCapsuleQueuePageRequest.Unpaged;
            var maximumVisualSlots = request.MaximumVisualSlots;
            var needsOverflowMaster = !showMaster && papers.Count > maximumVisualSlots;
            var hasMaster = papers.Count > 0 && (showMaster || needsOverflowMaster);
            var visualOffset = hasMaster ? 1 : 0;
            var pageCapacity = Math.Max(1, maximumVisualSlots - visualOffset);
            var pageCount = papers.Count == 0
                ? 0
                : 1 + (papers.Count - 1) / pageCapacity;
            var pageIndex = pageCount == 0
                ? 0
                : Math.Clamp(request.RequestedPage, 0, pageCount - 1);
            var pageStart = pageIndex * pageCapacity;
            var visibleCount = Math.Min(
                pageCapacity,
                Math.Max(0, papers.Count - pageStart));
            IReadOnlyList<PaperData> visiblePapers = visibleCount == papers.Count
                ? papers
                : papers.GetRange(pageStart, visibleCount);
            var slotCount = Math.Max(1, visibleCount + visualOffset);
            queues.Add(new EdgeCapsuleQueue(
                key,
                papers,
                visiblePapers,
                hasMaster,
                pageIndex,
                pageCount,
                pageStart,
                pageCapacity));

            for (var index = 0; index < papers.Count; index++)
            {
                var pageVisible = index >= pageStart &&
                    index < pageStart + visibleCount;
                placements[papers[index].Id] = new EdgeCapsulePlacement(
                    index,
                    visualOffset,
                    slotCount,
                    TopOffsetDip: 0,
                    PageStartIndex: pageStart,
                    IsPageVisible: pageVisible);
            }
        }

        return new EdgeCapsuleQueuePlan(queues, placements);
    }
}
