using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal sealed record EdgeCapsuleDragWindowOptions
{
#if DEBUG
    public required string DiagnosticId { get; init; }
#endif
    public required EdgeCapsuleFloatingShape Shape { get; init; }
    public required double WindowChromeMargin { get; init; }
    public required double OutlineMargin { get; init; }
    public required double OutlineThickness { get; init; }
    public required double OutlineOverlap { get; init; }
    public required double LeftPadding { get; init; }
    public required double IconGap { get; init; }
    public required double RightPadding { get; init; }
    public required string Icon { get; init; }
    public required string Label { get; init; }
    public required double IconFontSize { get; init; }
    public required double LabelFontSize { get; init; }
    public required FontWeight LabelFontWeight { get; init; }
    public required FontFamily UiFontFamily { get; init; }
    public required FontFamily SymbolFontFamily { get; init; }
    public required XmlLanguage Language { get; init; }
    public required Brush PaperBrush { get; init; }
    public required Brush PaperBorderBrush { get; init; }
    public required Brush IconBrush { get; init; }
    public required Brush LabelBrush { get; init; }
    public required Brush OutlineBrush { get; init; }
    public required bool Topmost { get; init; }
}

internal enum EdgeCapsuleNativeDragResult
{
    Completed,
    NotStarted,
    Aborted
}

internal readonly record struct EdgeCapsuleNativeDragOutcome(
    EdgeCapsuleNativeDragResult Result,
    DeviceScreenPoint DropPosition);

// A detached capsule is a complete, real-size pill in its own HWND. It never reuses the docked
// one-sided tag or any of its edge-specific columns, margins, corners, or width animation state.
internal sealed class EdgeCapsuleDragWindow : Window
{
    private const double PooledParkCoordinate = -32000;

    // A detached drag surface is process-global: the controller serializes capsule reorders, so
    // one cached HWND is sufficient. Its HWND and WPF visual tree stay alive for the application
    // lifetime; leases only bind paper-specific text, brushes and geometry onto the existing tree.
    // The host is destroyed only when it becomes unusable or the dispatcher shuts down.
    private static EdgeCapsuleDragWindow? s_pooledHost;
    private static bool s_pooledHostLeased;

    private enum DockingHandoffAnimationPhase
    {
        Flight,
        Reveal
    }

    private sealed record DockingHandoffAnimation(
        DockingHandoffAnimationPhase Phase,
        EdgeCapsuleFloatingHandoffGeometry Geometry,
        EdgeCapsuleEdge Edge,
        double SurfaceStartWidthDip,
        double SurfaceTargetWidthDip,
        double StartOpacity,
        long StartedAtTimestamp,
        long DurationTimestampTicks,
        Action<bool> Completed);

    private readonly ScaleTransform _entranceScale = new(1, 1);
#if DEBUG
    private static long _nextDiagnosticHostId;
    private readonly long _diagnosticHostId;
    private string _diagnosticId;
    private bool _diagnosticNativeDragTracking;
    private long _diagnosticNativeDragStartedAt;
    private long _diagnosticLastLocationAt;
    private int _diagnosticLocationEvents;
    private bool _diagnosticHandoffTracking;
    private string _diagnosticHandoffPhase = "<none>";
    private long _diagnosticHandoffStartedAt;
    private long _diagnosticLastHandoffFrameAt;
    private int _diagnosticHandoffFrames;
    private int _diagnosticHandoffNativeMoves;
#endif
    private double _widthDip;
    private double _heightDip;
    private Grid _root = null!;
    private Grid _surface = null!;
    private Border _paperBackground = null!;
    private Grid _shell = null!;
    private Grid _content = null!;
    private TextBlock _icon = null!;
    private TextBlock _label = null!;
    private Border _contentArea = null!;
    private Border _outline = null!;
    private EdgeCapsuleDragWindowOptions? _configuredOptions;
    private DockingHandoffAnimation? _dockingHandoffAnimation;
    private double _currentSurfaceWidthDip;
    private EdgeCapsuleEdge _currentDockingEdge;
    private bool _dockingPresentationActive;
    private bool _closingByOwner;
    private bool _hasBeenShown;
    private bool _nativeDragAttemptActive;
    private bool _isClosed;
    private int _lastDockingDeviceLeft = int.MinValue;
    private int _lastDockingDeviceTop = int.MinValue;
    private double _lastDockingSurfaceWidthDip = double.NaN;
    private EdgeCapsuleEdge? _lastDockingEdge;

    public EdgeCapsuleDragWindow(EdgeCapsuleDragWindowOptions options)
    {
#if DEBUG
        _diagnosticHostId = Interlocked.Increment(ref _nextDiagnosticHostId);
        _diagnosticId = options.DiagnosticId;
#endif
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);
        Opacity = 0;
        BuildContent();
        ConfigureForReuse(options);

