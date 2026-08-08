using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

internal readonly record struct EdgeCapsulePreviewSize(
    double WidthDip,
    double HeightDip)
{
    public const double MinimumWidthDip = 240;
    public const double MaximumWidthDip = 480;
    public const double MinimumHeightDip = 120;
    public const double MaximumHeightDip = 420;

    public EdgeCapsulePreviewSize Normalize(double maximumWidthDip, double maximumHeightDip)
    {
        var maxWidth = Math.Max(
            MinimumWidthDip,
            Math.Min(MaximumWidthDip, maximumWidthDip));
        var maxHeight = Math.Max(
            MinimumHeightDip,
            Math.Min(MaximumHeightDip, maximumHeightDip));
        return new EdgeCapsulePreviewSize(
            Math.Clamp(
                double.IsFinite(WidthDip) ? WidthDip : MinimumWidthDip,
                MinimumWidthDip,
                maxWidth),
            Math.Clamp(
                double.IsFinite(HeightDip) ? HeightDip : MinimumHeightDip,
                MinimumHeightDip,
                maxHeight));
    }
}

/// <summary>
/// Size is the complete visible card rectangle in DIPs, including the host-owned close segment.
/// The host normalizes it before CreateContent is invoked and freezes it for the preview session.
/// </summary>
internal sealed record EdgeCapsulePreviewDescriptor(
    EdgeCapsulePreviewSize Size,
    Func<EdgeCapsulePreviewSize, FrameworkElement> CreateContent);

internal sealed record EdgeCapsulePreviewRequest(
    EdgeCapsulePreviewSize Size,
    FrameworkElement Content);

internal sealed class EdgeCapsulePreviewInvalidationSource
{
    public event Action? Invalidated;

    public void Invalidate() => Invalidated?.Invoke();
}

internal sealed record EdgeCapsulePreviewContext(
    PaperData Paper,
    Func<string> ReadTitle,
    bool PaperExpanded,
    Func<string> ReadMarkdownText,
    Func<string, bool, bool> SetTodoDone,
    Func<Style> ReadTodoCheckStyle,
    Func<string> ReadPluginStatus,
    Action<string> OpenExternal,
    EdgeCapsulePreviewInvalidationSource InvalidationSource)
{
    public string Title => ReadTitle();
}

/// <summary>
/// Internal content seam for edge preview cards. Protocol 1.8 does not expose this yet: Todo and
/// Markdown can replace the default provider without changing queue, host or input code, and a
/// future plugin adapter can enter through the same descriptor.
/// </summary>
internal interface IEdgeCapsulePreviewProvider
{
    EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context);
}

internal static class EdgeCapsulePreviewInteraction
{
    public static readonly DependencyProperty ConsumesPointerProperty =
        DependencyProperty.RegisterAttached(
            "ConsumesPointer",
            typeof(bool),
            typeof(EdgeCapsulePreviewInteraction),
            new FrameworkPropertyMetadata(false));

    public static void SetConsumesPointer(DependencyObject element, bool value) =>
        element.SetValue(ConsumesPointerProperty, value);

    public static bool GetConsumesPointer(DependencyObject element) =>
        (bool)element.GetValue(ConsumesPointerProperty);
}

internal sealed class DefaultEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static DefaultEdgeCapsulePreviewProvider Instance { get; } = new();

    private DefaultEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var title = context.Title;
        var status = context.ReadPluginStatus();
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            title,
            status,
            minimum: 280,
            maximum: 440);
        var height = Math.Clamp(
            150 + EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                status,
                Math.Max(120, width - 48)) * AppTypography.Scale(20),
            160,
            280);

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new PluginFallbackEdgeCapsulePreviewView(context, size));
    }
}

internal sealed record EdgeCapsulePreviewLayoutSession(
    string QueueKey,
    string OwnerPaperId,
    EdgeCapsulePreviewSize Size,
    IReadOnlyList<string> QueuePaperIds,
    IReadOnlyDictionary<string, double> TopOffsetsDip);

