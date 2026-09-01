using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace PaperTodo;

[Flags]
internal enum MarkdownSemanticLineTraits
{
    None = 0,
    Quote = 1 << 0,
    UnorderedList = 1 << 1,
    OrderedList = 1 << 2,
    Code = 1 << 3,
    FencedCode = 1 << 4,
    HorizontalRule = 1 << 5,
    SetextMarker = 1 << 6,
    SetextHeading = 1 << 7,
    FencedCodeOpening = 1 << 8,
    FencedCodeClosing = 1 << 9
}

internal enum MarkdownSemanticSpanKind
{
    Heading,
    SetextHeading,
    Quote,
    UnorderedList,
    OrderedList,
    UnorderedListMarker,
    OrderedListMarker,
    Code,
    FencedCode,
    FencedCodeOpening,
    FencedCodeClosing,
    HorizontalRule,
    SetextMarker,
    Emphasis,
    Strong,
    Strikethrough,
    InlineCode,
    Image,
    TaskListMarker,
    HtmlContainer,
    HtmlMarker,
    HtmlStrong,
    HtmlEmphasis,
    HtmlStrikethrough,
    HtmlUnderline,
    HtmlCode,
    EscapeMarker
}

internal readonly record struct MarkdownSemanticSpan(
    MarkdownSemanticSpanKind Kind,
    int Start,
    int Length,
    int Level = 0,
    int MarkerLength = 0,
    bool Checked = false)
{
    public int End => Start + Length;
}

internal readonly record struct MarkdownSemanticLink(
    int Start,
    int Length,
    int LabelStart,
    int LabelLength,
    int DestinationStart,
    int DestinationLength,
    string Url,
    bool IsAuto)
{
    public int End => Start + Length;
    public int LabelEnd => LabelStart + LabelLength;
    public int DestinationEnd => DestinationStart + DestinationLength;
}

internal readonly record struct MarkdownSemanticLine(
    MarkdownSemanticLineTraits Traits,
    int HeadingLevel)
{
    public bool IsQuoted =>
        (Traits & MarkdownSemanticLineTraits.Quote) != 0;

    public bool IsCode =>
        (Traits & MarkdownSemanticLineTraits.Code) != 0;

    public bool IsFencedCode =>
        (Traits & MarkdownSemanticLineTraits.FencedCode) != 0;

    public bool IsFencedCodeOpening =>
        (Traits & MarkdownSemanticLineTraits.FencedCodeOpening) != 0;

    public bool IsFencedCodeClosing =>
        (Traits & MarkdownSemanticLineTraits.FencedCodeClosing) != 0;

    public bool IsFencedCodeMarker =>
        IsFencedCodeOpening || IsFencedCodeClosing;

    public bool IsHorizontalRule =>
        (Traits & MarkdownSemanticLineTraits.HorizontalRule) != 0;

    public bool IsSetextMarker =>
        (Traits & MarkdownSemanticLineTraits.SetextMarker) != 0;

    public bool IsSetextHeading =>
        (Traits & MarkdownSemanticLineTraits.SetextHeading) != 0;
}

