using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer
    {
        private bool TryHideImageReference(DocumentLine line)
        {
            var semantic = _owner.CurrentSnapshot().GetLine(Math.Max(0, line.LineNumber - 1));
            if (semantic.IsCode ||
                !_owner._editor.ShouldHideImageReferenceTextForSemanticPresentation ||
                !_owner._editor.IsImageReferenceLineForSemanticPresentation(line))
            {
                return false;
            }

            // Preserve the source line's metrics exactly as the mature colorizer did. The image
            // element generator still belongs to MarkdownTextBox; only its reference text styling
            // moves into the semantic presentation authority.
            ApplyAbsolute(line, line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetTypeface(NormalTypeface);
                var size = _owner.ScaledFontSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontRenderingEmSize(size);
                element.TextRunProperties.SetFontHintingEmSize(size);
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
            });
            return true;
        }
    }
}