        SourceInitialized += (_, _) => WindowNative.ApplyNoActivateStyle(this);
        Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
    }

    public event EventHandler? UnexpectedlyClosed;

    public static bool NeedsInfrastructurePrewarm
    {
        get
        {
            VerifyPoolThreadAccess();
            return s_pooledHost == null || !s_pooledHost._hasBeenShown;
        }
    }

    public static bool TryPrewarmInfrastructure(
        EdgeCapsuleDragWindowOptions seedOptions)
    {
        VerifyPoolThreadAccess();
        if (s_pooledHostLeased)
        {
            return false;
        }

#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var host = s_pooledHost;
        try
        {
            host ??= CreatePooledHost(seedOptions);
            if (host._hasBeenShown && !host.IsVisible)
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"drag.host phase=infrastructure-hit " +
                    $"dragHost={host._diagnosticHostId}");
#endif
                return true;
            }

            var warmedShow = host.PrewarmInfrastructureAndPark();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.host phase=infrastructure-prewarmed " +
                $"dragHost={host._diagnosticHostId} warmedShow={warmedShow} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
#endif
            return true;
        }
        catch
        {
            DiscardPooledHost(host);
            return false;
        }
    }

    public static EdgeCapsuleDragWindow Rent(EdgeCapsuleDragWindowOptions options)
    {
        VerifyPoolThreadAccess();
        if (s_pooledHostLeased)
        {
            throw new InvalidOperationException(
                "Only one detached edge-capsule drag window can be leased at a time.");
        }

        var host = s_pooledHost;
        if (host == null)
        {
            host = CreatePooledHost(options);
            s_pooledHostLeased = true;
            return host;
        }

        s_pooledHostLeased = true;
        try
        {
#if DEBUG
            var bindStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var rebound = host.ConfigureForReuse(options);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.host phase=bound paper={host._diagnosticId} " +
                $"dragHost={host._diagnosticHostId} rebound={rebound} " +
                "treeRebuilt=false " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(bindStartedAt):F3}");
#endif
            return host;
        }
        catch
        {
            // A hidden WPF Window can normally accept all label/theme/shape updates in place. If
            // a stale visual or HWND rejects that rebind, discard it and retry once with a clean
            // host rather than carrying the bad lease into the drag transaction.
            DiscardPooledHost(host);
            var replacement = CreatePooledHost(options);
            s_pooledHostLeased = true;
            return replacement;
        }
    }

    public void ReturnToPool()
    {
        VerifyAccess();
        if (_isClosed)
        {
            return;
        }

        if (!ReferenceEquals(this, s_pooledHost))
        {
            CloseFromOwner();
            return;
        }
        if (!s_pooledHostLeased)
        {
            return;
        }

#if DEBUG
        var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        try
        {
            ParkHidden();
            ResetTransientPresentation();
            s_pooledHostLeased = false;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.host phase=pooled paper={_diagnosticId} " +
                $"dragHost={_diagnosticHostId} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
#endif
        }
        catch
        {
            DiscardPooledHost(this);
        }
    }

    private static EdgeCapsuleDragWindow CreatePooledHost(
        EdgeCapsuleDragWindowOptions options)
    {
        var host = new EdgeCapsuleDragWindow(options);
        s_pooledHost = host;
        return host;
    }

    private static void DiscardPooledHost(EdgeCapsuleDragWindow? host)
    {
        if (host == null)
        {
            return;
        }
        if (ReferenceEquals(host, s_pooledHost))
        {
            s_pooledHost = null;
            s_pooledHostLeased = false;
        }

        try
        {
            host.CloseFromOwner();
        }
        catch
        {
            // The dispatcher may already be tearing down. Its native window will be destroyed by
            // shutdown; most importantly, it is no longer eligible for another pool lease.
        }
    }

    private static void VerifyPoolThreadAccess()
    {
        var applicationDispatcher = Application.Current?.Dispatcher;
        if (applicationDispatcher != null && !applicationDispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The detached edge-capsule drag pool is UI-thread only.");
        }
        if (s_pooledHost != null && !s_pooledHost.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The detached edge-capsule drag pool belongs to another dispatcher.");
        }
    }

    public void ShowWithEntrance(
        DeviceScreenPoint pointer,
        bool animate,
        double scaleFrom,
        int durationMilliseconds)
    {
#if DEBUG
        var totalStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var stageStartedAt = totalStartedAt;
#endif
        // Create only this detached HWND as System Aware, then let the Windows caption move loop
        // own cross-monitor drag position and bitmap scaling just as it did before PMv2.
        PlaceCenteredAtForShow(pointer);
#if DEBUG
        var placeMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
        stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var reusedHandle = EnsureSystemAwareHandle();
#if DEBUG
        var createHandleMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
#endif
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate)
        {
            _entranceScale.ScaleX = 1;
            _entranceScale.ScaleY = 1;
#if DEBUG
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            if (!IsVisible)
            {
                Show();
            }
            _hasBeenShown = true;
#if DEBUG
            var showMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            RefreshNativeMetricsLayout();
#if DEBUG
            var metricsMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            PlaceCenteredAtCursorForDrag(pointer);
#if DEBUG
            var centerMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
#endif
            Opacity = 1;
#if DEBUG
            TraceShowDiagnostics(
                animate,
                reusedHandle,
                placeMs,
                createHandleMs,
                showMs,
                metricsMs,
                centerMs,
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(totalStartedAt));
#endif
            return;
        }

        _entranceScale.ScaleX = scaleFrom;
        _entranceScale.ScaleY = scaleFrom;
