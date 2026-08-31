using System.IO;
using System.Text;
using System.Windows;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private sealed record ClipboardImagePayload(
        NoteImageAsset Asset,
        byte[] Bytes);

    private enum ImageAwareCopyResult
    {
        NotApplicable,
        Copied,
        TextOnlyDueToSize
    }

    public event Action? ImageCopyDegradedToText;

    public new void Copy()
    {
        var result = CopySelectionWithImagesToClipboard();
        if (result == ImageAwareCopyResult.Copied)
        {
            return;
        }

        base.Copy();
        if (result == ImageAwareCopyResult.TextOnlyDueToSize)
        {
            ImageCopyDegradedToText?.Invoke();
        }
    }

    private ImageAwareCopyResult CopySelectionWithImagesToClipboard()
    {
        var documentText = Text ?? "";
        var selectionStart = Math.Clamp(SelectionStart, 0, documentText.Length);
        var selectionLength = Math.Clamp(SelectionLength, 0, documentText.Length - selectionStart);
        if (selectionLength <= 0)
        {
            return TryCopySelectedImageReferenceToClipboard();
        }

        var selected = documentText.Substring(selectionStart, selectionLength);
        var references = ImageReferencesInDocumentRange(
            documentText,
            selectionStart,
            selectionLength);
        return references.Count == 0
            ? ImageAwareCopyResult.NotApplicable
            : TrySetImageAwareClipboardData(selected, references);
    }

    private ImageAwareCopyResult TryCopySelectedImageReferenceToClipboard()
    {
        if (Document == null ||
            _selectedImageReferenceAnchor is not { IsDeleted: false } anchor ||
            string.IsNullOrWhiteSpace(_selectedImageId))
        {
            return ImageAwareCopyResult.NotApplicable;
        }

        try
        {
            var line = Document.GetLineByOffset(Math.Clamp(anchor.Offset, 0, Document.TextLength));
            var analysis = GetLineAnalysis(Document, line);
            if (analysis.ParsedImageReference is not { } parsed ||
                !string.Equals(parsed.ImageId, _selectedImageId, StringComparison.Ordinal))
            {
                return ImageAwareCopyResult.NotApplicable;
            }

            var markdown = Document.GetText(line);
            var reference = parsed with
            {
                LineStart = 0,
                LineLength = markdown.Length
            };
            return TrySetImageAwareClipboardData(markdown, new[] { reference });
        }
        catch
        {
            return ImageAwareCopyResult.NotApplicable;
        }
    }

    private ImageAwareCopyResult TrySetImageAwareClipboardData(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references)
    {
        var imageStore = _imageStore;
        if (imageStore == null ||
            string.IsNullOrWhiteSpace(_noteId) ||
            references.Count == 0)
        {
            return ImageAwareCopyResult.NotApplicable;
        }

        try
        {
            // Preflight from metadata before reading any blobs. Count every occurrence because
            // HTML embeds the encoded bytes once per rendered image occurrence.
            var assets = new Dictionary<string, NoteImageAsset>(StringComparer.Ordinal);
            long htmlSourceBytes = 0;
            foreach (var reference in references)
            {
                if (!assets.TryGetValue(reference.ImageId, out var asset))
                {
                    if (!imageStore.TryGetAsset(reference.ImageId, out asset) ||
                        !string.Equals(asset.NoteId, _noteId, StringComparison.Ordinal))
                    {
                        return ImageAwareCopyResult.NotApplicable;
                    }
                    assets.Add(reference.ImageId, asset);
                }

                if (asset.ByteLength <= 0)
                {
                    return ImageAwareCopyResult.NotApplicable;
                }
                if (htmlSourceBytes > MaxExternalClipboardHtmlSourceBytes - asset.ByteLength)
                {
                    return ImageAwareCopyResult.TextOnlyDueToSize;
                }
                htmlSourceBytes += asset.ByteLength;
            }

            var images = new Dictionary<string, ClipboardImagePayload>(StringComparer.Ordinal);
            foreach (var pair in assets)
            {
                if (!imageStore.TryGetEncodedImageBytes(pair.Key, out var asset, out var bytes) ||
                    !string.Equals(asset.NoteId, _noteId, StringComparison.Ordinal))
                {
                    return ImageAwareCopyResult.NotApplicable;
                }

                images.Add(pair.Key, new ClipboardImagePayload(asset, bytes));
            }

            var data = new DataObject();
            AddImageAwareExternalClipboardFormats(data, markdown, references, images);
            // Keep Markdown as the canonical PaperTodo-to-PaperTodo and plain-text representation.
            data.SetData(DataFormats.UnicodeText, markdown);
            data.SetData(DataFormats.Text, markdown);
            Clipboard.SetDataObject(data, copy: true);
            return ImageAwareCopyResult.Copied;
        }
        catch
        {
            return ImageAwareCopyResult.NotApplicable;
        }
    }

    private static string EnsureContextAwareImageReferencePasteIsBlock(
        string pasteText,
        DocumentReplacementTarget replacementTarget)
    {
        if (string.IsNullOrWhiteSpace(pasteText))
        {
            return pasteText;
        }

        var firstLineEnd = pasteText.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? pasteText : pasteText[..firstLineEnd];
        var lastLineStart = pasteText.LastIndexOfAny(['\r', '\n']) + 1;
        var lastLine = pasteText[lastLineStart..];
        var selectionStart = replacementTarget.Start;
        var selectionEnd = selectionStart + replacementTarget.SelectionLength;
        var couldNeedLeadingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(firstLine, out var firstReference) &&
            selectionStart > 0 &&
            replacementTarget.OriginalText[selectionStart - 1] is not '\r' and not '\n';
        var couldNeedTrailingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(lastLine, out var lastReference) &&
            (selectionEnd >= replacementTarget.OriginalText.Length ||
                replacementTarget.OriginalText[selectionEnd] is not '\r' and not '\n');
        if (!couldNeedLeadingNewLine && !couldNeedTrailingNewLine)
        {
            return pasteText;
        }

        // Probe both possible boundaries together. A single image pasted in the middle of a text
        // line needs both newlines before it can become a valid block image; testing one edge at a
        // time would incorrectly classify it as ordinary text.
        var leadingLength = couldNeedLeadingNewLine ? Environment.NewLine.Length : 0;
        var probe = string.Concat(
            couldNeedLeadingNewLine ? Environment.NewLine : "",
            pasteText,
            couldNeedTrailingNewLine ? Environment.NewLine : "");
        var candidate = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            probe);
        var probeReferences = ImageReferencesInDocumentRange(
            candidate,
            replacementTarget.Start,
            probe.Length);

        var addLeadingNewLine = couldNeedLeadingNewLine && probeReferences.Any(reference =>
            reference.LineStart == leadingLength &&
            reference.LineLength == firstLine.Length &&
            string.Equals(reference.ImageId, firstReference.ImageId, StringComparison.Ordinal));
        var lastReferenceStart = leadingLength + lastLineStart;
        var addTrailingNewLine = couldNeedTrailingNewLine && probeReferences.Any(reference =>
            reference.LineStart == lastReferenceStart &&
            reference.LineLength == lastLine.Length &&
            string.Equals(reference.ImageId, lastReference.ImageId, StringComparison.Ordinal));

        if (!addLeadingNewLine && !addTrailingNewLine)
        {
            return pasteText;
        }

        var builder = new StringBuilder(
            pasteText.Length +
            (addLeadingNewLine ? Environment.NewLine.Length : 0) +
            (addTrailingNewLine ? Environment.NewLine.Length : 0));
        if (addLeadingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        builder.Append(pasteText);
        if (addTrailingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    private static string BuildImageReferenceCloneProbe(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < references.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(Environment.NewLine);
            }

            var reference = references[index];
            builder.Append(markdown, reference.LineStart, reference.LineLength);
        }
        return builder.ToString();
    }

    private static string ReplaceImageReferenceIdsAtReferences(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyList<string> replacementIds)
    {
        if (references.Count != replacementIds.Count)
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        var additionalLength = 0L;
        for (var index = 0; index < references.Count; index++)
        {
            additionalLength += replacementIds[index].Length - references[index].ImageId.Length;
        }
        var capacity = checked((int)(markdown.Length + additionalLength));
        var builder = new StringBuilder(capacity);
        var cursor = 0;
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            var line = markdown.Substring(reference.LineStart, reference.LineLength);
            var imageToken = MarkdownImageReferences.UriPrefix + reference.ImageId;
            var urlMarker = line.IndexOf("](", StringComparison.Ordinal);
            var tokenStart = urlMarker >= 0
                ? line.IndexOf(imageToken, urlMarker + 2, StringComparison.Ordinal)
                : -1;
            if (tokenStart < 0)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }

            var idStart = tokenStart + MarkdownImageReferences.UriPrefix.Length;
            builder.Append(line, 0, idStart);
            builder.Append(replacementIds[index]);
            builder.Append(
                line,
                idStart + reference.ImageId.Length,
                line.Length - idStart - reference.ImageId.Length);
            cursor = reference.LineStart + reference.LineLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static IReadOnlyList<MarkdownImageReference> ImageReferencesInDocumentRange(
        string documentText,
        int rangeStart,
        int rangeLength)
    {
        var rangeEnd = rangeStart + rangeLength;
        var references = new List<MarkdownImageReference>();

        // Always parse the complete document before intersecting the range. Starting a fresh parser
        // at selected/pasted text loses fenced-code state inherited from earlier document lines.
        foreach (var reference in MarkdownImageReferences.Enumerate(documentText))
        {
            var referenceEnd = reference.LineStart + reference.LineLength;
            if (reference.LineStart < rangeStart || referenceEnd > rangeEnd)
            {
                continue;
            }

            references.Add(reference with
            {
                LineStart = reference.LineStart - rangeStart
            });
        }

        return references;
    }
}
