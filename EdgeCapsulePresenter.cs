using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Sole owner of desired model, target plan, transition, applied frame and deferred work. Both
/// deferred invalidation and synchronous Flush execute the same reconcile callback.
/// </summary>
internal sealed class EdgeCapsulePresenter
{
    // Give a temporarily invalid HWND real retry opportunities at every refresh rate, while
    // bounding both high-refresh call volume and total retry lifetime. The failure count includes
    // the initial failed apply, so 3 means at least two genuine retries before time can expire.
    private const int MinimumApplyFailureCountBeforeTimeout = 3;
    private const int MaximumApplyFailureCount = 6;
    private const int MaximumApplyRetryMilliseconds = 120;
    private static readonly long MaximumApplyRetryTimestampTicks =
        Math.Max(
            1,
            (long)Math.Ceiling(
                Stopwatch.Frequency *
                MaximumApplyRetryMilliseconds / 1000.0));
    private const EdgeCapsuleDirty PresentationWorkMask =
        EdgeCapsuleDirty.Presentation |
        EdgeCapsuleDirty.Measure |
        EdgeCapsuleDirty.Frame |
        EdgeCapsuleDirty.ApplyRetry |
        EdgeCapsuleDirty.DisplayMetrics;

    private readonly record struct PresentationResult(
        bool Applied,
        bool NeedsNextFrame);