#if DEBUG
        stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        if (!IsVisible)
        {
            Show();
        }
        _hasBeenShown = true;
#if DEBUG
        var animatedShowMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
        stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        RefreshNativeMetricsLayout();
#if DEBUG
        var animatedMetricsMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
        stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        PlaceCenteredAtCursorForDrag(pointer);
#if DEBUG
        var animatedCenterMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
#endif
        Opacity = 1;
        var animation = new DoubleAnimation
        {
            From = scaleFrom,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
#if DEBUG
        TraceShowDiagnostics(
            animate,
            reusedHandle,
            placeMs,
            createHandleMs,
            animatedShowMs,
            animatedMetricsMs,
            animatedCenterMs,
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(totalStartedAt));
#endif
    }

#if DEBUG
    private void TraceShowDiagnostics(
        bool animate,
        bool reusedHandle,
        double placeMs,
        double createHandleMs,
        double showMs,
        double metricsMs,
        double centerMs,
        double totalMs)
    {
        var boundsText = WindowNative.TryGetWindowDeviceBounds(this, out var bounds)
            ? $"{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}"
            : "<unavailable>";
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.host phase=shown paper={_diagnosticId} dragHost={_diagnosticHostId} " +
            $"animate={animate} reusedHandle={reusedHandle} " +
            $"totalMs={totalMs:F3} placeMs={placeMs:F3} " +
            $"createHandleMs={createHandleMs:F3} showMs={showMs:F3} " +
            $"metricsMs={metricsMs:F3} centerMs={centerMs:F3} bounds={boundsText}");
    }
#endif

    private void PlaceCenteredAtForShow(DeviceScreenPoint pointer)
    {
        var pointerDip = WindowWorkAreaHelper.DeviceScreenPointToDip(pointer);
        Left = pointerDip.X - _widthDip / 2.0;
        Top = pointerDip.Y - _heightDip / 2.0;
    }

    // A pull-out can begin with the cursor already on another monitor. The WPF property write
    // above converts through the uniform system scale, which the monitor-anchored virtual desktop
    // mapping does not honor on mixed-DPI zones, so re-place the shown window once from inside
    // its own System Aware space; Windows then resolves the exact physical rectangle for the
    // cursor's monitor before the native move loop takes ownership.
    private void PlaceCenteredAtCursorForDrag(DeviceScreenPoint fallbackPointer)
    {
        if (!WindowNative.TryCenterSystemAwareWindowAtCursor(this, _widthDip, _heightDip))
        {
            PlaceCenteredAtForShow(fallbackPointer);
        }
    }

    public EdgeCapsuleNativeDragOutcome RunNativeDragFromCursor()
    {
        if (_isClosed ||
            _dockingPresentationActive ||
            _nativeDragAttemptActive)
        {
            return new EdgeCapsuleNativeDragOutcome(
                EdgeCapsuleNativeDragResult.NotStarted,
                default);
        }

        _nativeDragAttemptActive = true;
#if DEBUG
        var diagnosticResult = EdgeCapsuleNativeDragResult.NotStarted;
        BeginNativeDragDiagnostics();
#endif
        try
        {
            // Caption drag is intentional and modal until the left button is released. Escape is not
            // a product cancel for floating capsule reorder: do not register hotkeys or treat the
            // system move-loop Escape restore as Abort. When the loop ends for any reason, land at
            // the live cursor so sorting / cross-queue still commits.
            //
            // WindowNative captures and consumes the native anchor entirely inside the floating
            // HWND's System-Aware coordinate space. The completed drop is sampled separately
            // below in the application's PMv2 device space.
            //
            // SendMessage blocks in DefWindowProc's move loop. Do not require ENTERSIZEMOVE /
            // EXITSIZEMOVE — those are not reliable on layered NOACTIVATE HWNDs.
            if (!WindowNative.TryBeginSystemAwareWindowCaptionDragFromCursor(
                    this,
                    _widthDip,
                    _heightDip))
            {
                return new EdgeCapsuleNativeDragOutcome(
                    EdgeCapsuleNativeDragResult.NotStarted,
                    default);
            }

            if (!WindowNative.TryGetCursorScreenPosition(out var finalCursor))
            {
#if DEBUG
                diagnosticResult = EdgeCapsuleNativeDragResult.Aborted;
#endif
                return new EdgeCapsuleNativeDragOutcome(
                    EdgeCapsuleNativeDragResult.Aborted,
                    default);
            }

#if DEBUG
            diagnosticResult = EdgeCapsuleNativeDragResult.Completed;
#endif
            return new EdgeCapsuleNativeDragOutcome(
                EdgeCapsuleNativeDragResult.Completed,
                finalCursor);
        }
        finally
        {
#if DEBUG
            CompleteNativeDragDiagnostics(diagnosticResult);
#endif
            _nativeDragAttemptActive = false;
        }
    }

