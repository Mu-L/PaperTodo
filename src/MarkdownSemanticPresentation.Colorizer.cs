using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownSemanticPresentation _owner;

        private readonly record struct SourceRange(int Start, int End)
        {
            public bool Covers(int start, int end) => Start <= start && End >= end;
        }

        private static Typeface NormalTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static FontFamily SemanticBoldFontFamily =>
            AppTypography.FontFamilyFor(content: true, bold: true);

        private static FontWeight SemanticBoldFontWeight =>
            AppTypography.UsesCustomBoldFace(true)
                ? AppTypography.FontWeightFor(true)
                : NoteTypography.HeadingFontWeight;

        private static Typeface HeadingTypeface => new(
            SemanticBoldFontFamily,
            NoteTypography.FontStyle,
            SemanticBoldFontWeight,
            NoteTypography.FontStretch);

        private static Typeface StrongTypeface => new(
            SemanticBoldFontFamily,
            NoteTypography.FontStyle,
            SemanticBoldFontWeight,
            NoteTypography.FontStretch);

        private static Typeface CodeTypeface => new(
            NoteTypography.CodeFontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        public SemanticColorizer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (!_owner.ApplyMarkdownStyle || line.Length <= 0)
            {
                return;
            }

            var document = CurrentContext.Document;
            if (!_owner.TryCurrentSnapshot(out var snapshot))
            {
                return;
            }
            if (TryHideImageReference(line))
            {
                return;
            }

            var text = document.GetText(line);
            ApplyBlockSemantics(line, snapshot, text);
            ApplyListMarkerSemantics(line, snapshot);
            // Link foreground first; Markdown/HTML emphasis and code then compose on top.
            ApplyLinkSemantics(line, snapshot);
            ApplyInlineSemantics(line, snapshot);
            ApplyHtmlSemantics(line, snapshot);
            ApplyEscapeSemantics(line, snapshot);
        }

        private void ApplyInlineSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            if (lineEnd <= lineStart)
            {
                return;
            }

            var emphasisRanges = new List<SourceRange>();
            var strongRanges = new List<SourceRange>();
            var boundaries = new List<int> { lineStart, lineEnd };
            var markerBrush = _owner.FadeSyntax
                ? Theme.SyntaxFadeBrush
                : Theme.ActiveBrush;

            foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (span.End <= lineStart || span.Start >= lineEnd)
                {
                    continue;
                }

                if (span.Kind is not (
                        MarkdownSemanticSpanKind.Emphasis or
                        MarkdownSemanticSpanKind.Strong or
                        MarkdownSemanticSpanKind.Strikethrough or
                        MarkdownSemanticSpanKind.InlineCode))
                {
                    continue;
                }

                var markerLength = Math.Clamp(
                    span.MarkerLength,
                    1,
                    Math.Max(1, span.Length / 2));
                var contentStart = Math.Min(span.End, span.Start + markerLength);
                var contentEnd = Math.Max(contentStart, span.End - markerLength);

                ApplyAbsolute(
                    line,
                    span.Start,
                    contentStart,
                    element =>
                    {
                        if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                        {
                            ApplyCodeTypography(element, markerBrush);
                        }
                        else
                        {
                            element.TextRunProperties.SetForegroundBrush(markerBrush);
                        }
                    });
                ApplyAbsolute(
                    line,
                    contentEnd,
                    span.End,
                    element =>
                    {
                        if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                        {
                            ApplyCodeTypography(element, markerBrush);
                        }
                        else
                        {
                            element.TextRunProperties.SetForegroundBrush(markerBrush);
                        }
                    });

                if (contentEnd <= contentStart)
                {
                    continue;
                }

                if (span.Kind == MarkdownSemanticSpanKind.InlineCode)
                {
                    ApplyAbsolute(
                        line,
                        contentStart,
                        contentEnd,
                        element => ApplyCodeTypography(element, Theme.ActiveBrush));
                    continue;
                }

                if (span.Kind == MarkdownSemanticSpanKind.Strikethrough)
                {
                    ApplyAbsolute(
                        line,
                        contentStart,
                        contentEnd,
                        element => MergeDecoration(element, TextDecorations.Strikethrough));
                    continue;
                }

                var clippedStart = Math.Max(lineStart, contentStart);
                var clippedEnd = Math.Min(lineEnd, contentEnd);
                if (clippedEnd <= clippedStart)
                {
                    continue;
                }

                var range = new SourceRange(clippedStart, clippedEnd);
                if (span.Kind == MarkdownSemanticSpanKind.Strong)
                {
                    strongRanges.Add(range);
                }
                else
                {
                    emphasisRanges.Add(range);
                }
                boundaries.Add(clippedStart);
                boundaries.Add(clippedEnd);
            }

            if (emphasisRanges.Count == 0 && strongRanges.Count == 0)
            {
                return;
            }

            boundaries.Sort();
            var previous = -1;
            var compact = new List<int>(boundaries.Count);
            foreach (var boundary in boundaries)
            {
                if (boundary != previous)
                {
                    compact.Add(boundary);
                    previous = boundary;
                }
            }

            for (var index = 0; index + 1 < compact.Count; index++)
            {
                var start = compact[index];
                var end = compact[index + 1];
                if (end <= start)
                {
                    continue;
                }

                var strong = strongRanges.Any(range => range.Covers(start, end));
                var emphasis = emphasisRanges.Any(range => range.Covers(start, end));
                if (!strong && !emphasis)
                {
                    continue;
                }

                ApplyAbsolute(
                    line,
                    start,
                    end,
                    element =>
                    {
                        var current = element.TextRunProperties.Typeface;
                        var family = strong && AppTypography.UsesCustomBoldFace(true)
                            ? SemanticBoldFontFamily
                            : current.FontFamily;
                        var style = emphasis ? FontStyles.Italic : current.Style;
                        var weight = strong ? SemanticBoldFontWeight : current.Weight;
                        element.TextRunProperties.SetTypeface(new Typeface(
                            family,
                            style,
                            weight,
                            current.Stretch));
                    });
            }
        }

        private void ApplyAbsolute(
            DocumentLine line,
            int absoluteStart,
            int absoluteEnd,
            Action<VisualLineElement> action)
        {
            var start = Math.Max(line.Offset, absoluteStart);
            var end = Math.Min(line.EndOffset, absoluteEnd);
            if (end > start)
            {
                ChangeLinePart(start, end, action);
            }
        }
    }
}