/// <summary>
/// Pure preview placement policy. The compact queue remains the base plan. During one browsing
/// session only the owner has a non-standard height; transfers reuse the old preview space and move
/// the opposite side only when overlap makes it necessary. Full compaction happens only on exit.
/// </summary>
internal static class EdgeCapsulePreviewLayoutCoordinator
{
    public static EdgeCapsulePreviewLayoutSession? OpenOrTransfer(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession? previous,
        string queueKey,
        string ownerPaperId,
        EdgeCapsulePreviewSize size,
        double compactHeightDip,
        double gapDip)
    {
        var queue = basePlan.Queues.FirstOrDefault(item =>
            string.Equals(item.Key, queueKey, StringComparison.Ordinal));
        if (queue == null)
        {
            return null;
        }

        var papers = queue.Papers;
        var newIndex = IndexOf(papers, ownerPaperId);
        if (newIndex < 0)
        {
            return null;
        }

        var compactHeight = Math.Max(1, compactHeightDip);
        var gap = Math.Max(0, gapDip);
        var slotHeight = compactHeight + gap;
        var baseTops = papers
            .Select(paper =>
                basePlan.Placements[paper.Id].VisualIndex * slotHeight)
            .ToArray();
        var currentTops = baseTops.ToArray();

        var paperIds = papers.Select(paper => paper.Id).ToArray();
        var sameQueue = previous != null &&
            string.Equals(previous.QueueKey, queueKey, StringComparison.Ordinal) &&
            previous.QueuePaperIds.SequenceEqual(
                paperIds,
                StringComparer.Ordinal);
        var oldIndex = -1;
        if (sameQueue)
        {
            for (var index = 0; index < papers.Count; index++)
            {
                currentTops[index] += previous!.TopOffsetsDip
                    .GetValueOrDefault(papers[index].Id);
            }
            oldIndex = IndexOf(papers, previous!.OwnerPaperId);
        }

        var newHeight = Math.Max(compactHeight, size.HeightDip);
        var tops = currentTops.ToArray();

        // Accepted temporary 1.8 behavior: preview browsing preserves the queue-relative motion
        // even when a tall card or its followers extend beyond the monitor work area. Do not clamp
        // the card height or shrink the whole corridor here; that policy needs a separate design.

        if (oldIndex < 0)
        {
            tops[newIndex] = baseTops[newIndex];
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else if (newIndex > oldIndex)
        {
            // Moving downward: compact the released side, keep the first member below the target
            // where it currently is, and let the new card grow upward into the released space.
            for (var index = 0; index < newIndex; index++)
            {
                tops[index] = baseTops[index];
            }

            var nextTop = newIndex + 1 < papers.Count
                ? currentTops[newIndex + 1]
                : currentTops[newIndex] + compactHeight + gap;
            var proposedTop = nextTop - gap - newHeight;
            var anchoredTop = Math.Min(
                proposedTop,
                currentTops[newIndex]);
            var minimumTop = newIndex > 0
                ? tops[newIndex - 1] + compactHeight + gap
                : baseTops[newIndex];
            tops[newIndex] = Math.Max(anchoredTop, minimumTop);
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else if (newIndex < oldIndex)
        {
            // Moving upward: keep the upper side fixed and grow downward. Existing lower gaps are
            // retained; members only move when the new card would overlap them.
            var minimumTop = newIndex > 0
                ? tops[newIndex - 1] + compactHeight + gap
                : baseTops[newIndex];
            tops[newIndex] = Math.Max(currentTops[newIndex], minimumTop);
            PushFollowingMembers(
                tops,
                currentTops,
                newIndex,
                newHeight,
                compactHeight,
                gap);
        }
        else
        {
            return previous! with { Size = size };
        }

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < papers.Count; index++)
        {
            offsets[papers[index].Id] = tops[index] - baseTops[index];
        }

        return new EdgeCapsulePreviewLayoutSession(
            queueKey,
            ownerPaperId,
            size,
            paperIds,
            offsets);
    }

    public static EdgeCapsuleQueuePlan Apply(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession? session)
    {
        if (session == null)
        {
            return basePlan;
        }

        var placements = basePlan.Placements.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (paperId, offset) in session.TopOffsetsDip)
        {
            if (placements.TryGetValue(paperId, out var placement))
            {
                placements[paperId] = placement with { TopOffsetDip = offset };
            }
        }

        return new EdgeCapsuleQueuePlan(basePlan.Queues, placements);
    }

    private static void PushFollowingMembers(
        double[] tops,
        double[] currentTops,
        int ownerIndex,
        double ownerHeight,
        double compactHeight,
        double gap)
    {
        for (var index = ownerIndex + 1; index < tops.Length; index++)
        {
            var previousHeight = index - 1 == ownerIndex
                ? ownerHeight
                : compactHeight;
            var minimumTop = tops[index - 1] + previousHeight + gap;
            tops[index] = Math.Max(currentTops[index], minimumTop);
        }
    }

    private static int IndexOf(IReadOnlyList<PaperData> papers, string paperId)
    {
        for (var index = 0; index < papers.Count; index++)
        {
            if (string.Equals(
                    papers[index].Id,
                    paperId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }
}