#if DEBUG
    private void BeginNativeDragDiagnostics()
    {
        _diagnosticNativeDragTracking = true;
        _diagnosticNativeDragStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        _diagnosticLastLocationAt = 0;
        _diagnosticLocationEvents = 0;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.native phase=loop-begin paper={_diagnosticId} " +
            $"dragHost={_diagnosticHostId}");
    }

    private void CompleteNativeDragDiagnostics(EdgeCapsuleNativeDragResult result)
    {
        var durationMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            _diagnosticNativeDragStartedAt);
        _diagnosticNativeDragTracking = false;
        var boundsText = WindowNative.TryGetWindowDeviceBounds(this, out var bounds)
            ? $"{bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}"
            : "<unavailable>";
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.native phase=loop-summary paper={_diagnosticId} " +
            $"dragHost={_diagnosticHostId} result={result} modalMs={durationMs:F3} " +
            "modalIncludesPointerHold=true " +
            $"locationEvents={_diagnosticLocationEvents} bounds={boundsText}");
    }
#endif

    protected override void OnLocationChanged(EventArgs e)
    {
#if DEBUG
        var eventAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var tracking = _diagnosticNativeDragTracking;
        var sequence = 0;
        var totalMs = 0.0;
        var gapMs = 0.0;
        if (tracking)
        {
            sequence = ++_diagnosticLocationEvents;
            totalMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _diagnosticNativeDragStartedAt,
                eventAt);
            gapMs = _diagnosticLastLocationAt == 0
                ? totalMs
                : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    _diagnosticLastLocationAt,
                    eventAt);
            _diagnosticLastLocationAt = eventAt;
        }
        var handlerStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        base.OnLocationChanged(e);
#if DEBUG
        if (tracking)
        {
            var handlerMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                handlerStartedAt);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.motion paper={_diagnosticId} dragHost={_diagnosticHostId} " +
                $"sequence={sequence} totalMs={totalMs:F3} gapMs={gapMs:F3} " +
                $"handlerMs={handlerMs:F3} leftDip={Left:F2} topDip={Top:F2}");
        }
#endif
    }

    public void AnimateDockingHandoff(
        DeviceScreenRect dockingAnchorBounds,
        EdgeCapsuleEdge targetEdge,
        int durationMilliseconds,
        Action<bool> completed)
    {
        if (_dockingPresentationActive && targetEdge != _currentDockingEdge)
        {
            CancelDockingHandoffAnimation();
            completed(false);
            return;
        }
        CancelDockingHandoffAnimation();
        if (_isClosed || dockingAnchorBounds.IsEmpty)
        {
            completed(false);
            return;
        }

        // A very quick release can overlap the entrance scale. The docking surface starts from the
        // real full-size pill, so remove that transform before sampling its physical rectangle.
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _entranceScale.ScaleX = 1;
        _entranceScale.ScaleY = 1;
        _surface.Opacity = 1;
        _outline.Opacity = 1;
        if (!WindowNative.TryGetWindowDeviceBounds(this, out var startHostBounds) ||
            startHostBounds.IsEmpty)
        {
            completed(false);
            return;
        }

        var targetCenter = new DeviceScreenPoint(
            dockingAnchorBounds.Left + dockingAnchorBounds.Width / 2.0,
            dockingAnchorBounds.Top + dockingAnchorBounds.Height / 2.0);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                targetCenter,
                out var targetMonitor))
        {
            completed(false);
            return;
        }

        var geometry = EdgeCapsuleGeometry.FloatingHandoffGeometry(
            startHostBounds,
            dockingAnchorBounds,
            targetEdge,
            _widthDip,
            _heightDip,
            targetMonitor.DpiScaleX,
            targetMonitor.DpiScaleY);
        if (!geometry.IsUsable)
        {
            completed(false);
            return;
        }

        var startSurfaceWidthDip = Math.Clamp(_currentSurfaceWidthDip, 1, _widthDip);
        var targetSurfaceWidthDip = geometry.SurfaceTargetWidthDip;
        _dockingPresentationActive = true;
        _currentDockingEdge = targetEdge;
#if DEBUG
        BeginHandoffDiagnostics("flight");
