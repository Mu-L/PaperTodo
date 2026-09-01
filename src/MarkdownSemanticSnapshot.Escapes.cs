namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private static void CollectEscapeMarkers(
        string source,
        List<MarkdownSemanticSpan> spans)
    {
        if (string.IsNullOrEmpty(source) || source.IndexOf('\\') < 0)
        {
            return;
        }

        var slashRun = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != '\\')
            {
                slashRun = 0;
                continue;
            }

            var escapedSlash = (slashRun & 1) != 0;
            if (!escapedSlash &&
                index + 1 < source.Length &&
                IsSemanticEscapable(source[index + 1]) &&
                !IsProtectedEscapePosition(spans, index))
            {
                spans.Add(new MarkdownSemanticSpan(
                    MarkdownSemanticSpanKind.EscapeMarker,
                    index,
                    1));
            }

            slashRun++;
        }
    }

    private static bool IsProtectedEscapePosition(
        IReadOnlyList<MarkdownSemanticSpan> spans,
        int offset)
    {
        foreach (var span in spans)
        {
            if (span.Kind is not (
                    MarkdownSemanticSpanKind.Code or
                    MarkdownSemanticSpanKind.FencedCode or
                    MarkdownSemanticSpanKind.InlineCode or
                    MarkdownSemanticSpanKind.HtmlContainer))
            {
                continue;
            }

            if (offset >= span.Start && offset < span.End)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSemanticEscapable(char value) =>
        value is >= '!' and <= '/' or
        >= ':' and <= '@' or
        >= '[' and <= '`' or
        >= '{' and <= '~';
}
