using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer
    {
        private void ApplyLinkSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            var lineStart = line.Offset;
            var lineEnd = line.EndOffset;
            if (lineEnd <= lineStart)
            {
                return;
            }

            var isPreviewMode = _owner._editor.IsPreviewMode;
            var syntaxBrush = _owner.FadeSyntax
                ? Theme.SyntaxFadeBrush
                : Theme.ActiveBrush;
            var destinationBrush = _owner.FadeSyntax
                ? Theme.SyntaxFadeBrush
                : Theme.WeakTextBrush;

            foreach (var link in snapshot.LinksForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (link.End <= lineStart || link.Start >= lineEnd)
                {
                    continue;
                }

                if (!link.IsAuto)
                {
                    // Color only syntax around the label. Painting the whole source span first would
                    // incorrectly recolor the label in edit mode (and inside quote styling).
                    ApplyAbsolute(
                        line,
                        link.Start,
                        Math.Min(link.LabelStart, link.End),
                        element => element.TextRunProperties.SetForegroundBrush(syntaxBrush));
                    ApplyAbsolute(
                        line,
                        Math.Max(link.LabelEnd, link.Start),
                        link.End,
                        element => element.TextRunProperties.SetForegroundBrush(syntaxBrush));

                    if (link.DestinationStart >= 0 && link.DestinationLength > 0)
                    {
                        ApplyAbsolute(
                            line,
                            link.DestinationStart,
                            link.DestinationEnd,
                            element => element.TextRunProperties.SetForegroundBrush(destinationBrush));
                    }
                }

                if (isPreviewMode && link.LabelLength > 0)
                {
                    ApplyAbsolute(
                        line,
                        link.LabelStart,
                        link.LabelEnd,
                        element =>
                        {
                            element.TextRunProperties.SetForegroundBrush(Theme.LinkBrush);
                            MergeDecoration(element, TextDecorations.Underline);
                        });
                }
            }
        }
    }
}