#endif

        // The WPF Window keeps its fixed logical Width/Height. Each frame moves only the HWND and
        // changes only the child width, so there is no second native size owner to fight WPF.
        if (!ApplyDockingFrame(
                new DeviceScreenPoint(startHostBounds.Left, startHostBounds.Top),
                startSurfaceWidthDip,
                targetEdge))
        {
#if DEBUG
            CompleteHandoffDiagnostics("initial-apply-failed");
#endif
            completed(false);
            return;
        }

        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Flight,
            geometry,
            targetEdge,
            startSurfaceWidthDip,
            targetSurfaceWidthDip,
            1,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void AnimateDockingReveal(int durationMilliseconds, Action<bool> completed)
    {
        CancelDockingHandoffAnimation();
        if (_isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            completed(false);
            return;
        }

        // The confirmed docked host owns the final outline. Keep the floating body as the
        // anti-flash cover, but do not cross-fade two outlines with different edge geometry.
        _outline.Opacity = 0;
        var startOpacity = Math.Clamp(_surface.Opacity, 0, 1);
        if (startOpacity <= 0.001)
        {
            _surface.Opacity = 0;
            completed(true);
            return;
        }
#if DEBUG
        BeginHandoffDiagnostics("reveal");
#endif

        _dockingHandoffAnimation = new DockingHandoffAnimation(
            DockingHandoffAnimationPhase.Reveal,
            default,
            default,
            0,
            0,
            startOpacity,
            Stopwatch.GetTimestamp(),
            AnimationDurationTicks(durationMilliseconds),
            completed);
        CompositionTarget.Rendering += OnDockingHandoffFrame;
        AdvanceDockingHandoffFrame();
    }

    public void RestoreDockingCover()
    {
        if (_isClosed)
        {
            return;
        }

        _outline.Opacity = 1;
        _surface.Opacity = 1;
        WindowNative.BringToFrontNoActivate(this);
    }

    private void OnDockingHandoffFrame(object? sender, EventArgs e) =>
        AdvanceDockingHandoffFrame();

    private void AdvanceDockingHandoffFrame()
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null || _isClosed)
        {
            return;
        }
#if DEBUG
        RecordHandoffFrameDiagnostics();
