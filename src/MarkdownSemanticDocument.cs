namespace PaperTodo;

/// <summary>
/// Per-editor semantic cache owned by the same thread as AvalonEdit's TextDocument. The initial
/// document and every completed text change synchronously publish exact Markdig semantics before
/// control returns to WPF rendering. Ordinary edits use the bounded incremental parser first;
/// edits that cannot be proven local fall back to a full parse on the same thread.
///
/// One current source string is retained beside the semantic snapshot so the next edit can compare
/// against the exact published generation without rematerializing the previous AvalonEdit Rope on
/// every keystroke. There is no worker, semaphore, pending queue, stale generation or concurrent
/// publication path.
/// </summary>
internal sealed class MarkdownSemanticDocument : IDisposable
{
    private readonly ICSharpCode.AvalonEdit.Document.TextDocument _document;
    private MarkdownSemanticSnapshot _snapshot;
    private string _snapshotSource;
    private bool _disposed;

    public MarkdownSemanticDocument(ICSharpCode.AvalonEdit.Document.TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _snapshotSource = _document.Text;
        _snapshot = MarkdownSemanticSnapshot.Parse(_snapshotSource);
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

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var source = _document.Text;
        var next = MarkdownSemanticSnapshot.TryParseIncremental(
            _snapshotSource,
            _snapshot,
            source,
            out var incremental,
            out _)
            ? incremental
            : MarkdownSemanticSnapshot.Parse(source);

        _snapshotSource = source;
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
        _document.TextChanged -= OnDocumentTextChanged;
        _snapshotSource = string.Empty;
        _snapshot = MarkdownSemanticSnapshot.Empty;
        SnapshotChanged = null;
    }
}
