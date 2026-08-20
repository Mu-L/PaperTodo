using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Built-in Markdown body session. The stable root stays attached to PaperWindow while Markdown
/// presenters can be rebuilt inside it. All mutable editor/presenter state belongs to this session;
/// PaperWindow.Note.cs accesses it through narrow forwarding properties during the staged extraction.
/// </summary>
internal sealed class MarkdownPaperBodySession : IPaperBodySession
{
    private readonly PaperWindow _owner;
    private readonly PaperData _paper;
    private readonly NoteImageStore _imageStore;
    private readonly Grid _root = new();
    private bool _disposed;

    internal MarkdownPaperBodySession(
        PaperWindow owner,
        PaperData paper,
        NoteImageStore imageStore)
    {
        _owner = owner;
        _paper = paper;
        _imageStore = imageStore;
        owner.AttachMarkdownBodySession(this);
        try
        {
            var presenter = owner.CreateMarkdownBodyView();
            AddPresenter(presenter);
        }
        catch
        {
            owner.DetachMarkdownBodySession(this);
            throw;
        }
    }

    public FrameworkElement View => _root;

    internal MarkdownTextBox? NoteBox { get; set; }
    internal UIElement? CurrentPresenter { get; set; }
    internal ContextMenu? PreviewContextMenu { get; set; }
    internal Action? ShowPreview { get; set; }
    internal int PresenterGeneration { get; set; }
    internal int DeferredWorkGeneration { get; set; }
    internal Action? CancelPresenterInteractions { get; set; }
    internal Action? SettlePendingBodyRebuild { get; set; }
    internal bool ContentDirty { get; set; }
    internal bool ApplyingExternalChange { get; set; }
    internal bool LiveIsScriptCapsule { get; set; }

    internal IReadOnlyList<UIElement> PresenterElements =>
        _root.Children.Cast<UIElement>().ToArray();

    internal void AddPresenter(UIElement presenter)
    {
        if (!_root.Children.Contains(presenter))
        {
            _root.Children.Add(presenter);
        }
        CurrentPresenter = presenter;
    }

    internal void RemovePresenter(UIElement presenter)
    {
        _root.Children.Remove(presenter);
        if (ReferenceEquals(CurrentPresenter, presenter))
        {
            CurrentPresenter = _root.Children.OfType<UIElement>().LastOrDefault();
        }
    }

    internal void ResetPresenterState()
    {
        PresenterGeneration++;
        DeferredWorkGeneration++;
        NoteBox = null;
        CurrentPresenter = null;
        PreviewContextMenu = null;
        ShowPreview = null;
        CancelPresenterInteractions = null;
        SettlePendingBodyRebuild = null;
        ContentDirty = false;
        ApplyingExternalChange = false;
        LiveIsScriptCapsule = false;
        _root.Children.Clear();
    }

    public void Commit()
    {
        if (NoteBox == null || !ContentDirty)
        {
            return;
        }
        _paper.Content = NoteBox.PersistentText;
        ContentDirty = false;
    }

    public void RefreshFromModel() => _owner.RefreshLegacyMarkdownFromModel();

    public void CancelInteractions() => CancelPresenterDeferredWork();

    internal void CancelPresenterDeferredWork()
    {
        var settleRebuild = SettlePendingBodyRebuild;
        SettlePendingBodyRebuild = null;
        settleRebuild?.Invoke();

        DeferredWorkGeneration++;
        CancelPresenterInteractions?.Invoke();
    }

    public void OnThemeChanged(PaperBodyTheme theme) =>
        NoteBox?.RefreshVisualStyle();

    public void OnTypographyChanged(PaperBodyTheme theme) =>
        NoteBox?.RefreshTypography();

    public void OnDpiChanged() =>
        NoteBox?.RefreshImageDecodeForCurrentDpi();

    public void OnVisibilityChanged(bool visible)
    {
        NoteBox?.SetImageRenderingSuspended(!visible);
        if (!visible)
        {
            _imageStore.ReleaseNoteBitmapCache(_paper.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Commit();
        CancelInteractions();
        OnVisibilityChanged(false);
        ResetPresenterState();
        _owner.DetachMarkdownBodySession(this);
    }
}
