using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private static bool HasTaskMarkerOnLine(
        MarkdownSemanticSnapshot snapshot,
        DocumentLine line)
    {
        foreach (var span in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
        {
            if (span.Kind == MarkdownSemanticSpanKind.TaskListMarker &&
                span.Start < line.EndOffset &&
                span.End > line.Offset)
            {
                return true;
            }
        }
        return false;
    }

    private sealed partial class SemanticColorizer
    {
        private void ApplyListMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            ApplyTaskMarkerSemantics(line, snapshot);

            if (!_owner.RenderListBullets || HasTaskMarkerOnLine(snapshot, line))
            {
                return;
            }

            foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind is not (
                        MarkdownSemanticSpanKind.UnorderedListMarker or
                        MarkdownSemanticSpanKind.OrderedListMarker) ||
                    marker.End <= line.Offset ||
                    marker.Start >= line.EndOffset)
                {
                    continue;
                }

                ApplyAbsolute(
                    line,
                    marker.Start,
                    marker.End,
                    element => element.TextRunProperties.SetForegroundBrush(Brushes.Transparent));
            }
        }

        private void ApplyTaskMarkerSemantics(
            DocumentLine line,
            MarkdownSemanticSnapshot snapshot)
        {
            foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
            {
                if (marker.Kind != MarkdownSemanticSpanKind.TaskListMarker ||
                    marker.Length < 3 ||
                    marker.End <= line.Offset ||
                    marker.Start >= line.EndOffset)
                {
                    continue;
                }

                ApplyAbsolute(
                    line,
                    marker.Start,
                    marker.End,
                    element => element.TextRunProperties.SetForegroundBrush(Theme.ActiveBrush));
                if (marker.Checked)
                {
                    ApplyAbsolute(
                        line,
                        marker.Start + 1,
                        Math.Min(marker.End, marker.Start + 2),
                        element => element.TextRunProperties.SetTypeface(StrongTypeface));
                }
            }
        }
    }

    private sealed class SemanticListRenderer : IBackgroundRenderer
    {
        private readonly MarkdownSemanticPresentation _owner;

        private static Typeface ListMarkerTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        public SemanticListRenderer(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (!_owner.RenderListBullets || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var snapshot = _owner.CurrentSnapshot();
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    if (HasTaskMarkerOnLine(snapshot, line))
                    {
                        continue;
                    }

                    foreach (var marker in snapshot.SpansForLine(Math.Max(0, line.LineNumber - 1)))
                    {
                        if (marker.Kind is not (
                                MarkdownSemanticSpanKind.UnorderedListMarker or
                                MarkdownSemanticSpanKind.OrderedListMarker) ||
                            marker.End <= line.Offset ||
                            marker.Start >= line.EndOffset)
                        {
                            continue;
                        }

                        DrawMarker(textView, drawingContext, document, line, marker);
                    }
                }
            }
        }

        private void DrawMarker(
            TextView textView,
            DrawingContext drawingContext,
            IDocument document,
            DocumentLine line,
            MarkdownSemanticSpan marker)
        {
            if (!TryGetTextPoint(textView, line, marker.Start, VisualYPosition.TextTop, out var markerTop) ||
                !TryGetTextPoint(textView, line, marker.Start, VisualYPosition.TextMiddle, out var markerMiddle) ||
                !TryGetTextPoint(textView, line, marker.End, VisualYPosition.TextBottom, out var markerBottom))
            {
                return;
            }

            var markerLeft = markerTop.X;
            var markerRight = markerBottom.X;
            if (markerRight < markerLeft)
            {
                (markerLeft, markerRight) = (markerRight, markerLeft);
            }

            var markerWidth = Math.Max(1, markerRight - markerLeft);
            var markerHeight = Math.Max(1, markerBottom.Y - markerTop.Y);
            drawingContext.DrawRectangle(
                Theme.PaperBrush,
                null,
                new Rect(markerLeft - 1, markerTop.Y - 1, markerWidth + 2, markerHeight + 2));

            if (marker.Kind == MarkdownSemanticSpanKind.UnorderedListMarker)
            {
                var radius = Math.Max(
                    2.0,
                    Math.Min(3.2, _owner.ScaledFontSize(NoteTypography.FontSize) * 0.16));
                drawingContext.DrawEllipse(
                    Theme.TextBrush,
                    null,
                    new Point(markerLeft + markerWidth / 2, markerMiddle.Y),
                    radius,
                    radius);
                return;
            }

            var markerText = document.GetText(marker.Start, marker.Length);
            var formatted = new FormattedText(
                markerText,
                UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                ListMarkerTypeface,
                _owner.ScaledFontSize(NoteTypography.FontSize),
                Theme.TextBrush,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(textView).PixelsPerDip);
            drawingContext.DrawText(
                formatted,
                new Point(markerLeft, markerMiddle.Y - formatted.Height / 2));
        }

        private static bool TryGetTextPoint(
            TextView textView,
            DocumentLine line,
            int absoluteOffset,
            VisualYPosition yPosition,
            out Point point)
        {
            point = default;
            try
            {
                var indexInLine = Math.Clamp(absoluteOffset - line.Offset, 0, line.Length);
                point = textView.GetVisualPosition(
                    new TextViewPosition(line.LineNumber, indexInLine + 1),
                    yPosition);
                point.X -= textView.HorizontalOffset;
                point.Y -= textView.VerticalOffset;
                return double.IsFinite(point.X) && double.IsFinite(point.Y);
            }
            catch
            {
                return false;
            }
        }
    }
}
