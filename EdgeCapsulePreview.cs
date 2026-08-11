using System.Windows;

namespace PaperTodo;

internal readonly record struct EdgeCapsulePreviewSize(
    double WidthDip,
    double HeightDip)
{
    public const double MinimumWidthDip = 120;
    public const double MaximumWidthDip = 480;
    public const double MinimumHeightDip = 90;
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
    Func<EdgeCapsulePreviewSize, FrameworkElement> CreateContent,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    bool DeferContentCreation = false);

internal sealed record EdgeCapsulePreviewRequest(
    EdgeCapsulePreviewSize Size,
    FrameworkElement Content,
    Action<bool>? SetVisibility = null,
    Action? PrepareForActivation = null,
    Func<FrameworkElement>? CreateDeferredContent = null);



internal readonly record struct EdgeCapsulePreviewScreenGeometry(
    DeviceScreenRect Bounds,
    double DpiScaleX,
    double DpiScaleY);

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
    Func<string, bool> OpenTodoLinkedTarget,
    Func<Style> ReadTodoCheckStyle,
    Func<string> ReadPluginStatus,
    Action<string> OpenExternal,
    EdgeCapsulePreviewInvalidationSource InvalidationSource)
{
    public string Title => ReadTitle();
}

/// <summary>
/// Internal content seam for edge preview cards. Built-in Todo/Markdown and protocol 1.8 plugin
/// adapters replace only the descriptor; queue, host, transition and input code remain shared.
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
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 440);
        var height = Math.Clamp(
            150 + EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                status,
                Math.Max(72, width - 40)) * AppTypography.Scale(20),
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
/// session only the owner has a non-standard height. Every endpoint is a tightly packed queue;
/// shared transition progress then preserves the same gap between every adjacent pair in flight.
/// </summary>
internal static class EdgeCapsulePreviewLayoutCoordinator
{
    public static EdgeCapsulePreviewLayoutSession? OpenOrTransfer(
        EdgeCapsuleQueuePlan basePlan,
        string queueKey,
        string ownerPaperId,
        EdgeCapsulePreviewSize size,
        double compactHeightDip)
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
        var paperIds = papers.Select(paper => paper.Id).ToArray();
        var newHeight = Math.Max(compactHeight, size.HeightDip);
        var expansion = newHeight - compactHeight;

        // Accepted temporary 1.8 behavior: preview browsing preserves the queue-relative motion
        // even when a tall card or its followers extend beyond the monitor work area. Do not clamp
        // the card height or shrink the whole corridor here; that policy needs a separate design.

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < papers.Count; index++)
        {
            offsets[papers[index].Id] = index > newIndex ? expansion : 0;
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
