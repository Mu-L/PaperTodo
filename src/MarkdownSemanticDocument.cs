namespace PaperTodo;

/// <summary>
/// Per-editor semantic cache owned by the same thread as AvalonEdit's TextDocument. Opening a note
/// always publishes one exact full-document Markdig snapshot. Completed edits below 2K characters
/// are also parsed in full; larger notes use the lightweight local reparse path and synchronously
/// fall back to a full parse only for the few global reference-definition cases it declines.
/// There is no worker, semaphore, pending queue, stale generation or concurrent publication path.
/// </summary>
internal sealed class MarkdownSemanticDocument : IDisposable
{
    internal const int FullParseThresholdChars = 2000;

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
        MarkdownSemanticSnapshot next;
        if (source.Length < FullParseThresholdChars)
        {
            next = MarkdownSemanticSnapshot.Parse(source);
        }
        else if (MarkdownSemanticSnapshot.TryParseIncremental(
                     _snapshotSource,
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
