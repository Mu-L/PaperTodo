using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private bool _semanticTrackingEnabled;

    private void EnableSemanticTracking()
    {
        if (_semanticTrackingEnabled)
        {
            return;
        }

        Document.Changed += OnSemanticDocumentChanged;
        _semanticTrackingEnabled = true;
    }

    private void DisableSemanticTracking()
    {
        if (!_semanticTrackingEnabled)
        {
            return;
        }

        Document.Changed -= OnSemanticDocumentChanged;
        _semanticTrackingEnabled = false;
    }

    private void OnSemanticDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_hadInternalImageReferences || e.InsertedText.TextLength <= 0)
        {
            return;
        }

        _hadInternalImageReferences = e.InsertedText.IndexOf(
            MarkdownImageReferences.UriPrefix,
            0,
            e.InsertedText.TextLength,
            StringComparison.Ordinal) >= 0;
    }
}
