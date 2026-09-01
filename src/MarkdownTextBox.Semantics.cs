namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private MarkdownSemanticDocument? _semanticDocument;

    internal void SetSemanticDocument(MarkdownSemanticDocument? semanticDocument)
    {
        if (ReferenceEquals(_semanticDocument, semanticDocument))
        {
            return;
        }

        if (_semanticDocument != null)
        {
            DisableSemanticTracking();
            DisableSemanticImagePresentation();
        }

        _semanticDocument = semanticDocument;
        if (_semanticDocument != null)
        {
            EnableSemanticTracking();
            EnableSemanticImagePresentation();
        }
    }

    private bool TryGetSemanticSnapshot(out MarkdownSemanticSnapshot snapshot)
    {
        snapshot = null!;
        if (_semanticDocument == null)
        {
            return false;
        }

        try
        {
            snapshot = _semanticDocument.Current();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private bool TryGetPublishedSemanticSnapshot(out MarkdownSemanticSnapshot snapshot)
    {
        snapshot = null!;
        return _semanticDocument != null &&
            _semanticDocument.TryGetCurrent(out snapshot);
    }

    private bool TryGetLatestSemanticSnapshot(
        out MarkdownSemanticSnapshot snapshot,
        out int earliestChangedOffset,
        out bool lineStructureChanged)
    {
        snapshot = null!;
        earliestChangedOffset = 0;
        lineStructureChanged = true;
        return _semanticDocument != null &&
            _semanticDocument.TryGetLatest(
                out snapshot,
                out earliestChangedOffset,
                out lineStructureChanged);
    }
}
