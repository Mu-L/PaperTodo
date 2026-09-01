using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

/// <summary>
/// Per-editor semantic cache. TextDocument changes publish immutable source snapshots to one
/// background worker. At most one Markdig parse runs at a time; edits that arrive while it is busy
/// replace a single pending source instead of starting overlapping work. Ordinary non-structural
/// edits first attempt a conservative local Markdig reparse against the last published snapshot.
/// </summary>
internal sealed class MarkdownSemanticDocument : IDisposable
{
    private readonly TextDocument _document;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _workSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _workerTask;
    private ITextSource? _pendingSource;
    private int _pendingGeneration;
    private MarkdownSemanticSnapshot? _snapshot;
    private string? _snapshotSource;
    private int _generation;
    private int _snapshotGeneration = -1;
    private int _earliestChangedOffsetSincePublished = int.MaxValue;
    private bool _lineStructureChangedSincePublished;
    private bool _disposed;

    public MarkdownSemanticDocument(TextDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _document.Changed += OnDocumentChanged;
        _document.TextChanged += OnDocumentTextChanged;
        _workerTask = Task.Run(ProcessQueueAsync);
        QueueCurrentSource();
    }

    /// <summary>
    /// Raised after the semantic snapshot for the current source generation is published. The event
    /// is raised from the parsing worker; WPF consumers must marshal through their Dispatcher.
    /// </summary>
    public event Action? SnapshotChanged;

    public bool TryGetCurrent(out MarkdownSemanticSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_disposed &&
                _snapshot != null &&
                _snapshotGeneration == _generation)
            {
                snapshot = _snapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    /// <summary>
    /// Returns the most recently published snapshot even when a newer source generation is pending.
    /// Dirty-range metadata lets editor helpers use stale semantics only when the source range they
    /// care about is provably before every change since that publication.
    /// </summary>
    public bool TryGetLatest(
        out MarkdownSemanticSnapshot snapshot,
        out int earliestChangedOffset,
        out bool lineStructureChanged)
    {
        lock (_gate)
        {
            if (_disposed || _snapshot == null)
            {
                snapshot = null!;
                earliestChangedOffset = 0;
                lineStructureChanged = true;
                return false;
            }

            snapshot = _snapshot;
            earliestChangedOffset = _snapshotGeneration == _generation
                ? int.MaxValue
                : _earliestChangedOffsetSincePublished;
            lineStructureChanged = _snapshotGeneration != _generation &&
                _lineStructureChangedSincePublished;
            return true;
        }
    }

    /// <summary>
    /// Forces exact current semantics. Keep this for deliberate click/command paths only; ordinary
    /// presentation, pointer hover and plain typing must not call it.
    /// </summary>
    public MarkdownSemanticSnapshot Current()
    {
        while (true)
        {
            if (TryGetCurrent(out var current))
            {
                return current;
            }

            int generation;
            MarkdownSemanticSnapshot? baseSnapshot;
            string? baseSource;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = _generation;
                baseSnapshot = _snapshot;
                baseSource = _snapshotSource;
            }

            // CreateSnapshot is the one TextDocument operation explicitly safe for immutable
            // cross-thread consumption. Materialize its string only for the actual parse.
            var source = _document.CreateSnapshot().Text;
            var parsed = ParseAgainstPublished(source, baseSnapshot, baseSource);
            if (TryPublish(generation, source, parsed))
            {
                return parsed;
            }
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _generation++;
            _earliestChangedOffsetSincePublished = Math.Min(
                _earliestChangedOffsetSincePublished,
                e.Offset);
            _lineStructureChangedSincePublished |=
                ContainsLineBreak(e.InsertedText) ||
                ContainsLineBreak(e.RemovedText);
        }
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        QueueCurrentSource();
    }

    private void QueueCurrentSource()
    {
        var source = _document.CreateSnapshot();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingSource = source;
            _pendingGeneration = _generation;
        }

        SignalWorker();
    }

    private void SignalWorker()
    {
        try
        {
            if (_workSignal.CurrentCount == 0)
            {
                _workSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task ProcessQueueAsync()
    {
        var cancellationToken = _disposeCancellation.Token;
        while (true)
        {
            try
            {
                await _workSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ITextSource? source;
            int generation;
            MarkdownSemanticSnapshot? baseSnapshot;
            string? baseSource;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                source = _pendingSource;
                generation = _pendingGeneration;
                _pendingSource = null;
                baseSnapshot = _snapshot;
                baseSource = _snapshotSource;
            }

            if (source == null)
            {
                continue;
            }

            try
            {
                var sourceText = source.Text;
                var parsed = ParseAgainstPublished(sourceText, baseSnapshot, baseSource);
                TryPublish(generation, sourceText, parsed);
            }
            catch (Exception ex)
            {
                Trace.TraceError("Markdown semantic background parse failed: {0}", ex);
            }

            // Changes arriving during Parse leave one signal and one latest pending source. The next
            // loop consumes exactly that newest source; obsolete intermediate generations disappear.
        }
    }

    private static MarkdownSemanticSnapshot ParseAgainstPublished(
        string source,
        MarkdownSemanticSnapshot? baseSnapshot,
        string? baseSource)
    {
        if (baseSnapshot != null &&
            baseSource != null &&
            MarkdownSemanticSnapshot.TryParseIncremental(
                baseSource,
                baseSnapshot,
                source,
                out var incremental,
                out _))
        {
            return incremental;
        }

        return MarkdownSemanticSnapshot.Parse(source);
    }

    private bool TryPublish(int generation, string source, MarkdownSemanticSnapshot snapshot)
    {
        Action? changed;
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return false;
            }

            if (_snapshot != null && _snapshotGeneration == generation)
            {
                return true;
            }

            _snapshot = snapshot;
            _snapshotSource = source;
            _snapshotGeneration = generation;
            _earliestChangedOffsetSincePublished = int.MaxValue;
            _lineStructureChangedSincePublished = false;
            changed = SnapshotChanged;
        }

        changed?.Invoke();
        return true;
    }

    private static bool ContainsLineBreak(ITextSource text)
    {
        if (text.TextLength <= 0)
        {
            return false;
        }

        return text.IndexOf('\r', 0, text.TextLength) >= 0 ||
            text.IndexOf('\n', 0, text.TextLength) >= 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _document.Changed -= OnDocumentChanged;
            _document.TextChanged -= OnDocumentTextChanged;
            _pendingSource = null;
            _snapshot = null;
            _snapshotSource = null;
            _snapshotGeneration = -1;
            SnapshotChanged = null;
        }

        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }
}
