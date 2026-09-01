namespace PaperTodo;

internal readonly record struct MarkdownIncrementalUpdateInfo(
    int OldStart,
    int OldLength,
    int NewStart,
    int NewLength,
    int ChangedOldLength,
    int ChangedNewLength);

internal sealed partial class MarkdownSemanticSnapshot
{
    private const int IncrementalPrimaryWindowChars = 1024;
    private const int IncrementalRetryWindowChars = 16384;
    private const int IncrementalMaxWindowChars = IncrementalRetryWindowChars;
    private const int IncrementalMaxChangedChars = 2048;
    private const int IncrementalGuardChars = 1024;

    private readonly record struct GuardSpan(
        MarkdownSemanticSpanKind Kind,
        int Start,
        int Length,
        int Level,
        int MarkerLength,
        bool Checked);

    private readonly record struct GuardLink(
        int Start,
        int Length,
        string Url,
        bool IsAuto);

    /// <summary>
    /// Attempts one small local reparse before paying for a larger local retry or a full-document
    /// Markdig pass. The first target is 1K and may expand to complete semantic containers and safe
    /// block boundaries. If its unchanged outer guard regions are not stable, one 16K target is tried.
    /// Global reference dependencies, oversized edits, windows that exceed 16K, or an unstable 16K
    /// retry return false so the caller performs the synchronous full parse. Markdig remains the only
    /// Markdown authority.
    /// </summary>
    internal static bool TryParseIncremental(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        out MarkdownSemanticSnapshot snapshot,
        out MarkdownIncrementalUpdateInfo info)
    {
        ArgumentNullException.ThrowIfNull(oldSource);
        ArgumentNullException.ThrowIfNull(oldSnapshot);
        ArgumentNullException.ThrowIfNull(newSource);

        snapshot = null!;
        info = default;

        if (ReferenceEquals(oldSource, newSource) ||
            string.Equals(oldSource, newSource, StringComparison.Ordinal))
        {
            snapshot = oldSnapshot;
            return true;
        }

        FindContiguousDifference(
            oldSource,
            newSource,
            out var changedStart,
            out var oldChangedEnd,
            out var newChangedEnd);

        var changedOldLength = oldChangedEnd - changedStart;
        var changedNewLength = newChangedEnd - changedStart;
        if (changedOldLength + changedNewLength > IncrementalMaxChangedChars)
        {
            return false;
        }

        var oldLine = GetLineBounds(oldSource, changedStart);
        var newLine = GetLineBounds(newSource, changedStart);
        var hasGlobalReferenceDependency =
            IsPotentialReferenceDefinition(oldSource, oldLine.Start, oldLine.End) ||
            IsPotentialReferenceDefinition(newSource, newLine.Start, newLine.End) ||
            ChangeTouchesSquareBracket(oldSource, changedStart, oldChangedEnd) ||
            ChangeTouchesSquareBracket(newSource, changedStart, newChangedEnd) ||
            ReferenceStyleLinkOverlapsChange(
                oldSource,
                oldSnapshot._links,
                changedStart,
                oldChangedEnd);

        if (hasGlobalReferenceDependency)
        {
            // Reference definitions/uses can affect arbitrarily distant source. Do not grow a local
            // resolver or hide a whole-document parse inside the incremental path; the caller owns
            // the single synchronous full-parse fallback.
            return false;
        }

        var delta = newSource.Length - oldSource.Length;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targetChars = attempt == 0
                ? IncrementalPrimaryWindowChars
                : IncrementalRetryWindowChars;
            if (!TryBuildIncrementalWindow(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    changedStart,
                    oldChangedEnd,
                    newChangedEnd,
                    delta,
                    targetChars,
                    out var oldStart,
                    out var oldEnd,
                    out var newStart,
                    out var newEnd))
            {
                return false;
            }

            var localSource = newSource[newStart..newEnd];
            var local = Parse(localSource);

            if (oldStart == 0 &&
                oldEnd == oldSource.Length &&
                newStart == 0 &&
                newEnd == newSource.Length)
            {
                snapshot = local;
                info = new MarkdownIncrementalUpdateInfo(
                    oldStart,
                    oldEnd - oldStart,
                    newStart,
                    newEnd - newStart,
                    changedOldLength,
                    changedNewLength);
                return true;
            }

            var referenceStable = ReferenceLinksRemainStable(
                oldSource,
                oldSnapshot._links,
                local._links,
                oldStart,
                oldEnd,
                newStart,
                changedStart,
                oldChangedEnd,
                delta);
            var guardsStable = GuardRegionsMatch(
                oldSnapshot,
                local,
                oldStart,
                oldEnd,
                newStart,
                changedStart,
                oldChangedEnd,
                delta);

            if (!referenceStable || !guardsStable)
            {
                if (targetChars == IncrementalMaxWindowChars)
                {
                    return false;
                }
                continue;
            }

            var spans = SpliceSpans(
                oldSnapshot._spans,
                local._spans,
                oldStart,
                oldEnd,
                newStart,
                delta);
            var links = SpliceLinks(
                oldSnapshot._links,
                local._links,
                oldStart,
                oldEnd,
                newStart,
                delta);

            var lineStarts = BuildLineStarts(newSource);
            var lines = new MarkdownSemanticLine[lineStarts.Length];
            foreach (var span in spans)
            {
                ApplySpanToLines(newSource, lineStarts, lines, span);
            }

            snapshot = new MarkdownSemanticSnapshot(
                lines,
                spans,
                links,
                BuildSpanLineIndex(newSource, lineStarts, spans),
                BuildLinkLineIndex(newSource, lineStarts, links));
            info = new MarkdownIncrementalUpdateInfo(
                oldStart,
                oldEnd - oldStart,
                newStart,
                newEnd - newStart,
                changedOldLength,
                changedNewLength);
            return true;
        }

