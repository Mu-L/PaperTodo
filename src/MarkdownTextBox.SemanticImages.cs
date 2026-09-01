using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private SemanticMarkdownImageElementGenerator? _semanticImageElementGenerator;

    internal bool ShouldHideImageReferenceTextForSemanticPresentation =>
        ShouldHideImageReferenceText;

    internal bool IsImageReferenceLineForSemanticPresentation(DocumentLine line)
    {
        return Document != null &&
            !line.IsDeleted &&
            MarkdownImageReferences.TryParseReferenceLine(Document.GetText(line), out _);
    }

    private void EnableSemanticImagePresentation()
    {
        var generators = TextArea.TextView.ElementGenerators;
        _semanticImageElementGenerator ??= new SemanticMarkdownImageElementGenerator(this);
        var reuseIsActive = _reusingImageElementGenerator != null &&
            generators.Contains(_reusingImageElementGenerator);
        if (!reuseIsActive && !generators.Contains(_semanticImageElementGenerator))
        {
            generators.Add(_semanticImageElementGenerator);
        }

        RefreshTextView();
    }

    private void DisableSemanticImagePresentation()
    {
        if (_semanticImageElementGenerator != null)
        {
            TextArea.TextView.ElementGenerators.Remove(_semanticImageElementGenerator);
        }
        RefreshTextView();
    }

    private bool TryGetImageReferenceForLine(
        DocumentLine line,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        return TryGetSemanticSnapshot(out var snapshot) &&
            TryGetImageReferenceForLineWithSnapshot(line, snapshot, out reference, out asset);
    }

    private bool TryGetImageReferenceForLineForPresentation(
        DocumentLine line,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        return TryGetPublishedSemanticSnapshot(out var snapshot) &&
            TryGetImageReferenceForLineWithSnapshot(line, snapshot, out reference, out asset);
    }

    private bool TryGetImageReferenceForStableEditingLine(
        DocumentLine line,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        if (TryGetPublishedSemanticSnapshot(out var current))
        {
            return TryGetImageReferenceForLineWithSnapshot(
                line,
                current,
                out reference,
                out asset);
        }

        if (!TryGetLatestSemanticSnapshot(
                out var latest,
                out var earliestChangedOffset,
                out var lineStructureChanged) ||
            lineStructureChanged ||
            earliestChangedOffset < line.EndOffset + line.DelimiterLength)
        {
            return false;
        }

        return TryGetImageReferenceForLineWithSnapshot(
            line,
            latest,
            out reference,
            out asset);
    }

    private bool TryGetImageReferenceForLineWithSnapshot(
        DocumentLine line,
        MarkdownSemanticSnapshot snapshot,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        if (_imageStore == null ||
            Document == null ||
            !ShouldRenderImages ||
            line.IsDeleted)
        {
            return false;
        }

        var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
        if (semantic.IsCode ||
            !MarkdownImageReferences.TryParseReferenceLine(Document.GetText(line), out reference))
        {
            return false;
        }

        if (_imageStore.TryGetAsset(reference.ImageId, out var found) &&
            string.Equals(found.NoteId, _noteId, StringComparison.Ordinal))
        {
            asset = found;
        }

        return true;
    }

    private sealed class SemanticMarkdownImageElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownTextBox _owner;

        public SemanticMarkdownImageElementGenerator(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return -1;
            }

            var document = CurrentContext.Document;
            if (document == null || document.TextLength <= 0)
            {
                return -1;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            return referenceLine.EndOffset >= startOffset &&
                _owner.TryGetImageReferenceForLineForPresentation(referenceLine, out _, out _)
                ? referenceLine.EndOffset
                : -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return null!;
            }

            var document = CurrentContext.Document;
            if (document == null || offset < 0 || offset > document.TextLength)
            {
                return null!;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            if (referenceLine.EndOffset != offset ||
                !_owner.TryGetImageReferenceForLineForPresentation(
                    referenceLine,
                    out var reference,
                    out var asset))
            {
                return null!;
            }

            var element = _owner.CreateImageBlock(reference, asset, referenceLine);
            return new BlockImageElement(element);
        }
    }
}