    private EdgeCapsuleDirty _dirty;
    private bool _reconcileScheduled;
    private int _reconcileGeneration;
    private EdgeCapsuleFrameScheduler? _frameScheduler;
    private bool _frameSchedulerActive;
    private Dispatcher? _dispatcher;
    private Func<EdgeCapsuleDirty, EdgeCapsuleDirty>? _reconcile;
    private EdgeCapsuleLayoutSnapshot? _layoutSnapshot;
    private bool _hasFramePointerOverride;
    private DeviceScreenPoint? _framePointerOverride;
    private int _forceApplyVersion;
    private int _appliedForceApplyVersion;
    private long _applyRetryStartedAtTimestamp;
    private int _applyFailureCount;
    private bool _applyRetryExhausted;
    private Action<bool>? _presentationSettleCallback;
    private EdgeCapsuleMotion _pendingMotion =
        EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);

    private EdgeCapsuleModel Model { get; set; } = EdgeCapsuleModel.Initial;
    public EdgeCapsuleState State => Model.State;
    public EdgeCapsuleDragSession? DragSession => Model.DragSession;
    public EdgeCapsulePlacement Placement => Model.Placement;
    public EdgeCapsulePreviewState Preview => Model.Preview;
    public bool PointerOverSurface => Model.PointerOverSurface;
    public bool ContextMenuOpen => Model.ContextMenuOpen;
    public bool PeerReorderActive => Model.PeerReorderActive;
    private EdgeCapsulePresentationPlan TargetPlan { get; set; } =
        EdgeCapsulePresentationPlan.Hidden;
    private EdgeCapsuleTargetPresentation TargetPresentation => TargetPlan.Docked;
    public EdgeCapsuleFloatingShape FloatingShape => TargetPlan.Floating;
    public EdgeCapsulePresentationFrame AppliedPresentation { get; private set; } =
        EdgeCapsulePresentationFrame.Hidden;
    public int AppliedPresentationVersion { get; private set; }
    public DeviceScreenPoint? LastPointerSample { get; private set; }
    private EdgeCapsuleTransition? Transition { get; set; }

    public EdgeCapsuleDispatchResult Dispatch(
        EdgeCapsuleIntent intent,
        [CallerMemberName] string reason = "")
    {
        var result = EdgeCapsuleReducer.Reduce(Model, intent);
        if (!result.Accepted)
        {
            Debug.Fail($"{result.Error} ({reason})");
            return result;
        }
        Model = result.Model;
        return result;
    }

    public EdgeCapsuleCaptureAction HandleCaptureLost(EdgeCapsuleCaptureLoss captureLoss) =>
        Dispatch(EdgeCapsuleIntent.CaptureLost(captureLoss)).CaptureAction;

    public void RequestPresentation(EdgeCapsuleMotion motion)
    {
        // An explicit Snap owns the batch. Otherwise Animate outranks passive Preserve; measure
        // and display refreshes cannot downgrade an interaction transition already requested.
        if (motion.Kind == EdgeCapsuleMotionKind.Snap ||
            _pendingMotion.Kind == EdgeCapsuleMotionKind.Preserve ||
            (motion.Kind == EdgeCapsuleMotionKind.Animate &&
                _pendingMotion.Kind != EdgeCapsuleMotionKind.Snap))
        {
            _pendingMotion = motion;
        }
    }

    public void ForceApplyCurrentPresentation()
    {
        ResetApplyRetryWindow();
        unchecked
        {
            _forceApplyVersion++;
        }
    }

    public void NotifyWhenPresentationSettled(Action<bool> callback)
    {
        _presentationSettleCallback = callback;
    }

    public void ClearPresentationSettleNotification()
    {
        _presentationSettleCallback = null;
    }

    public EdgeCapsuleDirty Reconcile(
        EdgeCapsuleDirty dirty,
        Func<EdgeCapsuleLayoutSnapshot> captureLayout,
        Func<DeviceScreenPoint?> capturePointer,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long? nowTimestamp = null)
    {
        var remaining = EdgeCapsuleDirty.None;
        var now = nowTimestamp ?? Stopwatch.GetTimestamp();
        var pointer = _hasFramePointerOverride
            ? _framePointerOverride
            : capturePointer();
        LastPointerSample = pointer;

        // The fixed host is larger than the current visible surface. Every transition frame
        // resamples the real physical interactive rectangle, so transparent reserve pixels never
        // become hover intent and a shrinking surface can retarget in either direction.
        if ((dirty & EdgeCapsuleDirty.Frame) != 0)
        {
            dirty |= EdgeCapsuleDirty.Pointer;
        }
        if ((dirty & EdgeCapsuleDirty.Pointer) != 0 && SamplePointer(pointer))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Pointer,
                EdgeCapsuleLayout.HorizontalResizeMilliseconds));
            dirty |= EdgeCapsuleDirty.Presentation;
        }

        if ((dirty & EdgeCapsuleDirty.Measure) != 0)
        {
            var displayMetrics = (dirty & EdgeCapsuleDirty.DisplayMetrics) != 0;
            if (State.Gesture is
                EdgeCapsuleGestureState.DockedReordering or
                EdgeCapsuleGestureState.FloatingTransfer or
                EdgeCapsuleGestureState.FloatingReordering)
            {
                remaining |= EdgeCapsuleDirty.Measure;
                if (displayMetrics)
                {
                    // Unlike a title-only measure, display geometry cannot be force-replayed from
                    // the previous drag snapshot. Keep this batch intact for gesture completion.
                    Transition = null;
                    remaining |= EdgeCapsuleDirty.Presentation |
                        EdgeCapsuleDirty.DisplayMetrics;
                    return remaining;
                }
            }
            else
            {
                _layoutSnapshot = captureLayout();
                RequestPresentation(EdgeCapsuleMotion.Preserve(
                    displayMetrics
                        ? EdgeCapsuleTransitionReason.DisplayMetrics
                        : EdgeCapsuleTransitionReason.Measure));
                dirty |= EdgeCapsuleDirty.Presentation;
            }
        }

        if ((dirty & (EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Frame)) == 0)
        {
            return remaining;
        }

        var layout = _layoutSnapshot ?? captureLayout();
        _layoutSnapshot = layout;
        var result = ReconcilePresentation(layout, apply, now);
        if (!result.Applied)
        {
            remaining |= EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.ApplyRetry;
            return remaining;
        }
        if (result.NeedsNextFrame)
        {
            remaining |= EdgeCapsuleDirty.Frame;
        }

        // The frame just committed can change the physical hit rectangle. Re-evaluate once from
        // that exact frame; if intent changes, retarget from the frame already on screen.
        if (SamplePointer(pointer))
        {
            RequestPresentation(EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Pointer,
                EdgeCapsuleLayout.HorizontalResizeMilliseconds));
            var retarget = ReconcilePresentation(layout, apply, now);
            if (!retarget.Applied)
            {
                remaining |= EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.ApplyRetry;
            }
            if (retarget.NeedsNextFrame)
            {
                remaining |= EdgeCapsuleDirty.Frame;
            }
        }

        return remaining;
    }

    private PresentationResult ReconcilePresentation(
        EdgeCapsuleLayoutSnapshot layout,
        Func<EdgeCapsulePresentationFrame, bool> apply,
        long nowTimestamp)
    {
        var forceApplyVersion = _forceApplyVersion;
        var forceApply = _appliedForceApplyVersion != forceApplyVersion;
        var plan = EdgeCapsuleTargetPlanner.Calculate(Model, layout);
        var targetChanged = plan != TargetPlan;
        var motionMustRebase = _pendingMotion.Kind == EdgeCapsuleMotionKind.Snap &&
            Transition.HasValue;
        if (targetChanged || motionMustRebase)
        {
            if (targetChanged)
            {
                ResetApplyRetryWindow();
            }
            Transition = EdgeCapsuleTransitionPolicy.Create(
                AppliedPresentation,
                plan.Docked,
                _pendingMotion,
                Transition.HasValue,
                nowTimestamp,
                Stopwatch.Frequency);
            TargetPlan = plan;
        }

        _pendingMotion = EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.State);
        var sample = Transition is { } active
            ? EdgeCapsuleTransitionPolicy.Sample(active, nowTimestamp)
            : new EdgeCapsuleTransitionSample(TargetPresentation.ToFrame(), true);
        var shouldApply = forceApply || sample.Frame != AppliedPresentation || targetChanged;
        var applied = !shouldApply || apply(sample.Frame);
        if (applied && shouldApply)
        {
            SetAppliedPresentation(sample.Frame);
            _appliedForceApplyVersion = forceApplyVersion;
            ResetApplyRetryWindow();
        }

        if (!applied)
        {
            RegisterApplyFailure(nowTimestamp);
            return new PresentationResult(false, Transition.HasValue);
        }

        if (sample.IsComplete)
        {
            Transition = null;
        }

        var retractionCompleted = sample.IsComplete && State.Slot is (
            EdgeCapsuleSlotState.RetractingCollapsed or
            EdgeCapsuleSlotState.RetractingExpanded);
        if (retractionCompleted)
        {
            var reduction = Dispatch(EdgeCapsuleIntent.RetractionCompleted());
            if (reduction.Accepted)
            {
                TargetPlan = EdgeCapsulePresentationPlan.Hidden;
                var hidden = EdgeCapsulePresentationFrame.Hidden;
                if (apply(hidden))
                {
                    SetAppliedPresentation(hidden);
                }
            }
        }

        return new PresentationResult(true, Transition.HasValue);
    }

    public void Invalidate(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);
        Schedule();
    }

    public void Flush(
        EdgeCapsuleDirty dirty,
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        _dirty |= dirty;
        Configure(dispatcher, reconcile);
        _reconcileGeneration++;
        _reconcileScheduled = false;
        RunReconcile();
    }

    public void ClearDeferredWork()
    {
        _dirty = EdgeCapsuleDirty.None;
        _reconcileScheduled = false;
        _reconcileGeneration++;
        _presentationSettleCallback = null;
        ResetApplyRetryWindow();
        StopFrameScheduler();
    }

    public void CancelTransition()
    {
        Transition = null;
        StopFrameScheduler();
    }

    public void ResetPresentation()
    {
        CancelTransition();
        TargetPlan = EdgeCapsulePresentationPlan.Hidden;
        SetAppliedPresentation(EdgeCapsulePresentationFrame.Hidden);
        LastPointerSample = null;
        _layoutSnapshot = null;
        _appliedForceApplyVersion = _forceApplyVersion;
        _pendingMotion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);
        ClearDeferredWork();
    }

    public void Reset()
    {
        Dispatch(EdgeCapsuleIntent.ResetModel());
        ResetPresentation();
    }

    private bool SamplePointer(DeviceScreenPoint? pointer)
    {
        var over = pointer.HasValue &&
            AppliedPresentation.IsHitTestVisible &&
            EdgeCapsuleGeometry.Contains(
                AppliedPresentation.InteractiveBounds,
                pointer.Value);
        return Dispatch(EdgeCapsuleIntent.PointerSampled(over)).Changed;
    }

    private void SetAppliedPresentation(
        EdgeCapsulePresentationFrame presentation)
    {
        if (AppliedPresentation == presentation)
        {
            return;
        }

        AppliedPresentation = presentation;
        unchecked
        {
            AppliedPresentationVersion++;
        }
    }

    private bool NeedsPointerTracking =>
        (State.Slot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.ExpandedReserved) &&
        (PointerOverSurface ||
         Preview == EdgeCapsulePreviewState.Open) &&
        State.Gesture == EdgeCapsuleGestureState.Idle &&
        !ContextMenuOpen &&
        !_applyRetryExhausted;

    private void RegisterApplyFailure(long nowTimestamp)
    {
        if (_applyRetryExhausted)
        {
            return;
        }
        if (_applyRetryStartedAtTimestamp == 0)
        {
            _applyRetryStartedAtTimestamp = nowTimestamp;
        }
        if (_applyFailureCount < int.MaxValue)
        {
            _applyFailureCount++;
        }
    }

    private void ResetApplyRetryWindow()
    {
        _applyRetryStartedAtTimestamp = 0;
        _applyFailureCount = 0;
        _applyRetryExhausted = false;
    }

    private bool ApplyRetryExpired(long nowTimestamp)
    {
        if (_applyRetryExhausted ||
            _applyFailureCount >= MaximumApplyFailureCount)
        {
            return true;
        }
        if (_applyFailureCount < MinimumApplyFailureCountBeforeTimeout ||
            _applyRetryStartedAtTimestamp == 0)
        {
            return false;
        }
        return Math.Max(0, nowTimestamp - _applyRetryStartedAtTimestamp) >=
            MaximumApplyRetryTimestampTicks;
    }

    private void ExhaustApplyRetryWindow()
    {
        _applyRetryStartedAtTimestamp = 0;
        _applyRetryExhausted = true;
    }

    private void Configure(
        Dispatcher dispatcher,
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile)
    {
        if (_dispatcher != null && !ReferenceEquals(_dispatcher, dispatcher))
        {
            StopFrameScheduler();
            _frameScheduler = null;
            _layoutSnapshot = null;
        }

        _dispatcher = dispatcher;
        _reconcile = reconcile;
    }

    private void Schedule()
    {
        if (_reconcileScheduled || _dispatcher == null || _reconcile == null)
        {
            return;
        }

        _reconcileScheduled = true;
        var generation = ++_reconcileGeneration;
        _dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (generation != _reconcileGeneration)
                {
                    return;
                }
                _reconcileScheduled = false;
                RunReconcile();
            }),
            DispatcherPriority.Loaded);

        // Render runs above Loaded. Without a shared barrier, the first queue member whose Loaded
        // callback reconciles can advance one composition frame before later siblings reconcile.
        // Append the barrier release after this callback; the scheduler's generation keeps the
        // latest release behind every sibling scheduled in the same dispatcher batch.
        _frameScheduler ??= EdgeCapsuleFrameScheduler.For(_dispatcher);
        _frameScheduler.DeferRenderingUntilLoadedBatchDrains();
    }

    private void RunReconcile()
    {
        if (_reconcile == null)
        {
            return;
        }
        var dirty = _dirty;
        _dirty = EdgeCapsuleDirty.None;
        var remaining = _reconcile(dirty);
        var needsFrame = (remaining & EdgeCapsuleDirty.Frame) != 0;
        var needsApplyRetry = (remaining & EdgeCapsuleDirty.ApplyRetry) != 0;
        _dirty |= remaining & ~EdgeCapsuleDirty.Frame;
        var applyRetryExpired = needsApplyRetry &&
            ApplyRetryExpired(Stopwatch.GetTimestamp());
        if (applyRetryExpired)
        {
            // Keep Presentation dirty for a later explicit/display invalidation, but stop the
            // bounded attempt/time retry so a permanently invalid HWND cannot spin the UI thread.
            _dirty &= ~EdgeCapsuleDirty.ApplyRetry;
            Transition = null;
            needsFrame = false;
            ExhaustApplyRetryWindow();
        }

        if (needsFrame ||
            (needsApplyRetry && !applyRetryExpired) ||
            (!applyRetryExpired && NeedsPointerTracking))
        {
            StartFrameScheduler();
        }
        else if (!Transition.HasValue)
        {
            StopFrameScheduler();
        }

        if (!Transition.HasValue)
        {
            if (applyRetryExpired)
            {
                CompletePresentationSettle(success: false);
            }
            else if (_presentationSettleCallback != null &&
                !needsApplyRetry &&
                (_dirty & PresentationWorkMask) == EdgeCapsuleDirty.None)
            {
                SchedulePresentationSettleCompletion();
            }
        }
    }

    private void CompletePresentationSettle(bool success)
    {
        var callback = _presentationSettleCallback;
        _presentationSettleCallback = null;
        callback?.Invoke(success);
    }

    private void SchedulePresentationSettleCompletion()
    {
        var callback = _presentationSettleCallback;
        var dispatcher = _dispatcher;
        if (callback == null || dispatcher == null)
        {
            return;
        }

        // Let WPF finish the destination monitor's Render/Loaded work before the floating cover is
        // released. Intervening dirty work blocks this candidate and the next pass reschedules it.
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ReferenceEquals(callback, _presentationSettleCallback) ||
                    Transition.HasValue ||
                    (_dirty & PresentationWorkMask) != EdgeCapsuleDirty.None)
                {
                    return;
                }
                // This only reports that Presenter work drained. Cross-HWND callers still verify
                // the native host with ConfirmPresentationSettled before releasing their cover.
                CompletePresentationSettle(success: true);
            }),
            DispatcherPriority.ContextIdle);
    }

    private void StartFrameScheduler()
    {
        if (_dispatcher == null || _reconcile == null)
        {
            return;
        }
        if (_frameSchedulerActive)
        {
            return;
        }

        _frameScheduler ??= EdgeCapsuleFrameScheduler.For(_dispatcher);
        _frameSchedulerActive = true;
        _frameScheduler.Activate(this);
    }

    private void StopFrameScheduler()
    {
        if (!_frameSchedulerActive)
        {
            return;
        }

        _frameSchedulerActive = false;
        _frameScheduler?.Deactivate(this);
    }

    internal bool UsesSharedFrameScheduler(EdgeCapsuleFrameScheduler scheduler) =>
        _frameSchedulerActive && ReferenceEquals(_frameScheduler, scheduler);

    internal bool AdvanceSharedFrame(
        EdgeCapsuleFrameScheduler scheduler,
        DeviceScreenPoint? pointer)
    {
        var pointerTracking = NeedsPointerTracking;
        var applyRetryPending = (_dirty & EdgeCapsuleDirty.ApplyRetry) != 0;
        if (!UsesSharedFrameScheduler(scheduler) ||
            _dispatcher == null ||
            _reconcile == null ||
            (!Transition.HasValue && !applyRetryPending && !pointerTracking))
        {
            StopFrameScheduler();
            return false;
        }

        // Merge queued work into this render tick and invalidate its stale dispatcher callback.
        // Flush, deferred invalidation and animation still execute the same RunReconcile path.
        if (Transition.HasValue)
        {
            _dirty |= EdgeCapsuleDirty.Frame;
        }
        else if (pointerTracking)
        {
            _dirty |= EdgeCapsuleDirty.Pointer;
        }
        _reconcileGeneration++;
        _reconcileScheduled = false;
        _hasFramePointerOverride = true;
        _framePointerOverride = pointer;
        try
        {
            RunReconcile();
        }
        finally
        {
            _framePointerOverride = null;
            _hasFramePointerOverride = false;
        }
        return UsesSharedFrameScheduler(scheduler) &&
            (Transition.HasValue ||
                (_dirty & EdgeCapsuleDirty.ApplyRetry) != 0 ||
                NeedsPointerTracking);
    }
}
