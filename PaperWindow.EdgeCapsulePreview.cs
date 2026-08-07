using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private EdgeCapsulePreviewRequest? _edgeCapsulePreviewRequest;

    internal PaperData EdgeCapsulePreviewPaper => _paper;
    internal string EdgeCapsulePreviewPaperId => _paper.Id;
    internal bool IsEdgeCapsulePointerOver =>
        _edgeCapsule.PointerOverSurface;
    internal bool IsEdgeCapsulePreviewOpen =>
        _edgeCapsule.Preview == EdgeCapsulePreviewState.Open;
    internal bool EdgeCapsulePreviewInteractionActive =>
        _edgeCapsuleHost?.IsPreviewInteractionActive == true;

    internal bool CanEnterEdgeCapsulePreview =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        _paper.IsVisible &&
        HasDeepCapsuleSlotPlacement &&
        !IsDeepCapsuleRetractedIntoMaster &&
        !IsDeepCapsuleSlotRetracting &&
        !IsDeepCapsuleReordering &&
        !IsDeepCapsuleDockingHandoff &&
        (!_edgeCapsule.ContextMenuOpen || IsEdgeCapsulePreviewOpen) &&
        (EdgeCapsuleGesture == EdgeCapsuleGestureState.Idle ||
         (IsEdgeCapsulePreviewOpen &&
          EdgeCapsuleGesture == EdgeCapsuleGestureState.PendingClick)) &&
        (EdgeCapsuleSlot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.ExpandedReserved);

    internal EdgeCapsulePreviewRequest? PrepareEdgeCapsulePreview()
    {
        if (!CanEnterEdgeCapsulePreview)
        {
            return null;
        }

        try
        {
            var provider = ResolveEdgeCapsulePreviewProvider();
            var descriptor = provider.Describe(new EdgeCapsulePreviewContext(
                _paper,
                () => _controller.PaperTitleText(_paper),
                !_paper.IsCollapsed,
                OpenPaperFromEdgeCapsulePreview,
                CurrentMarkdownTextForEdgeCapsulePreview,
                SetTodoDoneFromEdgeCapsulePreview,
                CurrentTodoCheckBoxStyle,
                CurrentPluginStatusForEdgeCapsulePreview,
                OpenExternalFromEdgeCapsulePreview));
            var monitor = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var size = descriptor.Size.Normalize(
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumWidthDip,
                    monitor.Width - 16),
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumHeightDip,
                    monitor.Height - 16));
            var content = descriptor.CreateContent(size);
            if (content == null ||
                content is Window ||
                content.Parent != null)
            {
                return null;
            }

            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            return new EdgeCapsulePreviewRequest(size, content);
        }
        catch
        {
            return null;
        }
    }

    // Built-in papers and the plugin fallback all enter through the same internal seam. The
    // public plugin protocol remains 1.7; a later adapter can replace only this resolver branch.
    private IEdgeCapsulePreviewProvider ResolveEdgeCapsulePreviewProvider()
    {
        if (_paper.Type == PaperTypes.Todo)
        {
            return TodoEdgeCapsulePreviewProvider.Instance;
        }
        if (_paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown)
        {
            return MarkdownEdgeCapsulePreviewProvider.Instance;
        }
        return DefaultEdgeCapsulePreviewProvider.Instance;
    }

    internal bool SetEdgeCapsulePreviewOpen(
        EdgeCapsulePreviewRequest request,
        bool animate)
    {
        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: true),
                EdgeCapsuleDirty.None))
        {
            return false;
        }

        _edgeCapsulePreviewRequest = request;
        EnsureDeepCapsuleSlotHost().SetPreviewContent(request.Content);
        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
        return true;
    }

    internal void SetEdgeCapsulePreviewClosed(bool animate)
    {
        if (!IsEdgeCapsulePreviewOpen)
        {
            _edgeCapsulePreviewRequest = null;
            return;
        }

        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: false),
                EdgeCapsuleDirty.None))
        {
            return;
        }

        _edgeCapsulePreviewRequest = null;
        RequestEdgeCapsulePresentation(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
    }

    internal EdgeCapsulePreviewSize? CurrentEdgeCapsulePreviewSize =>
        _edgeCapsulePreviewRequest?.Size;

    internal bool TryGetEdgeCapsuleAppliedBounds(
        out DeviceScreenRect bounds)
    {
        var frame = _edgeCapsule.AppliedPresentation;
        bounds = frame.Bounds;
        return frame.Visible && !bounds.IsEmpty;
    }

    internal void FlushEdgeCapsulePreviewCompactPresentation()
    {
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure);
    }

    private void OpenPaperFromEdgeCapsulePreview()
    {
        _controller.CloseEdgeCapsulePreviewForActivation(this);
        ActivateFromEdgeCapsulePreview();
    }
}