        return false;
    }

    private static void FindContiguousDifference(
        string oldSource,
        string newSource,
        out int start,
        out int oldEnd,
        out int newEnd)
    {
        var sharedLength = Math.Min(oldSource.Length, newSource.Length);
        start = 0;
        while (start < sharedLength && oldSource[start] == newSource[start])
        {
            start++;
        }

        oldEnd = oldSource.Length;
        newEnd = newSource.Length;
        while (oldEnd > start &&
               newEnd > start &&
               oldSource[oldEnd - 1] == newSource[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
    }

    private static bool TryBuildIncrementalWindow(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        int changedStart,
        int oldChangedEnd,
        int newChangedEnd,
        int delta,
        int targetChars,
        out int oldStart,
        out int oldEnd,
        out int newStart,
        out int newEnd)
    {
        newStart = 0;
        newEnd = 0;

        var leftBudget = targetChars / 2;
        var rightBudget = targetChars - leftBudget;
        oldStart = FindLineStart(oldSource, Math.Max(0, changedStart - leftBudget));
        oldEnd = FindLineEndExclusive(
            oldSource,
            Math.Min(oldSource.Length, Math.Max(oldChangedEnd, changedStart + rightBudget)));

        var lineStarts = BuildLineStarts(oldSource);
        for (var pass = 0; pass < 12; pass++)
        {
            var previousStart = oldStart;
            var previousEnd = oldEnd;

            ExpandToOverlappingSemantics(oldSnapshot._spans, ref oldStart, ref oldEnd);
            ExpandToOverlappingSemantics(oldSnapshot._links, ref oldStart, ref oldEnd);
            oldStart = FindLineStart(oldSource, oldStart);
            oldEnd = AlignLineEndExclusive(oldSource, oldEnd);
            ExpandToSafeBlockBoundaries(
                oldSource,
                oldSnapshot,
                lineStarts,
                ref oldStart,
                ref oldEnd);

            if (oldEnd - oldStart > IncrementalMaxWindowChars)
            {
                return false;
            }
            if (oldStart == previousStart && oldEnd == previousEnd)
            {
                break;
            }
        }

        if (oldStart > changedStart || oldEnd < oldChangedEnd)
        {
            return false;
        }

        newStart = oldStart;
        newEnd = oldEnd == oldSource.Length
            ? newSource.Length
            : oldEnd + delta;
        if (newStart < 0 ||
            newStart > changedStart ||
            newEnd < newChangedEnd ||
            newEnd > newSource.Length ||
            newEnd - newStart > IncrementalMaxWindowChars)
        {
            return false;
        }

        return true;
    }

    private static void ExpandToSafeBlockBoundaries(
        string source,
        MarkdownSemanticSnapshot snapshot,
        int[] lineStarts,
        ref int start,
        ref int end)
    {
        while (start > 0)
        {
            var lineIndex = FindLineIndex(source, lineStarts, start);
            if (lineIndex <= 0 || IsSafeBoundary(snapshot, source, lineStarts, lineIndex))
            {
                break;
            }
            start = lineStarts[lineIndex - 1];
        }

        while (end < source.Length)
        {
            var lineIndex = FindLineIndex(source, lineStarts, end);
            if (lineStarts[lineIndex] != end)
            {
                end = AlignLineEndExclusive(source, end);
                continue;
            }
            if (IsSafeBoundary(snapshot, source, lineStarts, lineIndex))
            {
                break;
            }
            end = lineIndex + 1 < lineStarts.Length
                ? lineStarts[lineIndex + 1]
                : source.Length;
        }
    }

    private static bool IsSafeBoundary(
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts,
        int lineIndex)
    {
        if (lineIndex <= 0 || lineIndex >= lineStarts.Length)
        {
            return true;
        }

        if (IsBlankLine(source, lineStarts, lineIndex - 1) ||
            IsBlankLine(source, lineStarts, lineIndex))
        {
            return true;
        }

        return IsStructuralLine(snapshot.GetLine(lineIndex - 1)) ||
            IsStructuralLine(snapshot.GetLine(lineIndex));
    }

    private static bool IsStructuralLine(MarkdownSemanticLine line) =>
        line.Traits != MarkdownSemanticLineTraits.None || line.HeadingLevel > 0;

    private static bool IsBlankLine(string source, int[] lineStarts, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lineStarts.Length)
        {
            return true;
        }

        var start = lineStarts[lineIndex];
        var end = lineIndex + 1 < lineStarts.Length
            ? TrimLineDelimiterEnd(source, lineStarts[lineIndex + 1])
            : source.Length;
        for (var index = start; index < end; index++)
        {
            if (source[index] is not (' ' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    private static void ExpandToOverlappingSemantics(
        IReadOnlyList<MarkdownSemanticSpan> spans,
        ref int start,
        ref int end)
    {
        foreach (var span in spans)
        {
            if (span.End <= start)
            {
                continue;
            }
            if (span.Start >= end)
            {
                break;
            }

            start = Math.Min(start, span.Start);
            end = Math.Max(end, span.End);
        }
    }

    private static void ExpandToOverlappingSemantics(
        IReadOnlyList<MarkdownSemanticLink> links,
        ref int start,
        ref int end)
    {
        foreach (var link in links)
        {
            if (link.End <= start)
            {
                continue;
            }
            if (link.Start >= end)
            {
                break;
            }

            start = Math.Min(start, link.Start);
            end = Math.Max(end, link.End);
        }
    }

    private static bool GuardRegionsMatch(
        MarkdownSemanticSnapshot oldSnapshot,
        MarkdownSemanticSnapshot local,
        int oldStart,
        int oldEnd,
        int newStart,
        int changedStart,
        int oldChangedEnd,
        int delta)
    {
        var candidateLength = oldEnd - oldStart;
        var guard = Math.Min(IncrementalGuardChars, Math.Max(128, candidateLength / 4));

        var prefixLength = Math.Min(guard, Math.Max(0, changedStart - oldStart));
        if (prefixLength >= 64 &&
            !RegionSemanticsMatch(
                oldSnapshot,
                oldStart,
                oldStart + prefixLength,
                local,
                oldStart - newStart,
                oldStart - newStart + prefixLength))
        {
            return false;
        }

        var suffixLength = Math.Min(guard, Math.Max(0, oldEnd - oldChangedEnd));
        if (suffixLength >= 64)
        {
            var oldRegionStart = oldEnd - suffixLength;
            var newRegionStart = oldRegionStart + delta;
            if (!RegionSemanticsMatch(
                    oldSnapshot,
                    oldRegionStart,
                    oldEnd,
                    local,
                    newRegionStart - newStart,
                    newRegionStart - newStart + suffixLength))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegionSemanticsMatch(
        MarkdownSemanticSnapshot oldSnapshot,
        int oldStart,
        int oldEnd,
        MarkdownSemanticSnapshot local,
        int localStart,
        int localEnd)
    {
        if (oldEnd - oldStart != localEnd - localStart ||
            localStart < 0 ||
            localEnd < localStart)
        {
            return false;
        }

        var oldSpans = NormalizeGuardSpans(oldSnapshot._spans, oldStart, oldEnd);
        var localSpans = NormalizeGuardSpans(local._spans, localStart, localEnd);
        if (!oldSpans.SequenceEqual(localSpans))
        {
            return false;
        }

        var oldLinks = NormalizeGuardLinks(oldSnapshot._links, oldStart, oldEnd);
        var localLinks = NormalizeGuardLinks(local._links, localStart, localEnd);
        return oldLinks.SequenceEqual(localLinks);
    }

    private static GuardSpan[] NormalizeGuardSpans(
        IReadOnlyList<MarkdownSemanticSpan> spans,
        int regionStart,
        int regionEnd)
    {
        var result = new List<GuardSpan>();
        foreach (var span in spans)
        {
            if (span.End <= regionStart)
            {
                continue;
            }
            if (span.Start >= regionEnd)
            {
                break;
            }

            var start = Math.Max(span.Start, regionStart);
            var end = Math.Min(span.End, regionEnd);
            if (end <= start)
            {
                continue;
            }
            result.Add(new GuardSpan(
                span.Kind,
                start - regionStart,
                end - start,
                span.Level,
                span.MarkerLength,
                span.Checked));
        }
        return result.ToArray();
    }

    private static GuardLink[] NormalizeGuardLinks(
        IReadOnlyList<MarkdownSemanticLink> links,
        int regionStart,
        int regionEnd)
    {
        var result = new List<GuardLink>();
        foreach (var link in links)
        {
            if (link.End <= regionStart)
            {
                continue;
            }
            if (link.Start >= regionEnd)
            {
                break;
            }

            var start = Math.Max(link.Start, regionStart);
            var end = Math.Min(link.End, regionEnd);
            if (end <= start)
            {
                continue;
            }
            result.Add(new GuardLink(
                start - regionStart,
                end - start,
                link.Url,
                link.IsAuto));
        }
        return result.ToArray();
    }

    private static bool ReferenceLinksRemainStable(
        string oldSource,
        IReadOnlyList<MarkdownSemanticLink> oldLinks,
        IReadOnlyList<MarkdownSemanticLink> localLinks,
        int oldStart,
        int oldEnd,
        int newStart,
        int changedStart,
        int oldChangedEnd,
        int delta)
    {
        foreach (var oldLink in oldLinks)
        {
            if (oldLink.End <= oldStart)
            {
                continue;
            }
            if (oldLink.Start >= oldEnd)
            {
                break;
            }
            if (!IsReferenceStyleLinkSource(oldSource, oldLink))
            {
                continue;
            }
            if (RangesOverlap(oldLink.Start, oldLink.End, changedStart, oldChangedEnd))
            {
                return false;
            }

            var expectedStart = oldLink.Start >= oldChangedEnd
                ? oldLink.Start + delta
                : oldLink.Start;
            var localExpectedStart = expectedStart - newStart;
            var found = false;
            foreach (var localLink in localLinks)
            {
                if (localLink.Start < localExpectedStart)
                {
                    continue;
                }
                if (localLink.Start > localExpectedStart)
                {
                    break;
                }
                if (!localLink.IsAuto &&
                    localLink.Length == oldLink.Length &&
                    string.Equals(localLink.Url, oldLink.Url, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ReferenceStyleLinkOverlapsChange(
        string source,
        IReadOnlyList<MarkdownSemanticLink> links,
        int changedStart,
        int oldChangedEnd)
    {
        foreach (var link in links)
        {
            if (link.End <= changedStart)
            {
                continue;
            }
            if (oldChangedEnd > changedStart && link.Start >= oldChangedEnd)
            {
                break;
            }
            if (IsReferenceStyleLinkSource(source, link) &&
                RangesOverlap(link.Start, link.End, changedStart, oldChangedEnd))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReferenceStyleLinkSource(string source, MarkdownSemanticLink link)
    {
        if (link.IsAuto ||
            link.Start < 0 ||
            link.End > source.Length ||
            link.End <= link.Start)
        {
            return false;
        }

        var token = source.AsSpan(link.Start, link.Length).TrimStart();
        if (token.StartsWith("<a", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return token.IndexOf("](".AsSpan(), StringComparison.Ordinal) < 0;
    }

    private static bool RangesOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd)
    {
        if (rightStart == rightEnd)
        {
            return rightStart >= leftStart && rightStart < leftEnd;
        }
        return leftStart < rightEnd && rightStart < leftEnd;
    }

    private static MarkdownSemanticSpan[] SpliceSpans(
        MarkdownSemanticSpan[] oldSpans,
        MarkdownSemanticSpan[] localSpans,
        int oldStart,
        int oldEnd,
        int newStart,
        int delta)
    {
        var prefixCount = LowerBoundSpanStart(oldSpans, oldStart);
        var suffixStart = LowerBoundSpanStart(oldSpans, oldEnd);
        var result = new MarkdownSemanticSpan[
            prefixCount + localSpans.Length + (oldSpans.Length - suffixStart)];

        if (prefixCount > 0)
        {
            Array.Copy(oldSpans, 0, result, 0, prefixCount);
        }

        var write = prefixCount;
        foreach (var span in localSpans)
        {
            result[write++] = ShiftSpan(span, newStart);
        }
        for (var index = suffixStart; index < oldSpans.Length; index++)
        {
            result[write++] = ShiftSpan(oldSpans[index], delta);
        }
        return result;
    }

    private static MarkdownSemanticLink[] SpliceLinks(
        MarkdownSemanticLink[] oldLinks,
        MarkdownSemanticLink[] localLinks,
        int oldStart,
        int oldEnd,
        int newStart,
        int delta)
    {
        var prefixCount = LowerBoundLinkStart(oldLinks, oldStart);
        var suffixStart = LowerBoundLinkStart(oldLinks, oldEnd);
        var result = new MarkdownSemanticLink[
            prefixCount + localLinks.Length + (oldLinks.Length - suffixStart)];

        if (prefixCount > 0)
        {
            Array.Copy(oldLinks, 0, result, 0, prefixCount);
        }

        var write = prefixCount;
        foreach (var link in localLinks)
        {
            result[write++] = ShiftLink(link, newStart);
        }
        for (var index = suffixStart; index < oldLinks.Length; index++)
        {
            result[write++] = ShiftLink(oldLinks[index], delta);
        }
        return result;
    }

    private static int LowerBoundSpanStart(MarkdownSemanticSpan[] spans, int start)
    {
        var low = 0;
        var high = spans.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (spans[middle].Start < start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static int LowerBoundLinkStart(MarkdownSemanticLink[] links, int start)
    {
        var low = 0;
        var high = links.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (links[middle].Start < start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static MarkdownSemanticSpan ShiftSpan(MarkdownSemanticSpan span, int delta) =>
        delta == 0
            ? span
            : span with { Start = span.Start + delta };

    private static MarkdownSemanticLink ShiftLink(MarkdownSemanticLink link, int delta)
    {
        if (delta == 0)
        {
            return link;
        }

        return link with
        {
            Start = link.Start + delta,
            LabelStart = ShiftOptionalOffset(link.LabelStart, delta),
            DestinationStart = ShiftOptionalOffset(link.DestinationStart, delta)
        };
    }

    private static int ShiftOptionalOffset(int offset, int delta) =>
        offset < 0 ? offset : offset + delta;

    private static bool IsPotentialReferenceDefinition(string source, int lineStart, int lineEnd)
    {
        var index = lineStart;
        var spaces = 0;
        while (index < lineEnd && source[index] == ' ' && spaces < 4)
        {
            index++;
            spaces++;
        }
        if (spaces > 3 || index >= lineEnd || source[index] != '[')
        {
            return false;
        }

        for (var cursor = index + 1; cursor + 1 < lineEnd; cursor++)
        {
            if (source[cursor] == ']' && source[cursor + 1] == ':')
            {
                return true;
            }
        }
        return false;
    }

    private static bool ChangeTouchesSquareBracket(string source, int start, int end)
    {
        var normalizedStart = Math.Clamp(start, 0, source.Length);
        var normalizedEnd = Math.Clamp(end, normalizedStart, source.Length);
        for (var index = normalizedStart; index < normalizedEnd; index++)
        {
            if (source[index] is '[' or ']')
            {
                return true;
            }
        }
        return false;
    }

    private static (int Start, int End) GetLineBounds(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        var start = FindLineStart(source, normalized);
        var end = normalized >= source.Length
            ? source.Length
            : FindLineDelimiterStart(source, normalized);
        return (start, end);
    }

    private static int FindLineStart(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        if (normalized == 0)
        {
            return 0;
        }

        for (var index = normalized - 1; index >= 0; index--)
        {
            if (source[index] == '\n')
            {
                return index + 1;
            }
            if (source[index] == '\r')
            {
                return index + 1 < source.Length && source[index + 1] == '\n'
                    ? index + 2
                    : index + 1;
            }
        }
        return 0;
    }

    private static int FindLineDelimiterStart(string source, int offset)
    {
        for (var index = Math.Clamp(offset, 0, source.Length); index < source.Length; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return index;
            }
        }
        return source.Length;
    }

    private static int FindLineEndExclusive(string source, int offset)
    {
        var delimiter = FindLineDelimiterStart(source, offset);
        if (delimiter >= source.Length)
        {
            return source.Length;
        }
        return source[delimiter] == '\r' && delimiter + 1 < source.Length && source[delimiter + 1] == '\n'
            ? delimiter + 2
            : delimiter + 1;
    }

    private static int AlignLineEndExclusive(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        if (normalized == source.Length || IsPhysicalLineStart(source, normalized))
        {
            return normalized;
        }
        return FindLineEndExclusive(source, normalized);
    }

    private static bool IsPhysicalLineStart(string source, int offset)
    {
        if (offset <= 0)
        {
            return true;
        }
        if (offset > source.Length)
        {
            return false;
        }
        if (source[offset - 1] == '\n')
        {
            return true;
        }
        return source[offset - 1] == '\r' &&
            (offset >= source.Length || source[offset] != '\n');
    }
}