#endif

        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - animation.StartedAtTimestamp);
        var rawProgress = Math.Clamp(
            elapsed / (double)animation.DurationTimestampTicks,
            0,
            1);
        var progress = 1.0 - Math.Pow(1.0 - rawProgress, 3.0);
        if (animation.Phase == DockingHandoffAnimationPhase.Flight)
        {
            var hostPosition = EdgeCapsuleGeometry.InterpolateDevicePosition(
                animation.Geometry.HostStartBounds,
                animation.Geometry.HostTargetBounds,
                progress);
            var surfaceWidthDip = animation.SurfaceStartWidthDip +
                (animation.SurfaceTargetWidthDip - animation.SurfaceStartWidthDip) * progress;
            if (!ApplyDockingFrame(
                    hostPosition,
                    surfaceWidthDip,
                    animation.Edge))
            {
                CompleteDockingHandoffAnimation(reachedTarget: false);
                return;
            }
        }
        else
        {
            _surface.Opacity = Math.Clamp(
                animation.StartOpacity * (1.0 - progress),
                0,
                1);
        }

        if (rawProgress >= 1)
        {
            CompleteDockingHandoffAnimation(reachedTarget: true);
        }
    }

    private void CompleteDockingHandoffAnimation(bool reachedTarget)
    {
        var animation = _dockingHandoffAnimation;
        if (animation == null)
        {
            return;
        }

        CompositionTarget.Rendering -= OnDockingHandoffFrame;
        if (!reachedTarget ||
            _isClosed ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("interrupted");
#endif
            animation.Completed(false);
            return;
        }

        if (animation.Phase == DockingHandoffAnimationPhase.Reveal)
        {
            _surface.Opacity = 0;
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("completed");
#endif
            animation.Completed(true);
            return;
        }

        if (!ApplyDockingFrame(
                new DeviceScreenPoint(
                    animation.Geometry.HostTargetBounds.Left,
                    animation.Geometry.HostTargetBounds.Top),
                animation.SurfaceTargetWidthDip,
                animation.Edge))
        {
            _dockingHandoffAnimation = null;
#if DEBUG
            CompleteHandoffDiagnostics("endpoint-apply-failed");
#endif
            animation.Completed(false);
            return;
        }

        // Let WPF render the child-width endpoint, then verify the single native position/size
        // result. No replay is needed: a failed endpoint takes the existing terminal snap path.
        Dispatcher.BeginInvoke(
            (Action)(() => CompleteDockingHandoffEndpointSettle(animation)),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void CompleteDockingHandoffEndpointSettle(DockingHandoffAnimation animation)
    {
        if (!ReferenceEquals(animation, _dockingHandoffAnimation) || _isClosed)
        {
            return;
        }

        var settled = ApplyDockingFrame(
                new DeviceScreenPoint(
                    animation.Geometry.HostTargetBounds.Left,
                    animation.Geometry.HostTargetBounds.Top),
                animation.SurfaceTargetWidthDip,
                animation.Edge,
                forceNativeMove: true) &&
            WindowNative.TryGetWindowDeviceBounds(this, out var actualHostBounds) &&
            EdgeCapsuleGeometry.DeviceBoundsMatch(
                actualHostBounds,
                animation.Geometry.HostTargetBounds,
                tolerance: 2) &&
            MatchesDockingSurfaceLayout(animation.SurfaceTargetWidthDip);
        _dockingHandoffAnimation = null;
#if DEBUG
        CompleteHandoffDiagnostics(settled ? "completed" : "endpoint-verify-failed");
#endif
        animation.Completed(settled);
    }

    private bool MatchesDockingSurfaceLayout(double targetWidthDip)
    {
        if (!double.IsFinite(targetWidthDip) ||
            targetWidthDip <= 0 ||
            !double.IsFinite(_surface.ActualWidth) ||
            !double.IsFinite(_surface.ActualHeight) ||
            _surface.ActualWidth <= 0 ||
            _surface.ActualHeight <= 0)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(_surface);
        var actualWidth = (int)Math.Round(
            _surface.ActualWidth * dpi.DpiScaleX,
            MidpointRounding.AwayFromZero);
        var actualHeight = (int)Math.Round(
            _surface.ActualHeight * dpi.DpiScaleY,
            MidpointRounding.AwayFromZero);
        var targetWidth = (int)Math.Round(
            targetWidthDip * dpi.DpiScaleX,
            MidpointRounding.AwayFromZero);
        var targetHeight = (int)Math.Round(
            _heightDip * dpi.DpiScaleY,
            MidpointRounding.AwayFromZero);
        return Math.Abs(actualWidth - targetWidth) <= 1 &&
            Math.Abs(actualHeight - targetHeight) <= 1;
    }

    private void CancelDockingHandoffAnimation()
    {
        if (_dockingHandoffAnimation == null)
        {
            return;
        }

        var animation = _dockingHandoffAnimation;
        _dockingHandoffAnimation = null;
        CompositionTarget.Rendering -= OnDockingHandoffFrame;
#if DEBUG
        CompleteHandoffDiagnostics("cancelled");
#endif
        animation.Completed(false);
    }

    private bool ApplyDockingFrame(
        DeviceScreenPoint hostPosition,
        double surfaceWidthDip,
        EdgeCapsuleEdge edge,
        bool forceNativeMove = false)
    {
        if (_isClosed ||
            !double.IsFinite(hostPosition.X) ||
            !double.IsFinite(hostPosition.Y) ||
            !double.IsFinite(surfaceWidthDip) ||
            surfaceWidthDip <= 0)
        {
            return false;
        }

        var deviceLeft = (int)Math.Round(
            hostPosition.X,
            MidpointRounding.AwayFromZero);
        var deviceTop = (int)Math.Round(
            hostPosition.Y,
            MidpointRounding.AwayFromZero);
        var clampedSurfaceWidth = Math.Clamp(
            surfaceWidthDip,
            1,
            _widthDip);
        if (_lastDockingEdge != edge ||
            !double.IsFinite(_lastDockingSurfaceWidthDip) ||
            Math.Abs(_lastDockingSurfaceWidthDip - clampedSurfaceWidth) > 0.01)
        {
            _surface.HorizontalAlignment = edge == EdgeCapsuleEdge.Left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            _surface.VerticalAlignment = VerticalAlignment.Center;
            _surface.Width = clampedSurfaceWidth;
            _surface.Height = _heightDip;
            _lastDockingSurfaceWidthDip = clampedSurfaceWidth;
            _lastDockingEdge = edge;
        }
        _currentSurfaceWidthDip = clampedSurfaceWidth;
        _currentDockingEdge = edge;

        // Rendering can repeat at the same display timestamp, and eased samples often round to
        // the same physical pixel. Each redundant SetWindowPos serializes with DWM for up to 10ms
        // in the captured traces, so collapse identical native positions before that boundary.
        if (!forceNativeMove &&
            _lastDockingDeviceLeft == deviceLeft &&
            _lastDockingDeviceTop == deviceTop)
        {
            return true;
        }

        // Position is committed last. Width/Height remain owned by the fixed WPF Window and are
        // never included in this native operation.
#if DEBUG
        var nativeStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var appliedPosition = new DeviceScreenPoint(
            deviceLeft,
            deviceTop);
        var moved = WindowNative.TryMoveWindowDevicePosition(
            this,
            appliedPosition);
        if (moved)
        {
            _lastDockingDeviceLeft = deviceLeft;
            _lastDockingDeviceTop = deviceTop;
        }
#if DEBUG
        RecordHandoffNativeMoveDiagnostics(
            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(nativeStartedAt),
            appliedPosition,
            moved);
#endif
        return moved;
    }

#if DEBUG
    private void BeginHandoffDiagnostics(string phase)
    {
        if (_diagnosticHandoffTracking)
        {
            CompleteHandoffDiagnostics("restarted");
        }
        _diagnosticHandoffTracking = true;
        _diagnosticHandoffPhase = phase;
        _diagnosticHandoffStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        _diagnosticLastHandoffFrameAt = 0;
        _diagnosticHandoffFrames = 0;
        _diagnosticHandoffNativeMoves = 0;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={phase} event=begin paper={_diagnosticId} " +
            $"dragHost={_diagnosticHostId}");
    }

    private void RecordHandoffFrameDiagnostics()
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        var frameAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var totalMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            _diagnosticHandoffStartedAt,
            frameAt);
        var gapMs = _diagnosticLastHandoffFrameAt == 0
            ? totalMs
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _diagnosticLastHandoffFrameAt,
                frameAt);
        _diagnosticLastHandoffFrameAt = frameAt;
        _diagnosticHandoffFrames++;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=frame " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} " +
            $"sequence={_diagnosticHandoffFrames} totalMs={totalMs:F3} " +
            $"gapMs={gapMs:F3}");
    }

    private void RecordHandoffNativeMoveDiagnostics(
        double nativeMs,
        DeviceScreenPoint hostPosition,
        bool moved)
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        _diagnosticHandoffNativeMoves++;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=native-move " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} " +
            $"sequence={_diagnosticHandoffNativeMoves} outcome={(moved ? "success" : "failed")} " +
            $"nativeMs={nativeMs:F3} target={hostPosition.X:F0},{hostPosition.Y:F0}");
    }

    private void CompleteHandoffDiagnostics(string outcome)
    {
        if (!_diagnosticHandoffTracking)
        {
            return;
        }
        var durationMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
            _diagnosticHandoffStartedAt);
        _diagnosticHandoffTracking = false;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.handoff phase={_diagnosticHandoffPhase} event=summary " +
            $"paper={_diagnosticId} dragHost={_diagnosticHostId} outcome={outcome} " +
            $"totalMs={durationMs:F3} frames={_diagnosticHandoffFrames} " +
            $"nativeMoves={_diagnosticHandoffNativeMoves}");
    }
