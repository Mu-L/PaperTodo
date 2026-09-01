namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private const int LineIndexSegmentTargetLines = 8;

    /// <summary>
    /// Small immutable wrapper around a handful of per-line semantic buckets. Buckets keep the
    /// coordinates they had when they were created; suffix edits only create a new wrapper with an
    /// accumulated offset delta instead of rebuilding every unaffected line bucket.
    /// </summary>
    private sealed class MarkdownSemanticLineIndexSegment
    {
        private readonly MarkdownSemanticSpan[][] _storedSpansByLine;
        private readonly MarkdownSemanticLink[][] _storedLinksByLine;
        private readonly MarkdownSemanticSpan[]?[] _shiftedSpansByLine;
        private readonly MarkdownSemanticLink[]?[] _shiftedLinksByLine;

        public MarkdownSemanticLineIndexSegment(
            int baseOffset,
            int sourceLength,
            int firstLine,
            int offsetDelta,
            MarkdownSemanticSpan[][] spansByLine,
            MarkdownSemanticLink[][] linksByLine)
        {
            BaseOffset = baseOffset;
            SourceLength = sourceLength;
            FirstLine = firstLine;
            OffsetDelta = offsetDelta;
            _storedSpansByLine = spansByLine;
            _storedLinksByLine = linksByLine;
            _shiftedSpansByLine = new MarkdownSemanticSpan[]?[spansByLine.Length];
            _shiftedLinksByLine = new MarkdownSemanticLink[]?[linksByLine.Length];
        }

        public int BaseOffset { get; }
        public int SourceLength { get; }
        public int EndOffset => BaseOffset + SourceLength;
        public int FirstLine { get; }
        public int LineCount => _storedSpansByLine.Length;
        public int EndLine => FirstLine + LineCount;
        private int OffsetDelta { get; }

        public MarkdownSemanticLineIndexSegment Rebase(int sourceDelta, int lineDelta) =>
            new(
                BaseOffset + sourceDelta,
                SourceLength,
                FirstLine + lineDelta,
                OffsetDelta + sourceDelta,
                _storedSpansByLine,
                _storedLinksByLine);

        public MarkdownSemanticLineIndexSegment TakeLines(int lineCount)
        {
            if (lineCount >= LineCount)
            {
                return this;
            }
            if (lineCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lineCount));
            }

            var spans = new MarkdownSemanticSpan[lineCount][];
            var links = new MarkdownSemanticLink[lineCount][];
            Array.Copy(_storedSpansByLine, spans, lineCount);
            Array.Copy(_storedLinksByLine, links, lineCount);
            return new MarkdownSemanticLineIndexSegment(
                BaseOffset,
                SourceLength,
                FirstLine,
                OffsetDelta,
                spans,
                links);
        }

        public MarkdownSemanticSpan[] SpansForLine(int localLine)
        {
            if (localLine < 0 || localLine >= LineCount)
            {
                return Array.Empty<MarkdownSemanticSpan>();
            }

            var stored = _storedSpansByLine[localLine];
            if (stored.Length == 0 || OffsetDelta == 0)
            {
                return stored;
            }

            var cached = Volatile.Read(ref _shiftedSpansByLine[localLine]);
            if (cached != null)
            {
                return cached;
            }

            var shifted = new MarkdownSemanticSpan[stored.Length];
            for (var index = 0; index < stored.Length; index++)
            {
                shifted[index] = ShiftSpan(stored[index], OffsetDelta);
            }

            var published = Interlocked.CompareExchange(
                ref _shiftedSpansByLine[localLine],
                shifted,
                null);
            return published ?? shifted;
        }

        public MarkdownSemanticLink[] LinksForLine(int localLine)
        {
            if (localLine < 0 || localLine >= LineCount)
            {
                return Array.Empty<MarkdownSemanticLink>();
            }

            var stored = _storedLinksByLine[localLine];
            if (stored.Length == 0 || OffsetDelta == 0)
            {
                return stored;
            }

            var cached = Volatile.Read(ref _shiftedLinksByLine[localLine]);
            if (cached != null)
            {
                return cached;
            }

            var shifted = new MarkdownSemanticLink[stored.Length];
            for (var index = 0; index < stored.Length; index++)
            {
                shifted[index] = ShiftLink(stored[index], OffsetDelta);
            }

            var published = Interlocked.CompareExchange(
                ref _shiftedLinksByLine[localLine],
                shifted,
                null);
            return published ?? shifted;
        }
    }

    private static MarkdownSemanticLineIndexSegment[] BuildLineIndexSegments(
        string source,
        int[] lineStarts,
        MarkdownSemanticSpan[][] spansByLine,
        MarkdownSemanticLink[][] linksByLine,
        bool includeTrailingEmptyLine = true)
    {
        var lineCount = lineStarts.Length;
        if (!includeTrailingEmptyLine &&
            lineCount > 0 &&
            lineStarts[^1] == source.Length)
        {
            lineCount--;
        }

        if (lineCount <= 0)
        {
            return Array.Empty<MarkdownSemanticLineIndexSegment>();
        }

        var segmentCount = (lineCount + LineIndexSegmentTargetLines - 1) /
            LineIndexSegmentTargetLines;
        var result = new MarkdownSemanticLineIndexSegment[segmentCount];
        var write = 0;
        for (var startLine = 0;
             startLine < lineCount;
             startLine += LineIndexSegmentTargetLines)
        {
            var endLine = Math.Min(lineCount, startLine + LineIndexSegmentTargetLines);
            var segmentStart = lineStarts[startLine];
            var segmentEnd = endLine < lineCount
                ? lineStarts[endLine]
                : source.Length;
            var localLineCount = endLine - startLine;
            var segmentSpans = new MarkdownSemanticSpan[localLineCount][];
            var segmentLinks = new MarkdownSemanticLink[localLineCount][];
            Array.Copy(spansByLine, startLine, segmentSpans, 0, localLineCount);
            Array.Copy(linksByLine, startLine, segmentLinks, 0, localLineCount);

            result[write++] = new MarkdownSemanticLineIndexSegment(
                segmentStart,
                segmentEnd - segmentStart,
                startLine,
                0,
                segmentSpans,
                segmentLinks);
        }

        return result;
    }

    private MarkdownSemanticLineIndexSegment? FindLineIndexSegment(int zeroBasedLine)
    {
        var low = 0;
        var high = _lineIndexSegments.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var segment = _lineIndexSegments[middle];
            if (zeroBasedLine < segment.FirstLine)
            {
                high = middle - 1;
            }
            else if (zeroBasedLine >= segment.EndLine)
            {
                low = middle + 1;
            }
            else
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Production incremental path. Markdig still parses and validates the adaptive local window,
    /// while the expensive per-line derived indexes are replaced only for the small group of
    /// segments covering that window. Flat spans/links remain canonical and keep existing query and
    /// predictor behavior unchanged.
    /// </summary>
    internal static bool TryParseSegmentedIncremental(
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

        // Snapshots produced by the compatibility constructor deliberately stay on the legacy path.
        // Normal application snapshots are created by Parse and always contain line-index segments.
        if (oldSnapshot._lineIndexSegments.Length == 0)
        {
            return false;
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
            if (Math.Max(oldSource.Length, newSource.Length) > IncrementalMaxWindowChars)
            {
                return false;
            }

            snapshot = Parse(newSource);
            info = new MarkdownIncrementalUpdateInfo(
                0,
                oldSource.Length,
                0,
                newSource.Length,
                changedOldLength,
                changedNewLength);
            return true;
        }

        var delta = newSource.Length - oldSource.Length;
        var oldLineStarts = BuildLineStarts(oldSource);
        for (var targetChars = IncrementalTargetWindowChars;
             targetChars <= IncrementalMaxWindowChars;
             targetChars *= 2)
        {
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
                    out _,
                    out _))
            {
                return false;
            }

            if (!ExpandWindowToLineIndexSegmentBoundaries(
                    oldSource,
                    oldSnapshot,
                    oldLineStarts,
                    changedStart,
                    oldChangedEnd,
                    ref oldStart,
                    ref oldEnd))
            {
                return false;
            }

            var newStart = oldStart;
            var newEnd = oldEnd == oldSource.Length
                ? newSource.Length
                : oldEnd + delta;
            if (newStart < 0 ||
                newStart > changedStart ||
                newEnd < newChangedEnd ||
                newEnd > newSource.Length)
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

            if (!TryReplaceLineIndexSegments(
                    oldSnapshot,
                    localSource,
                    local,
                    oldStart,
                    oldEnd,
                    newStart,
                    newEnd,
                    newSource.Length,
                    delta,
                    out var lineIndexSegments,
                    out var firstLine,
                    out var oldSuffixLine,
                    out var newLocalLineCount))
            {
                return false;
            }

            var lines = SpliceLines(
                oldSnapshot._lines,
                local._lines,
                firstLine,
                oldSuffixLine,
                newLocalLineCount);
            snapshot = new MarkdownSemanticSnapshot(lines, spans, links, lineIndexSegments);
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

    private static bool ExpandWindowToLineIndexSegmentBoundaries(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        int[] lineStarts,
        int changedStart,
        int oldChangedEnd,
        ref int start,
        ref int end)
    {
        for (var pass = 0; pass < 12; pass++)
        {
            var previousStart = start;
            var previousEnd = end;

            AlignToLineIndexSegments(oldSnapshot._lineIndexSegments, ref start, ref end);
            ExpandToOverlappingSemantics(oldSnapshot._spans, ref start, ref end);
            ExpandToOverlappingSemantics(oldSnapshot._links, ref start, ref end);
            start = FindLineStart(oldSource, start);
            end = AlignLineEndExclusive(oldSource, end);
            ExpandToSafeBlockBoundaries(
                oldSource,
                oldSnapshot,
                lineStarts,
                ref start,
                ref end);
            AlignToLineIndexSegments(oldSnapshot._lineIndexSegments, ref start, ref end);

            if (start > changedStart || end < oldChangedEnd)
            {
                return false;
            }
            if (end - start > IncrementalMaxWindowChars + IncrementalTargetWindowChars)
            {
                return false;
            }
            if (start == previousStart && end == previousEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static void AlignToLineIndexSegments(
        MarkdownSemanticLineIndexSegment[] segments,
        ref int start,
        ref int end)
    {
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.SourceLength <= 0)
            {
                continue;
            }
            if (start >= segment.BaseOffset && start < segment.EndOffset)
            {
                start = segment.BaseOffset;
                break;
            }
        }

        if (end <= 0)
        {
            return;
        }

        var probe = end - 1;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.SourceLength <= 0)
            {
                continue;
            }
            if (probe >= segment.BaseOffset && probe < segment.EndOffset)
            {
                end = segment.EndOffset;
                break;
            }
        }
    }

    private static bool TryReplaceLineIndexSegments(
        MarkdownSemanticSnapshot oldSnapshot,
        string localSource,
        MarkdownSemanticSnapshot local,
        int oldStart,
        int oldEnd,
        int newStart,
        int newEnd,
        int newDocumentLength,
        int delta,
        out MarkdownSemanticLineIndexSegment[] result,
        out int firstLine,
        out int oldSuffixLine,
        out int newLocalLineCount)
    {
        result = Array.Empty<MarkdownSemanticLineIndexSegment>();
        firstLine = 0;
        oldSuffixLine = 0;
        newLocalLineCount = 0;

        var oldSegments = oldSnapshot._lineIndexSegments;
        var first = 0;
        while (first < oldSegments.Length && oldSegments[first].EndOffset <= oldStart)
        {
            first++;
        }

        var suffix = first;
        while (suffix < oldSegments.Length &&
               (oldSegments[suffix].BaseOffset < oldEnd ||
                (oldSegments[suffix].SourceLength == 0 &&
                 oldEnd == oldSegments[suffix].BaseOffset)))
        {
            suffix++;
        }

        if (first >= oldSegments.Length ||
            oldSegments[first].BaseOffset != oldStart ||
            suffix <= first ||
            oldSegments[suffix - 1].EndOffset != oldEnd)
        {
            return false;
        }

        firstLine = oldSegments[first].FirstLine;
        oldSuffixLine = suffix < oldSegments.Length
            ? oldSegments[suffix].FirstLine
            : oldSnapshot.LineCount;
        var oldLineCount = oldSuffixLine - firstLine;
        var includeTrailingEmptyLine = newEnd == newDocumentLength;
        newLocalLineCount = EffectiveLineCount(localSource, includeTrailingEmptyLine);
        if (newLocalLineCount < 0 || newLocalLineCount > local.LineCount)
        {
            return false;
        }

        var localSegments = RebaseLocalSegments(
            local._lineIndexSegments,
            newStart,
            firstLine,
            newLocalLineCount);
        if (newLocalLineCount > 0 && localSegments.Length == 0)
        {
            return false;
        }

        var lineDelta = newLocalLineCount - oldLineCount;
        result = new MarkdownSemanticLineIndexSegment[
            first + localSegments.Length + (oldSegments.Length - suffix)];

        if (first > 0)
        {
            Array.Copy(oldSegments, 0, result, 0, first);
        }
        if (localSegments.Length > 0)
        {
            Array.Copy(localSegments, 0, result, first, localSegments.Length);
        }

        var write = first + localSegments.Length;
        for (var index = suffix; index < oldSegments.Length; index++)
        {
            result[write++] = oldSegments[index].Rebase(delta, lineDelta);
        }

        return true;
    }

    private static MarkdownSemanticLineIndexSegment[] RebaseLocalSegments(
        MarkdownSemanticLineIndexSegment[] localSegments,
        int sourceDelta,
        int lineDelta,
        int lineCount)
    {
        if (lineCount <= 0)
        {
            return Array.Empty<MarkdownSemanticLineIndexSegment>();
        }

        var result = new List<MarkdownSemanticLineIndexSegment>();
        var remaining = lineCount;
        foreach (var segment in localSegments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(remaining, segment.LineCount);
            if (take <= 0)
            {
                continue;
            }

            var selected = take == segment.LineCount
                ? segment
                : segment.TakeLines(take);
            result.Add(selected.Rebase(sourceDelta, lineDelta));
            remaining -= take;
        }

        return remaining == 0
            ? result.ToArray()
            : Array.Empty<MarkdownSemanticLineIndexSegment>();
    }

    private static int EffectiveLineCount(string source, bool includeTrailingEmptyLine)
    {
        var lineStarts = BuildLineStarts(source);
        var count = lineStarts.Length;
        if (!includeTrailingEmptyLine &&
            count > 0 &&
            lineStarts[^1] == source.Length)
        {
            count--;
        }
        return count;
    }

    private static MarkdownSemanticLine[] SpliceLines(
        MarkdownSemanticLine[] oldLines,
        MarkdownSemanticLine[] localLines,
        int firstLine,
        int oldSuffixLine,
        int newLocalLineCount)
    {
        var suffixCount = oldLines.Length - oldSuffixLine;
        var result = new MarkdownSemanticLine[firstLine + newLocalLineCount + suffixCount];
        if (firstLine > 0)
        {
            Array.Copy(oldLines, 0, result, 0, firstLine);
        }
        if (newLocalLineCount > 0)
        {
            Array.Copy(localLines, 0, result, firstLine, newLocalLineCount);
        }
        if (suffixCount > 0)
        {
            Array.Copy(
                oldLines,
                oldSuffixLine,
                result,
                firstLine + newLocalLineCount,
                suffixCount);
        }
        return result;
    }
}
