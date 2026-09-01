using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation : IMarkdownRendererSession
{
    private readonly MarkdownTextBox _editor;
    private readonly MarkdownSemanticDocument _semanticDocument;
    private readonly SemanticColorizer _colorizer;
    private readonly SemanticBackgroundRenderer _backgroundRenderer;
    private readonly SemanticListRenderer _listRenderer;
    private readonly SemanticHorizontalRuleRenderer _horizontalRuleRenderer;
    private bool _redrawQueued;
    private bool _disposed;

    public MarkdownSemanticPresentation(
        MarkdownTextBox editor,
        MarkdownSemanticDocument semanticDocument)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _semanticDocument = semanticDocument ?? throw new ArgumentNullException(nameof(semanticDocument));
        _colorizer = new SemanticColorizer(this);
        _backgroundRenderer = new SemanticBackgroundRenderer(this);
        _listRenderer = new SemanticListRenderer(this);
        _horizontalRuleRenderer = new SemanticHorizontalRuleRenderer(this);

        var textView = editor.TextArea.TextView;
        textView.LineTransformers.Insert(0, _colorizer);
        textView.BackgroundRenderers.Insert(0, _backgroundRenderer);
        textView.BackgroundRenderers.Add(_listRenderer);
        textView.BackgroundRenderers.Add(_horizontalRuleRenderer);
        _semanticDocument.SnapshotChanged += OnSnapshotChanged;
        RedrawAll();
    }

    private bool ApplyMarkdownStyle =>
        !string.Equals(
            _editor.MarkdownRenderMode,
            MarkdownRenderModes.Off,
            StringComparison.Ordinal);

    private bool FadeSyntax =>
        string.Equals(
            _editor.MarkdownRenderMode,
            MarkdownRenderModes.Enhanced,
            StringComparison.Ordinal) &&
        _editor.IsPreviewMode;

    private bool RenderBlocks => ApplyMarkdownStyle;

    private bool RenderListBullets => FadeSyntax;

    private bool RenderHorizontalRules =>
        RenderBlocks && _editor.IsPreviewMode;

    private bool TryCurrentSnapshot(out MarkdownSemanticSnapshot snapshot) =>
        _semanticDocument.TryGetCurrent(out snapshot);

    private MarkdownSemanticSnapshot CurrentSnapshot() =>
        _semanticDocument.TryGetCurrent(out var snapshot)
            ? snapshot
            : MarkdownSemanticSnapshot.Empty;

    private MarkdownSemanticLine SemanticFor(DocumentLine line) =>
        CurrentSnapshot().GetLine(Math.Max(0, line.LineNumber - 1));

    private double ScaledFontSize(double baseFontSize)
    {
        var baseSize = Math.Max(1, NoteTypography.FontSize);
        var scale = Math.Clamp(_editor.FontSize / baseSize, 0.5, 1.5);
        return Math.Round(baseFontSize * scale, 1);
    }

    private void OnSnapshotChanged()
    {
        if (_redrawQueued || _disposed)
        {
            return;
        }

        _redrawQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _redrawQueued = false;
                if (!_disposed)
                {
                    RedrawAll();
                }
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RedrawAll()
    {
        _editor.TextArea.TextView.Redraw(
            System.Windows.Threading.DispatcherPriority.Render);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semanticDocument.SnapshotChanged -= OnSnapshotChanged;
        var textView = _editor.TextArea.TextView;
        textView.LineTransformers.Remove(_colorizer);
        textView.BackgroundRenderers.Remove(_backgroundRenderer);
        textView.BackgroundRenderers.Remove(_listRenderer);
        textView.BackgroundRenderers.Remove(_horizontalRuleRenderer);
    }
}