#endif

    private static long AnimationDurationTicks(int durationMilliseconds) =>
        Math.Max(
            1,
            (long)Math.Round(
                Stopwatch.Frequency * Math.Max(1, durationMilliseconds) / 1000.0));

    private bool ConfigureForReuse(EdgeCapsuleDragWindowOptions options)
    {
        if (_isClosed)
        {
            throw new InvalidOperationException(
                "A closed detached edge-capsule drag window cannot be reused.");
        }
        ValidateOptions(options);

#if DEBUG
        _diagnosticId = options.DiagnosticId;
#endif
        var reconfigured = !Equals(_configuredOptions, options);
        if (reconfigured)
        {
            CancelDockingHandoffAnimation();
            _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BindOptions(options);
        }

        ResetTransientPresentation();
        return reconfigured;
    }

    private static void ValidateOptions(EdgeCapsuleDragWindowOptions options)
    {
        if (!options.Shape.Visible ||
            options.Shape.Kind != EdgeCapsuleSurfaceKind.FloatingFree ||
            !double.IsFinite(options.Shape.WindowWidthDip) ||
            !double.IsFinite(options.Shape.WindowHeightDip) ||
            !double.IsFinite(options.Shape.BodyHeightDip) ||
            !double.IsFinite(options.Shape.CornerRadiusDip) ||
            options.Shape.WindowWidthDip <= 0 ||
            options.Shape.WindowHeightDip <= 0 ||
            options.Shape.BodyHeightDip <= 0 ||
            options.Shape.CornerRadiusDip < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The detached drag host requires a finite FloatingFree shape.");
        }
    }

    private void BindOptions(EdgeCapsuleDragWindowOptions options)
    {
        // The expensive Window, HWND, DropShadowEffect and WPF tree are permanent. Switching papers
        // is intentionally a property bind; never clear Content or rebuild controls on the input path.
        _widthDip = options.Shape.WindowWidthDip;
        _heightDip = options.Shape.WindowHeightDip;
        Width = _widthDip;
        Height = _heightDip;
        FontFamily = options.UiFontFamily;
        Language = options.Language;
        Topmost = options.Topmost;

        _paperBackground.Margin = new Thickness(options.WindowChromeMargin);
        _paperBackground.Background = options.PaperBrush;
        _paperBackground.BorderBrush = options.PaperBorderBrush;
        _paperBackground.CornerRadius = new CornerRadius(
            options.Shape.CornerRadiusDip);

        _shell.Margin = new Thickness(options.WindowChromeMargin);
        _shell.Height = options.Shape.BodyHeightDip;
        _content.Margin = new Thickness(
            options.LeftPadding,
            0,
            options.RightPadding,
            0);

        _icon.Text = options.Icon;
        _icon.Foreground = options.IconBrush;
        _icon.FontFamily = options.SymbolFontFamily;
        _icon.FontSize = options.IconFontSize;

        _label.Text = options.Label;
        _label.Foreground = options.LabelBrush;
        _label.FontFamily = options.UiFontFamily;
        _label.FontSize = options.LabelFontSize;
        _label.FontWeight = options.LabelFontWeight;
        _label.Margin = new Thickness(options.IconGap, 0, 0, 0);

        _contentArea.CornerRadius = new CornerRadius(
            options.Shape.CornerRadiusDip);
        _outline.Margin = new Thickness(options.OutlineMargin);
        _outline.BorderBrush = options.OutlineBrush;
        _outline.BorderThickness = new Thickness(options.OutlineThickness);
        _outline.CornerRadius = new CornerRadius(
            options.Shape.CornerRadiusDip +
            options.OutlineThickness -
            options.OutlineOverlap);
        _outline.Visibility = options.Shape.OutlineVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _configuredOptions = options;
    }

    private void ResetTransientPresentation()
    {
        CancelDockingHandoffAnimation();
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _entranceScale.ScaleX = 1;
        _entranceScale.ScaleY = 1;
        _surface.BeginAnimation(OpacityProperty, null);
        _outline.BeginAnimation(OpacityProperty, null);
        _surface.Opacity = 1;
        _outline.Opacity = 1;
        _surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        _surface.VerticalAlignment = VerticalAlignment.Stretch;
        _surface.Width = double.NaN;
        _surface.Height = double.NaN;
        _currentSurfaceWidthDip = _widthDip;
        _currentDockingEdge = default;
        _lastDockingDeviceLeft = int.MinValue;
        _lastDockingDeviceTop = int.MinValue;
        _lastDockingSurfaceWidthDip = double.NaN;
        _lastDockingEdge = null;
        _dockingPresentationActive = false;
        _nativeDragAttemptActive = false;
    }

    private bool EnsureSystemAwareHandle()
    {
        if (_isClosed)
        {
            throw new InvalidOperationException(
                "A closed detached edge-capsule drag window has no reusable HWND.");
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                throw new InvalidOperationException(
                    "The cached detached edge-capsule HWND is no longer alive.");
            }
            return true;
        }

        WindowNative.CreateSystemAwareTopLevelWindowHandle(this);
        return false;
    }

    private bool PrewarmInfrastructureAndPark()
    {
        Opacity = 0;
        Left = PooledParkCoordinate;
        Top = PooledParkCoordinate;
        EnsureSystemAwareHandle();

        var warmShow = !_hasBeenShown;
        if (warmShow)
        {
            if (!IsVisible)
            {
                Show();
            }
            _hasBeenShown = true;
            RefreshNativeMetricsLayout();
        }

        ParkHidden();
        return warmShow;
    }

    private void ParkHidden()
    {
        if (IsVisible)
        {
            Hide();
        }
        Opacity = 0;
        Left = PooledParkCoordinate;
        Top = PooledParkCoordinate;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            _ = WindowNative.TryMoveWindowDevicePosition(
                this,
                new DeviceScreenPoint(PooledParkCoordinate, PooledParkCoordinate));
        }
    }

    private void RefreshNativeMetricsLayout()
    {
        _surface.InvalidateMeasure();
        _surface.InvalidateArrange();
        _root.InvalidateMeasure();
        _root.InvalidateArrange();
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    public void CloseFromOwner()
    {
        if (_closingByOwner || _isClosed)
        {
            return;
        }

        _closingByOwner = true;
        CancelDockingHandoffAnimation();
        Content = null;
        Close();
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e)
    {
        if (ReferenceEquals(this, s_pooledHost))
        {
            s_pooledHost = null;
            s_pooledHostLeased = false;
        }
        CloseFromOwner();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        if (ReferenceEquals(this, s_pooledHost))
        {
            s_pooledHost = null;
            s_pooledHostLeased = false;
        }
        base.OnClosed(e);
        if (!_closingByOwner)
        {
            UnexpectedlyClosed?.Invoke(this, EventArgs.Empty);
        }
        // UnexpectedlyClosed lets the owner clear its host reference first. Cancellation can then
        // complete the in-flight operation exactly once without re-entering owner recovery through
        // a window that has already closed.
        CancelDockingHandoffAnimation();
    }

    private void BuildContent()
    {
        _root = new Grid
        {
            Background = null,
            IsHitTestVisible = false
        };
        _surface = new Grid
        {
            Background = null,
            IsHitTestVisible = false,
            RenderTransform = _entranceScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _root.Children.Add(_surface);

        _paperBackground = new Border
        {
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 1,
                Opacity = 0.12
            }
        };
        _surface.Children.Add(_paperBackground);

        _shell = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        _content = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        _content.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

        _icon = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_icon, 0);
        _content.Children.Add(_icon);

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AppTypography.ApplyTextRendering(_label);
        Grid.SetColumn(_label, 1);
        _content.Children.Add(_label);

        _contentArea = new Border
        {
            Background = Brushes.Transparent,
            Child = _content
        };
        _shell.Children.Add(_contentArea);
        Panel.SetZIndex(_shell, 10);
        _surface.Children.Add(_shell);

        _outline = new Border
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_outline, 20);
        _surface.Children.Add(_outline);
        Content = _root;
    }
}
