using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private sealed partial class SemanticColorizer
    {
        private void ApplyCodeTypography(
            VisualLineElement element,
            Brush foreground)
        {
            element.TextRunProperties.SetTypeface(CodeTypeface);
            var size = _owner.ScaledFontSize(NoteTypography.CodeFontSize);
            element.TextRunProperties.SetFontRenderingEmSize(size);
            element.TextRunProperties.SetFontHintingEmSize(size);
            element.TextRunProperties.SetForegroundBrush(foreground);
        }
    }
}
