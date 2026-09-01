using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

/// <summary>
/// Per-editor semantic cache owned by the same thread as AvalonEdit's TextDocument. The initial
/// document and every completed text change synchronously publish exact Markdig semantics before
/// control returns to WPF rendering. Ordinary edits use the bounded incremental parser first;
/// edits that cannot be proven local fall back to a full parse on the same thread.
///
/// The previous source is retained only for the duration of one edit transaction as AvalonEdit's
/// cheap immutable Rope snapshot. No permanent worker, semaphore, generation queue or duplicate
/// full-source string is kept while the editor is idle.
/// </summary>
internal sealed class MarkdownSemanticDocument : IDisposable
{
    private readonly TextDocument _document;
    private MarkdownSemanticSnapshot _snapshot;
    private ITextSource? _sourceBeforeChange;
    private bool _disposed;

    public MarkdownSemanticDocument(TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _snapshot = MarkdownSemanticSnapshot.Parse(_document.Text);
        _document.Changing += OnDocumentChanging;
        _document.TextChanged += OnDocumentTextChanged;
    }

    /// <summary>
    /// Raised synchronously after semantics for the completed TextDocument change are published.
    /// Consumers may still defer visual invalidation to their normal WPF render priority.
    /// </summary>
    public event Action? SnapshotChanged;

    public bool TryGetCurrent(out MarkdownSemanticSnapshot snapshot)
    {
        if (!_disposed)
        {
            snapshot = _snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    /// <summary>
    /// Compatibility surface for editor helpers that previously had to reason about an async stale
    /// snapshot. Publication is now synchronous, so a live document's latest snapshot is always
    /// current and there is no pending dirty range.
    /// </summary>
    public bool TryGetLatest(
        out MarkdownSemanticSnapshot snapshot,
        out int earliestChangedOffset,
        out bool lineStructureChanged)
    {
        if (!_disposed)
        {
            snapshot = _snapshot;
            earliestChangedOffset = int.MaxValue;
            lineStructureChanged = false;
            return true;
        }

        snapshot = null!;
        earliestChangedOffset = 0;
        lineStructureChanged = true;
        return false;
    }

    public MarkdownSemanticSnapshot Current()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _snapshot;
    }

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_disposed || _sourceBeforeChange != null)
        {
            return;
        }

        // TextDocument may batch several primitive mutations inside BeginUpdate/EndUpdate. Capture
        // the source only before the first mutation so the semantic snapshot and old source describe
        // the same complete pre-edit generation. RopeTextSource shares immutable rope nodes and does
        // not materialize another whole-document string here.
        _sourceBeforeChange = _document.CreateSnapshot();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var sourceBeforeChange = _sourceBeforeChange;
        _sourceBeforeChange = null;
        var source = _document.Text;

        MarkdownSemanticSnapshot next;
        if (sourceBeforeChange != null &&
            MarkdownSemanticSnapshot.TryParseIncremental(
                sourceBeforeChange.Text,
                _snapshot,
                source,
                out var incremental,
                out _))
        {
            next = incremental;
        }
        else
        {
            next = MarkdownSemanticSnapshot.Parse(source);
        }

        _snapshot = next;
        SnapshotChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.Changing -= OnDocumentChanging;
        _document.TextChanged -= OnDocumentTextChanged;
        _sourceBeforeChange = null;
        _snapshot = MarkdownSemanticSnapshot.Empty;
        SnapshotChanged = null;
    }
}
