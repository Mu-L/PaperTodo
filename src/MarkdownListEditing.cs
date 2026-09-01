using System.Globalization;

namespace PaperTodo;

internal readonly record struct MarkdownListContinuationPlan(
    int MarkerStart,
    int ContentStart,
    int EmptyContentStart,
    string Continuation);

/// <summary>
/// Builds editor continuation behavior from PaperTodo semantic spans plus the untouched source line.
/// Markdig decides whether a real list item/task marker exists; this helper only preserves the exact
/// source prefix/spacing and computes the text that Enter should insert.
/// </summary>
internal static class MarkdownListEditing
{
    public static bool TryBuildContinuationPlan(
        string lineText,
        int absoluteLineStart,
        MarkdownSemanticSnapshot snapshot,
        out MarkdownListContinuationPlan plan)
    {
        plan = default;
        ArgumentNullException.ThrowIfNull(lineText);
        ArgumentNullException.ThrowIfNull(snapshot);

        var absoluteLineEnd = absoluteLineStart + lineText.Length;
        if (!TryFindListMarker(snapshot, absoluteLineStart, absoluteLineEnd, out var marker))
        {
            return false;
        }

        var markerStart = marker.Start - absoluteLineStart;
        var markerEnd = marker.End - absoluteLineStart;
        if (markerStart < 0 || markerEnd <= markerStart || markerEnd > lineText.Length)
        {
            return false;
        }

        var contentStart = markerEnd;
        while (contentStart < lineText.Length && char.IsWhiteSpace(lineText[contentStart]))
        {
            contentStart++;
        }

        var task = FindTaskMarker(
            snapshot,
            absoluteLineStart + contentStart,
            absoluteLineEnd);
        var hasTask = task.HasValue && task.Value.Start == absoluteLineStart + contentStart;
        var emptyContentStart = contentStart;

        string continuation;
        if (marker.Kind == MarkdownSemanticSpanKind.UnorderedListMarker)
        {
            continuation = lineText[..contentStart];
        }
        else
        {
            if (!TryBuildOrderedContinuation(
                    lineText,
                    markerStart,
                    markerEnd,
                    contentStart,
                    out continuation))
            {
                return false;
            }
        }

        if (hasTask)
        {
            continuation += "[ ] ";
            emptyContentStart = Math.Clamp(
                task!.Value.End - absoluteLineStart,
                contentStart,
                lineText.Length);
            while (emptyContentStart < lineText.Length &&
                   char.IsWhiteSpace(lineText[emptyContentStart]))
            {
                emptyContentStart++;
            }
        }

        plan = new MarkdownListContinuationPlan(
            markerStart,
            contentStart,
            emptyContentStart,
            continuation);
        return true;
    }

    private static bool TryFindListMarker(
        MarkdownSemanticSnapshot snapshot,
        int lineStart,
        int lineEnd,
        out MarkdownSemanticSpan marker)
    {
        foreach (var span in snapshot.Spans)
        {
            if (span.Start >= lineEnd)
            {
                break;
            }

            if (span.Start < lineStart ||
                span.End > lineEnd ||
                span.Kind is not (
                    MarkdownSemanticSpanKind.UnorderedListMarker or
                    MarkdownSemanticSpanKind.OrderedListMarker))
            {
                continue;
            }

            // Preserve established PaperTodo behavior for multiple markers on one physical line:
            // the first source marker owns Enter continuation for that line.
            marker = span;
            return true;
        }

        marker = default;
        return false;
    }

    private static MarkdownSemanticSpan? FindTaskMarker(
        MarkdownSemanticSnapshot snapshot,
        int searchStart,
        int lineEnd)
    {
        foreach (var span in snapshot.Spans)
        {
            if (span.Start >= lineEnd)
            {
                break;
            }

            if (span.Kind == MarkdownSemanticSpanKind.TaskListMarker &&
                span.Start >= searchStart &&
                span.End <= lineEnd)
            {
                return span;
            }
        }

        return null;
    }

    private static bool TryBuildOrderedContinuation(
        string lineText,
        int markerStart,
        int markerEnd,
        int contentStart,
        out string continuation)
    {
        continuation = string.Empty;
        if (markerEnd - markerStart < 2)
        {
            return false;
        }

        var delimiterIndex = markerEnd - 1;
        var delimiter = lineText[delimiterIndex];
        if (delimiter is not ('.' or ')'))
        {
            return false;
        }

        var numberText = lineText[markerStart..delimiterIndex];
        if (!long.TryParse(
                numberText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) ||
            number == long.MaxValue)
        {
            return false;
        }

        continuation = lineText[..markerStart] +
            (number + 1).ToString(CultureInfo.InvariantCulture) +
            delimiter +
            lineText[markerEnd..contentStart];
        return true;
    }
}
