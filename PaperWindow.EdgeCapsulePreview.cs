using System.Diagnostics;
using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly EdgeCapsulePreviewInvalidationSource
        _edgeCapsulePreviewInvalidationSource = new();
    private EdgeCapsulePreviewRequest? _edgeCapsulePreviewRequest;

    internal PaperData EdgeCapsulePreviewPaper => _paper;
    internal string EdgeCapsulePreviewPaperId => _paper.Id;
    internal bool IsEdgeCapsulePointerOver =>
        _edgeCapsule.PointerOverSurface;
    internal bool IsEdgeCapsulePreviewOpen =>
        _edgeCapsule.Preview == EdgeCapsulePreviewState.Open;
    internal bool EdgeCapsulePreviewPointerCaptureActive =>
        _edgeCapsuleHost?.IsPreviewPointerCaptureActive == true;

    internal bool CanEnterEdgeCapsulePreview =>
        _controller.State.ExperimentalEdgeCapsuleHoverPreview &&
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        _paper.IsVisible &&
        !IsExperimentalPassive &&
        !_advancedInteractionLocked &&
        HasDeepCapsuleSlotPlacement &&
        !IsDeepCapsuleRetractedIntoMaster &&
        !IsDeepCapsuleSlotRetracting &&
        !IsDeepCapsuleReordering &&
        !_edgeCapsule.PeerReorderActive &&
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

        IEdgeCapsulePreviewProvider? provider = null;
        try
        {
            provider = ResolveEdgeCapsulePreviewProvider();
            var descriptor = provider.Describe(new EdgeCapsulePreviewContext(
                _paper,
                () => _controller.PaperTitleText(_paper),
                !_paper.IsCollapsed,
                CurrentMarkdownTextForEdgeCapsulePreview,
                SetTodoDoneFromEdgeCapsulePreview,
                OpenTodoLinkedTargetFromEdgeCapsulePreview,
                CurrentTodoCheckBoxStyle,
                CurrentPluginStatusForEdgeCapsulePreview,
                OpenExternalFromEdgeCapsulePreview,
                _edgeCapsulePreviewInvalidationSource));
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
                Trace.TraceWarning(
                    "Edge capsule preview provider returned invalid content. " +
                    "PaperId={0}; PaperType={1}; Provider={2}; ContentType={3}",
                    _paper.Id,
                    _paper.Type,
                    provider.GetType().FullName,
                    content?.GetType().FullName ?? "<null>");
                return null;
            }

            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            return new EdgeCapsulePreviewRequest(size, content);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "Edge capsule preview creation failed. " +
                "PaperId={0}; PaperType={1}; Provider={2}; Exception={3}",
                _paper.Id,
                _paper.Type,
                provider?.GetType().FullName ?? "<unresolved>",
                ex);
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
        EnsureDeepCapsuleSlotHost().StagePreviewContent(request.Content);
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

    internal void ClearEdgeCapsulePreviewContent() =>
        _edgeCapsuleHost?.ClearPreviewContent();

    internal EdgeCapsulePreviewSize? CurrentEdgeCapsulePreviewSize =>
        _edgeCapsulePreviewRequest?.Size;

    internal bool TryGetEdgeCapsuleAppliedGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        var frame = _edgeCapsule.AppliedPresentation;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.Bounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return frame.Visible && !frame.Bounds.IsEmpty;
    }

    internal bool IsEdgeCapsuleInteractiveAt(DeviceScreenPoint pointer)
    {
        var frame = _edgeCapsule.AppliedPresentation;
        return frame.Visible &&
            frame.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, pointer);
    }

    internal bool TryGetEdgeCapsuleInteractiveGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        var frame = _edgeCapsule.AppliedPresentation;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.InteractiveBounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return frame.Visible &&
            frame.IsHitTestVisible &&
            !frame.InteractiveBounds.IsEmpty;
    }

    internal void RefreshEdgeCapsuleHoverIntentSettings() =>
        InvalidateEdgeCapsulePointer();

    internal void FlushEdgeCapsulePreviewCompactPresentation()
    {
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure);
    }

}