/// <summary>
/// Immutable Markdig-derived Markdown semantics for one exact source version. Source offsets stay
/// in the original Markdown coordinate space; PaperTodo never creates a second rendered document.
/// Markdig AST objects are flattened immediately into PaperTodo-owned records.
/// </summary>
internal sealed partial class MarkdownSemanticSnapshot
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .Build();

    public static MarkdownSemanticSnapshot Empty { get; } = new(
        Array.Empty<MarkdownSemanticLine>(),
        Array.Empty<MarkdownSemanticSpan>(),
        Array.Empty<MarkdownSemanticLink>(),
        Array.Empty<MarkdownSemanticSpan[]>(),
        Array.Empty<MarkdownSemanticLink[]>());

    private readonly MarkdownSemanticLine[] _lines;
    private readonly MarkdownSemanticSpan[] _spans;
    private readonly MarkdownSemanticLink[] _links;
    private readonly MarkdownSemanticSpan[][] _spansByLine;
    private readonly MarkdownSemanticLink[][] _linksByLine;

    private MarkdownSemanticSnapshot(
        MarkdownSemanticLine[] lines,
        MarkdownSemanticSpan[] spans,
        MarkdownSemanticLink[] links,
        MarkdownSemanticSpan[][] spansByLine,
        MarkdownSemanticLink[][] linksByLine)
    {
        _lines = lines;
        _spans = spans;
        _links = links;
        _spansByLine = spansByLine;
        _linksByLine = linksByLine;
    }

    public IReadOnlyList<MarkdownSemanticSpan> Spans => _spans;
    public IReadOnlyList<MarkdownSemanticLink> Links => _links;
    public int LineCount => _lines.Length;

    public MarkdownSemanticLine GetLine(int zeroBasedLine)
    {
        if (zeroBasedLine < 0 || zeroBasedLine >= _lines.Length)
        {
            return default;
        }

        return _lines[zeroBasedLine];
    }

    public ReadOnlySpan<MarkdownSemanticSpan> SpansForLine(int zeroBasedLine)
    {
        return zeroBasedLine >= 0 && zeroBasedLine < _spansByLine.Length
            ? _spansByLine[zeroBasedLine]
            : ReadOnlySpan<MarkdownSemanticSpan>.Empty;
    }

    public ReadOnlySpan<MarkdownSemanticLink> LinksForLine(int zeroBasedLine)
    {
        return zeroBasedLine >= 0 && zeroBasedLine < _linksByLine.Length
            ? _linksByLine[zeroBasedLine]
            : ReadOnlySpan<MarkdownSemanticLink>.Empty;
    }

    public bool TryGetLinkAtOffset(int offset, out MarkdownSemanticLink link)
    {
        var low = 0;
        var high = _links.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = _links[middle];
            if (offset < candidate.Start)
            {
                high = middle - 1;
                continue;
            }
            if (offset >= candidate.End)
            {
                low = middle + 1;
                continue;
            }

            link = candidate;
            return true;
        }

        link = default;
        return false;
    }

    public static MarkdownSemanticSnapshot Parse(string? markdown)
    {
        var source = markdown ?? string.Empty;
        var lineStarts = BuildLineStarts(source);
        var spans = new List<MarkdownSemanticSpan>();
        var links = new List<MarkdownSemanticLink>();
        var document = Markdown.Parse(source, Pipeline);

        CollectBlocks(document, source, lineStarts, spans, links);
        ApplyLegacyCompatibilityBoundaries(source, spans, links);
        CollectEscapeMarkers(source, spans);
        CollectBareHttpLinks(source, spans, links);
        spans.Sort(CompareSemanticSpans);
        links.Sort(CompareSemanticLinks);

        var lines = new MarkdownSemanticLine[lineStarts.Length];
        foreach (var span in spans)
        {
            ApplySpanToLines(source, lineStarts, lines, span);
        }

        return new MarkdownSemanticSnapshot(
            lines,
            spans.ToArray(),
            links.ToArray(),
            BuildSpanLineIndex(source, lineStarts, spans),
            BuildLinkLineIndex(source, lineStarts, links));
    }

    /// <summary>
    /// Canonical snapshot ordering must not depend on List.Sort's unstable treatment of equal keys.
    /// Equal source ranges are common (for example an empty list item can make the list container and
    /// its marker occupy the same character). Full and local parses can contain different list sizes,
    /// so a complete tie-breaker is required for byte-for-byte-equivalent incremental snapshots.
    /// </summary>
    private static int CompareSemanticSpans(MarkdownSemanticSpan left, MarkdownSemanticSpan right)
    {
        var comparison = left.Start.CompareTo(right.Start);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.End.CompareTo(right.End);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.Level.CompareTo(right.Level);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.MarkerLength.CompareTo(right.MarkerLength);
        return comparison != 0
            ? comparison
            : left.Checked.CompareTo(right.Checked);
    }

    private static int CompareSemanticLinks(MarkdownSemanticLink left, MarkdownSemanticLink right)
    {
        var comparison = left.Start.CompareTo(right.Start);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.End.CompareTo(right.End);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.LabelStart.CompareTo(right.LabelStart);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.LabelLength.CompareTo(right.LabelLength);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.DestinationStart.CompareTo(right.DestinationStart);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.DestinationLength.CompareTo(right.DestinationLength);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.IsAuto.CompareTo(right.IsAuto);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Url, right.Url);
    }

    private static MarkdownSemanticSpan[][] BuildSpanLineIndex(
        string source,
        int[] lineStarts,
        IReadOnlyList<MarkdownSemanticSpan> spans)
    {
        var result = new MarkdownSemanticSpan[lineStarts.Length][];
        if (lineStarts.Length == 0)
        {
            return result;
        }

        var counts = new int[lineStarts.Length];
        foreach (var span in spans)
        {
            if (span.Length <= 0)
            {
                continue;
            }

            var first = FindLineIndex(source, lineStarts, span.Start);
            var last = FindLineIndex(source, lineStarts, Math.Max(span.Start, span.End - 1));
            for (var line = first; line <= last && line < counts.Length; line++)
            {
                counts[line]++;
            }
        }

        for (var line = 0; line < result.Length; line++)
        {
            result[line] = counts[line] == 0
                ? Array.Empty<MarkdownSemanticSpan>()
                : new MarkdownSemanticSpan[counts[line]];
        }

        Array.Clear(counts);
        foreach (var span in spans)
        {
            if (span.Length <= 0)
            {
                continue;
            }

            var first = FindLineIndex(source, lineStarts, span.Start);
            var last = FindLineIndex(source, lineStarts, Math.Max(span.Start, span.End - 1));
            for (var line = first; line <= last && line < result.Length; line++)
            {
                result[line][counts[line]++] = span;
            }
        }

        return result;
    }

    private static MarkdownSemanticLink[][] BuildLinkLineIndex(
        string source,
        int[] lineStarts,
        IReadOnlyList<MarkdownSemanticLink> links)
    {
        var result = new MarkdownSemanticLink[lineStarts.Length][];
        if (lineStarts.Length == 0)
        {
            return result;
        }

        var counts = new int[lineStarts.Length];
        foreach (var link in links)
        {
            if (link.Length <= 0)
            {
                continue;
            }

            var first = FindLineIndex(source, lineStarts, link.Start);
            var last = FindLineIndex(source, lineStarts, Math.Max(link.Start, link.End - 1));
            for (var line = first; line <= last && line < counts.Length; line++)
            {
                counts[line]++;
            }
        }

        for (var line = 0; line < result.Length; line++)
        {
            result[line] = counts[line] == 0
                ? Array.Empty<MarkdownSemanticLink>()
                : new MarkdownSemanticLink[counts[line]];
        }

        Array.Clear(counts);
        foreach (var link in links)
        {
            if (link.Length <= 0)
            {
                continue;
            }

            var first = FindLineIndex(source, lineStarts, link.Start);
            var last = FindLineIndex(source, lineStarts, Math.Max(link.Start, link.End - 1));
            for (var line = first; line <= last && line < result.Length; line++)
            {
                result[line][counts[line]++] = link;
            }
        }

        return result;
    }
}
