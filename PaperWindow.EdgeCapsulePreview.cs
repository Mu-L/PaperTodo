using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly EdgeCapsulePreviewInvalidationSource
        _edgeCapsulePreviewInvalidationSource = new();
    private EdgeCapsulePreviewRequest? _edgeCapsulePreviewRequest;
    private int _edgeCapsulePreviewContentGeneration;
    private EventHandler? _edgeCapsulePreviewDeferredContentRenderHandler;

    internal PaperData EdgeCapsulePreviewPaper => _paper;
    internal string EdgeCapsulePreviewPaperId => _paper.Id;
    internal bool IsEdgeCapsulePointerOver =>
        _edgeCapsule.PointerOverSurface;
    internal bool IsEdgeCapsulePreviewOpen =>
        _edgeCapsule.Preview == EdgeCapsulePreviewState.Open;
    internal bool HasEdgeCapsulePreviewContent =>
        _edgeCapsulePreviewRequest != null ||
        _edgeCapsuleHost?.HasPreviewContent == true;
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
        var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var bodySessionGeneration = _bodySessionGeneration;

        IEdgeCapsulePreviewProvider? provider = null;
        try
        {
            provider = ResolveEdgeCapsulePreviewProvider();
            var context = CreateEdgeCapsulePreviewContext();
            var describeStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var descriptor = provider.Describe(context);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.describe paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"provider={provider.GetType().Name} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(describeStartedAt):F3} " +
                $"deferred={descriptor.DeferContentCreation}");
            if (bodySessionGeneration != _bodySessionGeneration ||
                !CanEnterEdgeCapsulePreview)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.prepare.cancel paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    "phase=describe-invalidated");
                return null;
            }
            var monitor = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
            var size = descriptor.Size.Normalize(
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumWidthDip,
                    monitor.Width - 16),
                Math.Max(
                    EdgeCapsulePreviewSize.MinimumHeightDip,
                    monitor.Height - 16));
            // Native/Web/migration providers may execute arbitrary plugin code or construct a
            // WebView. Paint the host-owned 1.6/1.7 fallback first, then replace it only after the
            // visual transaction has produced a committed compositor frame. Creation failure keeps
            // this useful fallback instead of leaving a permanent loading ellipsis.
            var deferProviderContent = descriptor.DeferContentCreation;
            var contentStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var content = deferProviderContent
                ? BuildPluginCapsuleEdgePreviewContent(context, size)
                : descriptor.CreateContent(size);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.content paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"provider={provider.GetType().Name} deferred={deferProviderContent} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(contentStartedAt):F3} " +
                $"type={content?.GetType().Name ?? "<null>"}");
            if (bodySessionGeneration != _bodySessionGeneration ||
                !CanEnterEdgeCapsulePreview ||
                content == null ||
                !IsValidEdgeCapsulePreviewContent(content))
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
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.ready paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(prepareStartedAt):F3} " +
                $"size={size.WidthDip:F1}x{size.HeightDip:F1} deferred={deferProviderContent}");
            return new EdgeCapsulePreviewRequest(
                size,
                content,
                descriptor.SetVisibility,
                descriptor.PrepareForActivation,
                deferProviderContent
                    ? () => descriptor.CreateContent(size)
                    : null);
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.prepare.fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(prepareStartedAt):F3} " +
                $"provider={provider?.GetType().Name ?? "<unresolved>"} " +
                $"exception={ex.GetType().Name}");
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

    private EdgeCapsulePreviewContext CreateEdgeCapsulePreviewContext() =>
        new(
            _paper,
            () => _controller.PaperTitleText(_paper),
            !_paper.IsCollapsed,
            CurrentMarkdownTextForEdgeCapsulePreview,
            SetTodoDoneFromEdgeCapsulePreview,
            OpenTodoLinkedTargetFromEdgeCapsulePreview,
            CurrentTodoCheckBoxStyle,
            CurrentPluginStatusForEdgeCapsulePreview,
            OpenExternalFromEdgeCapsulePreview,
            _edgeCapsulePreviewInvalidationSource);

    private bool IsValidEdgeCapsulePreviewContent(FrameworkElement content) =>
        content is not Window &&
        (content.Parent == null ||
         _edgeCapsuleHost?.OwnsPreviewContent(content) == true);

    // Built-in papers and protocol 1.8 plugin mini/fallback adapters enter through the same seam;
    // queue, host, transition and input policy stay independent of the content provider.
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
        if (_paper.Type == PaperTypes.Note &&
            !IsCurrentBodyProviderMarkdown &&
            !_bodyFailed &&
            _bodyDescriptor != null)
        {
            return new PluginEdgeCapsulePreviewProvider(this);
        }
        return DefaultEdgeCapsulePreviewProvider.Instance;
    }

    internal bool SetEdgeCapsulePreviewOpen(
        EdgeCapsulePreviewRequest request,
        bool animate)
    {
        var openStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: true),
                EdgeCapsuleDirty.None))
        {
            return false;
        }

        if (!ReferenceEquals(_edgeCapsulePreviewRequest, request))
        {
            var previousRequest = _edgeCapsulePreviewRequest;
            var previousGeneration = _edgeCapsulePreviewContentGeneration;
            NotifyEdgeCapsulePreviewVisibility(previousRequest, visible: false);
            if (previousGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, previousRequest))
            {
                if (ReferenceEquals(
                        _edgeCapsulePreviewRequest,
                        previousRequest) &&
                    IsEdgeCapsulePreviewOpen)
                {
                    _ = DispatchEdgeCapsuleIntent(
                        EdgeCapsuleIntent.PreviewChanged(open: false),
                        EdgeCapsuleDirty.None);
                }
                return false;
            }
        }
        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        var contentGeneration = ++_edgeCapsulePreviewContentGeneration;
        _edgeCapsulePreviewRequest = request;
        var previewContentWidth = Math.Max(
            1,
            request.Size.WidthDip - CapsuleCloseWidth - WindowChromeMargin);
        var previewContentHeight = Math.Max(
            1,
            request.Size.HeightDip - WindowChromeMargin * 2);
        var host = EnsureDeepCapsuleSlotHost();
        var stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var staged = host.StagePreviewContent(
            request.Content,
            previewContentWidth,
            previewContentHeight);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.open.stage paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt):F3} " +
            $"content={request.Content.GetType().Name} staged={staged} " +
            $"layoutValid={request.Content.IsMeasureValid && request.Content.IsArrangeValid}");
        var requestStillCurrent =
            _windowLifecycle == PaperWindowLifecycleState.Alive &&
            contentGeneration == _edgeCapsulePreviewContentGeneration &&
            ReferenceEquals(_edgeCapsulePreviewRequest, request) &&
            IsEdgeCapsulePreviewOpen &&
            _controller.IsEdgeCapsulePreviewOwner(this);
        if (!staged || !requestStillCurrent)
        {
            if (ReferenceEquals(_edgeCapsulePreviewRequest, request))
            {
                if (host.OwnsPreviewContent(request.Content))
                {
                    host.ClearPreviewContent();
                }
                if (contentGeneration != _edgeCapsulePreviewContentGeneration ||
                    !ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    return false;
                }
                _edgeCapsulePreviewRequest = null;
                _edgeCapsulePreviewContentGeneration++;
                if (IsEdgeCapsulePreviewOpen)
                {
                    _ = DispatchEdgeCapsuleIntent(
                        EdgeCapsuleIntent.PreviewChanged(open: false),
                        EdgeCapsuleDirty.None);
                }
            }
            return false;
        }
        if (request.CreateDeferredContent == null)
        {
            var visibilityStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            NotifyEdgeCapsulePreviewVisibility(request, visible: true);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.open.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=true ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3}");
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                contentGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                !IsEdgeCapsulePreviewOpen ||
                !_controller.IsEdgeCapsulePreviewOwner(this))
            {
                if (ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    if (host.OwnsPreviewContent(request.Content))
                    {
                        host.ClearPreviewContent();
                    }
                    if (contentGeneration == _edgeCapsulePreviewContentGeneration &&
                        ReferenceEquals(_edgeCapsulePreviewRequest, request))
                    {
                        NotifyEdgeCapsulePreviewVisibility(request, visible: false);
                    }
                }
                if (contentGeneration == _edgeCapsulePreviewContentGeneration &&
                    ReferenceEquals(_edgeCapsulePreviewRequest, request))
                {
                    _edgeCapsulePreviewRequest = null;
                    _edgeCapsulePreviewContentGeneration++;
                    if (IsEdgeCapsulePreviewOpen)
                    {
                        _ = DispatchEdgeCapsuleIntent(
                            EdgeCapsuleIntent.PreviewChanged(open: false),
                            EdgeCapsuleDirty.None);
                    }
                }
                return false;
            }
        }

        // Preview open/transfer/close is one cross-window visual transaction. The owner state is
        // staged now; the immediate ArrangeDeepCapsules call stages every peer placement into the
        // same transaction, then one Send-priority commit creates all transitions before rendering.
        _controller.BeginEdgeCapsuleVisualTransaction(this);
        _ = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
        QueueDeferredEdgeCapsulePreviewContent(
            request,
            contentGeneration,
            _edgeCapsule.NativeBatchCommitVersion);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"preview.open.queued paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(openStartedAt):F3} " +
            $"animate={animate} deferred={request.CreateDeferredContent != null}");
        return true;
    }

    private void QueueDeferredEdgeCapsulePreviewContent(
        EdgeCapsulePreviewRequest request,
        int contentGeneration,
        int queuedAtNativeBatchCommitVersion)
    {
        if (request.CreateDeferredContent is not { } createContent)
        {
            return;
        }
        var deferredQueuedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();

        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        EventHandler? renderHandler = null;
        renderHandler = (_, _) =>
        {
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                contentGeneration != _edgeCapsulePreviewContentGeneration ||
                !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                !IsEdgeCapsulePreviewOpen ||
                !_controller.IsEdgeCapsulePreviewOwner(this))
            {
                if (renderHandler != null)
                {
                    CompositionTarget.Rendering -= renderHandler;
                }
                if (ReferenceEquals(
                        _edgeCapsulePreviewDeferredContentRenderHandler,
                        renderHandler))
                {
                    _edgeCapsulePreviewDeferredContentRenderHandler = null;
                }
                return;
            }

            // The fallback must have belonged to a successfully committed native batch before
            // this Rendering can count as its first compositor pass. A failed transaction keeps
            // the handler armed across coordinated retries; exhaustion leaves it armed until a
            // later explicit successful replay or request cancellation.
            if (_edgeCapsule.NativeBatchCommitVersion ==
                queuedAtNativeBatchCommitVersion)
            {
                return;
            }

            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.deferred.first-frame paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"waitMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(deferredQueuedAt):F3}");

            if (renderHandler != null)
            {
                CompositionTarget.Rendering -= renderHandler;
            }
            if (ReferenceEquals(
                    _edgeCapsulePreviewDeferredContentRenderHandler,
                    renderHandler))
            {
                _edgeCapsulePreviewDeferredContentRenderHandler = null;
            }

            // Run after the Render-priority event returns, so the host-owned fallback has
            // completed one genuine WPF composition pass before arbitrary plugin code can block UI.
            _ = Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        return;
                    }

                    FrameworkElement? content;
                    var createStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    try
                    {
                        content = createContent();
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError(
                            "Deferred edge capsule preview creation failed. " +
                            "PaperId={0}; PaperType={1}; Exception={2}",
                            _paper.Id,
                            _paper.Type,
                            ex);
                        return;
                    }
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.create paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(createStartedAt):F3} " +
                        $"content={content?.GetType().Name ?? "<null>"}");

                    // Plugin code is allowed to pump messages. A close or another transfer may have
                    // invalidated this request while CreateContent was on the stack.
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        return;
                    }

                    if (content == null ||
                        !IsValidEdgeCapsulePreviewContent(content))
                    {
                        Trace.TraceWarning(
                            "Deferred edge capsule preview provider returned invalid content. " +
                            "PaperId={0}; PaperType={1}; ContentType={2}",
                            _paper.Id,
                            _paper.Type,
                            content?.GetType().FullName ?? "<null>");
                        return;
                    }

                    content.HorizontalAlignment = HorizontalAlignment.Stretch;
                    content.VerticalAlignment = VerticalAlignment.Stretch;
                    var previewContentWidth = Math.Max(
                        1,
                        request.Size.WidthDip - CapsuleCloseWidth - WindowChromeMargin);
                    var previewContentHeight = Math.Max(
                        1,
                        request.Size.HeightDip - WindowChromeMargin * 2);
                    var host = EnsureDeepCapsuleSlotHost();
                    var replaceStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    if (!host.ReplacePreviewContent(
                            request.Content,
                            content,
                            previewContentWidth,
                            previewContentHeight))
                    {
                        return;
                    }
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.replace paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(replaceStartedAt):F3} " +
                        $"layoutValid={content.IsMeasureValid && content.IsArrangeValid}");

                    // Stage/Prepare and WPF dependency-property callbacks may re-enter application
                    // code. Only the still-current request may adopt and reveal this tree.
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        if (ReferenceEquals(_edgeCapsulePreviewRequest, request) &&
                            host.OwnsPreviewContent(content))
                        {
                            host.ClearPreviewContent();
                        }
                        return;
                    }

                    var resolvedRequest = request with
                    {
                        Content = content,
                        CreateDeferredContent = null
                    };
                    _edgeCapsulePreviewRequest = resolvedRequest;
                    if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                        contentGeneration != _edgeCapsulePreviewContentGeneration ||
                        !ReferenceEquals(_edgeCapsulePreviewRequest, resolvedRequest) ||
                        !IsEdgeCapsulePreviewOpen ||
                        !_controller.IsEdgeCapsulePreviewOwner(this))
                    {
                        if (ReferenceEquals(
                                _edgeCapsulePreviewRequest,
                                resolvedRequest) &&
                            host.OwnsPreviewContent(content))
                        {
                            host.ClearPreviewContent();
                        }
                        return;
                    }
                    var visibilityStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
                    NotifyEdgeCapsulePreviewVisibility(resolvedRequest, visible: true);
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.deferred.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3} " +
                        $"totalWaitMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(deferredQueuedAt):F3}");
                }),
                DispatcherPriority.Background);
        };
        _edgeCapsulePreviewDeferredContentRenderHandler = renderHandler;
        CompositionTarget.Rendering += renderHandler;
    }

    private void CancelDeferredEdgeCapsulePreviewContentRenderWait()
    {
        var handler = _edgeCapsulePreviewDeferredContentRenderHandler;
        _edgeCapsulePreviewDeferredContentRenderHandler = null;
        if (handler != null)
        {
            CompositionTarget.Rendering -= handler;
        }
    }

    internal void SetEdgeCapsulePreviewClosed(bool animate)
    {
        if (!IsEdgeCapsulePreviewOpen)
        {
            _ = TryReleaseEdgeCapsulePreviewRequest();
            return;
        }

        if (!DispatchEdgeCapsuleIntent(
                EdgeCapsuleIntent.PreviewChanged(open: false),
                EdgeCapsuleDirty.None))
        {
            return;
        }

        if (!TryReleaseEdgeCapsulePreviewRequest())
        {
            return;
        }
        _controller.BeginEdgeCapsuleVisualTransaction(this);
        _ = TryStageEdgeCapsuleVisualTransaction(
            animate,
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleLayout.SlotMoveMilliseconds,
            refreshLayout: true);
    }

    internal void ClearEdgeCapsulePreviewContent()
    {
        if (!TryReleaseEdgeCapsulePreviewRequest())
        {
            return;
        }
        _edgeCapsuleHost?.ClearPreviewContent();
    }

    private bool TryReleaseEdgeCapsulePreviewRequest()
    {
        CancelDeferredEdgeCapsulePreviewContentRenderWait();
        var request = _edgeCapsulePreviewRequest;
        var generation = ++_edgeCapsulePreviewContentGeneration;
        NotifyEdgeCapsulePreviewVisibility(request, visible: false);
        if (generation != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request))
        {
            return false;
        }
        _edgeCapsulePreviewRequest = null;
        return true;
    }

    internal void ResetEdgeCapsulePreviewForBodySessionChange()
    {
        if (!IsEdgeCapsulePreviewOpen &&
            _edgeCapsulePreviewRequest == null &&
            _edgeCapsuleHost?.HasPreviewContent != true)
        {
            return;
        }

        SetEdgeCapsulePreviewClosed(animate: false);
        FlushEdgeCapsulePreviewCompactPresentation();
        ClearEdgeCapsulePreviewContent();
    }

    private static void NotifyEdgeCapsulePreviewVisibility(
        EdgeCapsulePreviewRequest? request,
        bool visible)
    {
        try
        {
            request?.SetVisibility?.Invoke(visible);
        }
        catch
        {
            // Mini presentation lifecycle is optional and cannot disable the paper body.
        }
    }

    private void PrepareEdgeCapsulePreviewForActivation()
    {
        RestorePrewarmedPluginBodyForActivation();
        try
        {
            _edgeCapsulePreviewRequest?.PrepareForActivation?.Invoke();
        }
        catch
        {
            // A migration hand-off cannot block the normal paper activation path.
        }
    }

    internal EdgeCapsulePreviewSize? CurrentEdgeCapsulePreviewSize =>
        _edgeCapsulePreviewRequest?.Size;

    internal bool TryGetEdgeCapsuleAppliedGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        var frame = EdgeCapsulePresentationFrame.Hidden;
        var hasFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out frame) == true;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.Bounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return hasFrame && frame.Visible && !frame.Bounds.IsEmpty;
    }

    internal bool IsEdgeCapsuleInteractiveAt(DeviceScreenPoint pointer)
    {
        var frame = EdgeCapsulePresentationFrame.Hidden;
        return _edgeCapsuleHost?.TryGetAppliedPresentation(out frame) == true &&
            frame.Visible &&
            frame.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(frame.InteractiveBounds, pointer);
    }

    internal bool TryGetEdgeCapsuleInteractiveGeometry(
        out EdgeCapsulePreviewScreenGeometry geometry)
    {
        var frame = EdgeCapsulePresentationFrame.Hidden;
        var hasFrame = _edgeCapsuleHost?.TryGetAppliedPresentation(
            out frame) == true;
        geometry = new EdgeCapsulePreviewScreenGeometry(
            frame.InteractiveBounds,
            frame.DpiScaleX,
            frame.DpiScaleY);
        return hasFrame && frame.Visible &&
            frame.IsHitTestVisible &&
            !frame.InteractiveBounds.IsEmpty;
    }

    internal void RefreshEdgeCapsuleHoverIntentSettings()
    {
        if (_controller.State.ExperimentalEdgeCapsuleHoverPreview)
        {
            ScheduleMigratedPluginBodyPreviewWarmup();
        }
        else
        {
            RestorePrewarmedPluginBodyForActivation("preview-disabled");
        }
        InvalidateEdgeCapsulePointer();
    }

    internal void FlushEdgeCapsulePreviewCompactPresentation()
    {
        if (TryStageEdgeCapsuleVisualTransaction(
                animate: false,
                EdgeCapsuleTransitionReason.Preview,
                EdgeCapsuleLayout.SlotMoveMilliseconds,
                refreshLayout: true))
        {
            return;
        }

        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.Preview,
            EdgeCapsuleDirty.Presentation |
            EdgeCapsuleDirty.Measure);
    }

}
