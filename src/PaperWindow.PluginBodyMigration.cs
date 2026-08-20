using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _pluginBodyEverPresented;
    private ImageSource? _pluginBodyMiniSnapshot;
    private int _pluginBodyMiniSnapshotGeneration;
    private MigratedPluginBodyPreview? _migratedPluginBodyPreview;
    private bool _migratedPluginBodyPreviewVisible;
    private bool _migratedPluginBodySessionPresented;
    private bool _migratedPluginBodyPreviousRuntimeVisible;
    private int _migratedPluginBodyWarmupGeneration;
    private int _migratedPluginBodyPreviewSessionGeneration = -1;
    private bool _migratedPluginBodyPreviewPrewarmed;
    private DispatcherTimer? _migratedPluginBodyWarmupRetryTimer;
    private int _migratedPluginBodyWarmupRetryGeneration;
    private int _migratedPluginBodyWarmupRetryCount;

    private partial bool TryDescribeMigratedPluginBodyPreview(
        IPaperBodyViewMigrationProvider provider,
        EdgeCapsulePreviewContext context,
        out EdgeCapsulePreviewDescriptor descriptor)
    {
        descriptor = null!;
        if (_paperBodyHost.Current is not { } session ||
            session.View is Window ||
            !PluginVisualTreePolicy.IsSupportedPureWpfTree(session.View))
        {
            return false;
        }

        var size = ReadPreferredMiniSize(
            () => provider.PreferredMigratedMiniViewSize,
            new PaperMiniViewSize(360, 260));
        var normalizedSize = NormalizePluginMiniSizeForCurrentMonitor(size);
        var prewarmed = IsMigratedPluginBodyPreviewPrewarmed(
            session,
            normalizedSize);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"migration.describe paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"prewarmed={prewarmed} size={normalizedSize.WidthDip:F1}x{normalizedSize.HeightDip:F1}");
        descriptor = new EdgeCapsulePreviewDescriptor(
            size,
            normalized => CreateMigratedPluginBodyPreview(
                session,
                context,
                normalized),
            visible => SetMigratedPluginBodyPreviewVisibility(
                visible,
                session),
            () => SetMigratedPluginBodyPreviewVisibility(
                visible: false,
                session: session),
            // A warmed tree is already final-size and detached from the paper shell. Stage it before
            // animation; an unwarmed migration keeps the existing fallback-first safety path.
            DeferContentCreation: !prewarmed);
        return true;
    }

    private FrameworkElement CreateMigratedPluginBodyPreview(
        IPaperBodySession session,
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        if (IsMigratedPluginBodyPreviewPrewarmed(session, size) &&
            _migratedPluginBodyPreview is { } warmed)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.create.reuse paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"size={size.WidthDip:F1}x{size.HeightDip:F1} " +
                $"layoutValid={warmed.IsLiveViewLayoutValid}");
            return warmed;
        }

        ResetMigratedPluginBodyPreview(keepSnapshot: true);
        var fallback = BuildPluginCapsuleEdgePreviewContent(context, size);
        var preview = new MigratedPluginBodyPreview(
            size,
            fallback,
            EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id));
        _migratedPluginBodyPreview = preview;
        _migratedPluginBodyPreviewSessionGeneration = _bodySessionGeneration;

        if (_pluginBodyMiniSnapshot != null)
        {
            preview.ShowSnapshot(_pluginBodyMiniSnapshot);
        }
        else if (_pluginBodyEverPresented &&
                 TryCapturePluginBodySnapshot(session, size, out var initial))
        {
            _pluginBodyMiniSnapshot = initial;
            preview.ShowSnapshot(initial);
        }
        return preview;
    }

    private bool TryMovePluginBodyIntoPreview(
        IPaperBodySession session,
        MigratedPluginBodyPreview preview)
    {
        var moveStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var view = session.View;
        if (view.Parent is not Panel parent ||
            view.Visibility != Visibility.Visible ||
            !PluginVisualTreePolicy.IsSupportedPureWpfTree(view))
        {
            return false;
        }

        var index = parent.Children.IndexOf(view);
        if (index < 0)
        {
            return false;
        }

        parent.Children.RemoveAt(index);
        try
        {
            preview.ShowLiveView(
                view,
                () => RestoreMigratedPluginBody(session, view, parent, index));
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.move paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(moveStartedAt):F3} " +
                $"view={view.GetType().Name} result=success");
            return true;
        }
        catch
        {
            try
            {
                preview.RestoreLiveView();
            }
            catch
            {
            }
            if (view.Parent is Panel current)
            {
                current.Children.Remove(view);
            }
            if (view.Parent == null &&
                ReferenceEquals(_paperBodyHost.Current, session))
            {
                parent.Children.Insert(Math.Min(index, parent.Children.Count), view);
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.move paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(moveStartedAt):F3} " +
                $"view={view.GetType().Name} result=failed");
            return false;
        }
    }

    private void RestoreMigratedPluginBody(
        IPaperBodySession session,
        FrameworkElement view,
        Panel parent,
        int index)
    {
        if (view.Parent is Panel current)
        {
            current.Children.Remove(view);
        }
        if (view.Parent != null ||
            !ReferenceEquals(_paperBodyHost.Current, session))
        {
            return;
        }

        parent.Children.Insert(Math.Min(index, parent.Children.Count), view);
    }

    private void SetMigratedPluginBodyPreviewVisibility(
        bool visible,
        IPaperBodySession session)
    {
        var preview = _migratedPluginBodyPreview;
        if (preview == null)
        {
            return;
        }
        _migratedPluginBodyPreviewVisible = visible;

        if (!visible)
        {
            if (_migratedPluginBodyPreviewPrewarmed)
            {
                RestorePrewarmedPluginBodyForActivation("request-cancelled");
                return;
            }

            var liveView = preview.PrepareLiveViewForSnapshot();
            var snapshotStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            if (liveView != null &&
                TryCaptureVisualSnapshot(liveView, preview.Size, out var liveSnapshot))
            {
                _pluginBodyMiniSnapshot = liveSnapshot;
                preview.ShowSnapshot(liveSnapshot);
            }
            else if (liveView != null)
            {
                preview.ShowFallback();
            }
            preview.RestoreLiveView();
            ExitMigratedPluginBodyPresentation(session);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=false snapshotMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(snapshotStartedAt):F3} " +
                $"hadLive={liveView != null}");
            return;
        }

        _migratedPluginBodyPreviewPrewarmed = false;

        // Do not detach the body during descriptor creation: layout can still reject the preview
        // request. SetVisibility(true) runs only after StagePreviewContent has committed ownership.
        if (!_pluginBodyEverPresented &&
            _pluginBodyMiniSnapshot == null &&
            preview.LiveView == null &&
            TryMovePluginBodyIntoPreview(session, preview))
        {
            EnterMigratedPluginBodyPresentation(session);
            return;
        }

        if (preview.LiveView != null)
        {
            EnterMigratedPluginBodyPresentation(session);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.visibility paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=true source=prewarmed layoutValid={preview.IsLiveViewLayoutValid}");
            return;
        }

        if (preview.LiveView == null)
        {
            QueuePluginBodySnapshotRefresh(session, preview.Size, preview);
        }
    }

    private bool IsMigratedPluginBodyPreviewPrewarmed(
        IPaperBodySession session,
        EdgeCapsulePreviewSize size) =>
        _migratedPluginBodyPreviewPrewarmed &&
        _migratedPluginBodyPreviewSessionGeneration == _bodySessionGeneration &&
        ReferenceEquals(_paperBodyHost.Current, session) &&
        _migratedPluginBodyPreview is { } preview &&
        ReferenceEquals(preview.LiveView, session.View) &&
        preview.IsLiveViewLayoutValid &&
        Math.Abs(preview.Size.WidthDip - size.WidthDip) <= 0.001 &&
        Math.Abs(preview.Size.HeightDip - size.HeightDip) <= 0.001;

    private void ScheduleMigratedPluginBodyPreviewWarmup()
    {
        // WPF trees are dispatcher-affine, so "background" means an invisible ApplicationIdle
        // pass on their owning UI thread, never cross-thread Measure/Arrange. The real View stays
        // parked in this final-size wrapper until preview ownership or normal activation decides it.
        var generation = ++_migratedPluginBodyWarmupGeneration;
        CancelMigratedPluginBodyWarmupRetry();
        if (!_controller.State.ExperimentalEdgeCapsuleHoverPreview ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            !_paper.IsVisible ||
            !_paper.IsCollapsed ||
            !HasDeepCapsuleSlotPlacement ||
            _pluginBodyEverPresented ||
            _pluginBodyMiniSnapshot != null ||
            _bodyDescriptor?.Kind != PaperBodyPluginKind.Native ||
            _paperBodyHost.Current is not IPaperBodyViewMigrationProvider ||
            _paperBodyHost.Current is IPaperMiniViewProvider)
        {
            return;
        }

        EdgeCapsulePerformanceDiagnostics.Trace(
            $"migration.warmup.schedule paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"generation={generation}");
        _ = Dispatcher.BeginInvoke(
            (Action)(() => WarmMigratedPluginBodyPreview(generation)),
            DispatcherPriority.ApplicationIdle);
    }

    private void WarmMigratedPluginBodyPreview(int generation)
    {
        if (generation != _migratedPluginBodyWarmupGeneration)
        {
            return;
        }
        if (_edgeCapsule.HasActiveTransition || IsPaperFormTransitioning)
        {
            ScheduleMigratedPluginBodyWarmupRetry(generation);
            return;
        }
        CancelMigratedPluginBodyWarmupRetry();
        if (!TryGetMigratedPluginBodyWarmupCandidate(
                out var session,
                out var size))
        {
            return;
        }

        if (IsMigratedPluginBodyPreviewPrewarmed(session, size))
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.reuse paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"generation={generation}");
            return;
        }

        var warmupStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        MigratedPluginBodyPreview? warmingPreview = null;
        ResetMigratedPluginBodyPreview(
            keepSnapshot: true,
            cancelWarmup: false);
        try
        {
            var context = CreateEdgeCapsulePreviewContext();
            var fallback = BuildPluginCapsuleEdgePreviewContent(context, size);
            if (generation != _migratedPluginBodyWarmupGeneration ||
                !ReferenceEquals(_paperBodyHost.Current, session))
            {
                return;
            }

            var preview = new MigratedPluginBodyPreview(
                size,
                fallback,
                EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id));
            warmingPreview = preview;
            if (!TryMovePluginBodyIntoPreview(session, preview) ||
                !preview.PrepareLiveViewLayout())
            {
                DiscardMigratedPluginBodyWarmup(preview);
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"migration.warmup.fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(warmupStartedAt):F3}");
                return;
            }

            if (generation != _migratedPluginBodyWarmupGeneration ||
                !ReferenceEquals(_paperBodyHost.Current, session) ||
                !_paper.IsCollapsed ||
                !HasDeepCapsuleSlotPlacement ||
                _pluginBodyEverPresented ||
                _migratedPluginBodyPreview != null)
            {
                DiscardMigratedPluginBodyWarmup(preview);
                return;
            }

            _migratedPluginBodyPreview = preview;
            _migratedPluginBodyPreviewSessionGeneration =
                _bodySessionGeneration;
            _migratedPluginBodyPreviewPrewarmed = true;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.ready paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(warmupStartedAt):F3} " +
                $"size={size.WidthDip:F1}x{size.HeightDip:F1} " +
                $"view={session.View.GetType().Name}");
        }
        catch (Exception ex)
        {
            if (warmingPreview != null)
            {
                DiscardMigratedPluginBodyWarmup(warmingPreview);
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.fail paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(warmupStartedAt):F3} " +
                $"exception={ex.GetType().Name}");
        }
    }

    private void ScheduleMigratedPluginBodyWarmupRetry(int generation)
    {
        if (generation != _migratedPluginBodyWarmupGeneration)
        {
            return;
        }
        if (++_migratedPluginBodyWarmupRetryCount > 40)
        {
            CancelMigratedPluginBodyWarmupRetry();
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.skip paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"generation={generation} reason=transition-timeout");
            return;
        }

        var timer = _migratedPluginBodyWarmupRetryTimer;
        if (timer == null)
        {
            timer = new DispatcherTimer(
                DispatcherPriority.ApplicationIdle,
                Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += OnMigratedPluginBodyWarmupRetryTick;
            _migratedPluginBodyWarmupRetryTimer = timer;
        }
        _migratedPluginBodyWarmupRetryGeneration = generation;
        timer.Stop();
        timer.Start();
        if (_migratedPluginBodyWarmupRetryCount == 1 ||
            _migratedPluginBodyWarmupRetryCount % 5 == 0)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.defer paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"generation={generation} retry={_migratedPluginBodyWarmupRetryCount} " +
                "reason=transition");
        }
    }

    private void OnMigratedPluginBodyWarmupRetryTick(
        object? sender,
        EventArgs e)
    {
        var timer = _migratedPluginBodyWarmupRetryTimer;
        timer?.Stop();
        var generation = _migratedPluginBodyWarmupRetryGeneration;
        _migratedPluginBodyWarmupRetryGeneration = 0;
        if (generation == _migratedPluginBodyWarmupGeneration)
        {
            WarmMigratedPluginBodyPreview(generation);
        }
    }

    private void CancelMigratedPluginBodyWarmupRetry()
    {
        _migratedPluginBodyWarmupRetryGeneration = 0;
        _migratedPluginBodyWarmupRetryCount = 0;
        _migratedPluginBodyWarmupRetryTimer?.Stop();
    }

    private bool TryGetMigratedPluginBodyWarmupCandidate(
        out IPaperBodySession session,
        out EdgeCapsulePreviewSize size)
    {
        session = null!;
        size = default;
        if (!CanEnterEdgeCapsulePreview ||
            _edgeCapsulePreviewRequest != null ||
            _migratedPluginBodyPreviewVisible ||
            _pluginBodyEverPresented ||
            _pluginBodyMiniSnapshot != null ||
            _bodyDescriptor?.Kind != PaperBodyPluginKind.Native ||
            _paperBodyHost.Current is not { } current ||
            current is IPaperMiniViewProvider ||
            current is not IPaperBodyViewMigrationProvider migrationProvider ||
            current.View is Window ||
            current.View.Visibility != Visibility.Visible ||
            !PluginVisualTreePolicy.IsSupportedPureWpfTree(current.View))
        {
            return false;
        }

        var preferredSize = ReadPreferredMiniSize(
            () => migrationProvider.PreferredMigratedMiniViewSize,
            new PaperMiniViewSize(360, 260));
        var normalizedSize = NormalizePluginMiniSizeForCurrentMonitor(
            preferredSize);
        if (!IsMigratedPluginBodyPreviewPrewarmed(
                current,
                normalizedSize) &&
            current.View.Parent is not Panel)
        {
            return false;
        }

        session = current;
        size = normalizedSize;
        return true;
    }

    private void DiscardMigratedPluginBodyWarmup(
        MigratedPluginBodyPreview preview)
    {
        preview.RestoreLiveView();
        if (!ReferenceEquals(_migratedPluginBodyPreview, preview))
        {
            return;
        }

        _migratedPluginBodyPreview = null;
        _migratedPluginBodyPreviewSessionGeneration = -1;
        _migratedPluginBodyPreviewPrewarmed = false;
        _migratedPluginBodyPreviewVisible = false;
    }

    private void RestorePrewarmedPluginBodyForActivation(
        string reason = "activation")
    {
        if (!_migratedPluginBodyPreviewPrewarmed)
        {
            return;
        }

        _migratedPluginBodyWarmupGeneration++;
        CancelMigratedPluginBodyWarmupRetry();
        _migratedPluginBodyPreviewPrewarmed = false;
        _migratedPluginBodyPreview?.RestoreLiveView();
        _migratedPluginBodyPreview = null;
        _migratedPluginBodyPreviewSessionGeneration = -1;
        _migratedPluginBodyPreviewVisible = false;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"migration.warmup.restore paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"reason={reason}");
    }

    private void QueuePluginBodySnapshotRefresh(
        IPaperBodySession session,
        EdgeCapsulePreviewSize size,
        MigratedPluginBodyPreview? target = null)
    {
        var generation = ++_pluginBodyMiniSnapshotGeneration;
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _pluginBodyMiniSnapshotGeneration ||
                    !ReferenceEquals(_paperBodyHost.Current, session) ||
                    !TryCapturePluginBodySnapshot(session, size, out var snapshot))
                {
                    return;
                }

                _pluginBodyMiniSnapshot = snapshot;
                if (target != null &&
                    ReferenceEquals(target, _migratedPluginBodyPreview) &&
                    _migratedPluginBodyPreviewVisible)
                {
                    target.ShowSnapshot(snapshot);
                }
            }),
            DispatcherPriority.Render);
    }

    private bool TryCapturePluginBodySnapshot(
        IPaperBodySession session,
        EdgeCapsulePreviewSize size,
        out ImageSource snapshot)
    {
        snapshot = null!;
        return ReferenceEquals(_paperBodyHost.Current, session) &&
            session.View.Parent != null &&
            PluginVisualTreePolicy.IsSupportedPureWpfTree(session.View) &&
            TryCaptureVisualSnapshot(session.View, size, out snapshot);
    }

    private bool TryCaptureVisualSnapshot(
        FrameworkElement view,
        EdgeCapsulePreviewSize size,
        out ImageSource snapshot)
    {
        var snapshotStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        snapshot = null!;
        try
        {
            var targetWidth = Math.Max(
                1,
                size.WidthDip - CapsuleCloseWidth - WindowChromeMargin);
            var targetHeight = Math.Max(
                1,
                size.HeightDip - WindowChromeMargin * 2);
            var sourceWidth = view.ActualWidth > 1
                ? view.ActualWidth
                : Math.Max(PaperLayoutDefaults.MinWidth, _paper.Width);
            var sourceHeight = view.ActualHeight > 1
                ? view.ActualHeight
                : Math.Max(PaperLayoutDefaults.MinHeight, _paper.Height);
            var scale = Math.Min(
                targetWidth / Math.Max(1, sourceWidth),
                targetHeight / Math.Max(1, sourceHeight));
            var drawWidth = Math.Max(1, sourceWidth * scale);
            var drawHeight = Math.Max(1, sourceHeight * scale);
            var targetRect = new Rect(
                (targetWidth - drawWidth) / 2,
                (targetHeight - drawHeight) / 2,
                drawWidth,
                drawHeight);

            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(
                    new VisualBrush(view)
                    {
                        Stretch = Stretch.Fill,
                        AlignmentX = AlignmentX.Center,
                        AlignmentY = AlignmentY.Center
                    },
                    null,
                    targetRect);
            }

            var dpi = DeepCapsuleSlotDpi();
            var pixelsWide = Math.Max(
                1,
                (int)Math.Ceiling(targetWidth * dpi.DpiScaleX));
            var pixelsHigh = Math.Max(
                1,
                (int)Math.Ceiling(targetHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                pixelsWide,
                pixelsHigh,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            snapshot = bitmap;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.snapshot.render paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(snapshotStartedAt):F3} " +
                $"source={sourceWidth:F1}x{sourceHeight:F1} target={targetWidth:F1}x{targetHeight:F1} " +
                $"pixels={pixelsWide}x{pixelsHigh} result=success");
            return true;
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.snapshot.render paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(snapshotStartedAt):F3} " +
                $"result=failed exception={ex.GetType().Name}");
            return false;
        }
    }

    private void CaptureMigratedPluginBodyOnPointerLeave()
    {
        if (_paperBodyHost.Current is not IPaperBodySession session ||
            session is not IPaperBodyViewMigrationProvider provider ||
            !_controller.State.ExperimentalEdgeCapsuleHoverPreview ||
            !HasDeepCapsuleSlotPlacement ||
            !_pluginBodyEverPresented)
        {
            return;
        }

        var size = NormalizePluginMiniSizeForCurrentMonitor(
            ReadPreferredMiniSize(
                () => provider.PreferredMigratedMiniViewSize,
                new PaperMiniViewSize(360, 260)));
        QueuePluginBodySnapshotRefresh(session, size);
    }

    private void EnterMigratedPluginBodyPresentation(IPaperBodySession session)
    {
        if (_migratedPluginBodySessionPresented ||
            !ReferenceEquals(_paperBodyHost.Current, session))
        {
            return;
        }

        _migratedPluginBodyPreviousRuntimeVisible = _bodyRuntimeVisible;
        _bodyRuntimeVisible = true;
        _migratedPluginBodySessionPresented = true;
        var presentationStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        try
        {
            session.OnPresentationChanged(true);
            var presentationMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    presentationStartedAt);
            var visibilityStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
            session.OnVisibilityChanged(true);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.callbacks paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=true presentationMs={presentationMilliseconds:F3} " +
                $"visibilityMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3} " +
                "result=success");
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.callbacks paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=true totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(presentationStartedAt):F3} " +
                $"result=failed exception={ex.GetType().Name}");
            ExitMigratedPluginBodyPresentation(session);
        }
    }

    private void ExitMigratedPluginBodyPresentation(IPaperBodySession session)
    {
        if (!_migratedPluginBodySessionPresented)
        {
            return;
        }

        _migratedPluginBodySessionPresented = false;
        _bodyRuntimeVisible = _migratedPluginBodyPreviousRuntimeVisible;
        if (!ReferenceEquals(_paperBodyHost.Current, session))
        {
            return;
        }
        var callbacksStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        try
        {
            session.OnPresentationChanged(false);
            var presentationMilliseconds =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    callbacksStartedAt);
            var visibilityStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
            session.OnVisibilityChanged(_bodyRuntimeVisible);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.callbacks paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=false presentationMs={presentationMilliseconds:F3} " +
                $"visibilityMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(visibilityStartedAt):F3} " +
                "result=success");
        }
        catch (Exception ex)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.callbacks paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                $"visible=false totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callbacksStartedAt):F3} " +
                $"result=failed exception={ex.GetType().Name}");
            // Migration is optional; normal paper activation can still retry session callbacks.
        }
    }

    private partial void ResetMigratedPluginBodyPreview() =>
        ResetMigratedPluginBodyPreview(keepSnapshot: false);

    private void ResetMigratedPluginBodyPreview(
        bool keepSnapshot,
        bool cancelWarmup = true)
    {
        if (cancelWarmup)
        {
            _migratedPluginBodyWarmupGeneration++;
            CancelMigratedPluginBodyWarmupRetry();
        }
        _pluginBodyMiniSnapshotGeneration++;
        _migratedPluginBodyPreview?.RestoreLiveView();
        if (_paperBodyHost.Current is { } session)
        {
            ExitMigratedPluginBodyPresentation(session);
        }
        _migratedPluginBodyPreview = null;
        _migratedPluginBodyPreviewSessionGeneration = -1;
        _migratedPluginBodyPreviewPrewarmed = false;
        _migratedPluginBodyPreviewVisible = false;
        if (!keepSnapshot)
        {
            _pluginBodyMiniSnapshot = null;
            _pluginBodyEverPresented = false;
        }
    }

    private sealed class MigratedPluginBodyPreview : Grid
    {
        private readonly FrameworkElement _fallback;
        private readonly Image _snapshot;
        private Action? _restoreLiveView;
        private bool _previousHitTestVisible;
        private double _previousOpacity;
        private Visibility _previousVisibility;
        private int _liveRevealGeneration;
        private readonly string _diagnosticId;
        private bool _liveViewPrearranged;

        public MigratedPluginBodyPreview(
            EdgeCapsulePreviewSize size,
            FrameworkElement fallback,
            string diagnosticId)
        {
            Size = size;
            _diagnosticId = diagnosticId;
            _fallback = fallback;
            if (_fallback is EdgeCapsuleLivePreviewView livePreview)
            {
                livePreview.PrepareForFirstDisplay();
            }
            _snapshot = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            Background = Brushes.Transparent;
            ClipToBounds = true;
            Children.Add(_fallback);
            Children.Add(_snapshot);
        }

        public FrameworkElement? LiveView { get; private set; }
        public EdgeCapsulePreviewSize Size { get; }
        public bool IsLiveViewLayoutValid =>
            _liveViewPrearranged &&
            LiveView is { IsMeasureValid: true, IsArrangeValid: true };

        public void ShowLiveView(FrameworkElement view, Action restore)
        {
            RestoreLiveView();
            LiveView = view;
            _restoreLiveView = restore;
            _previousHitTestVisible = view.IsHitTestVisible;
            _previousOpacity = view.Opacity;
            _previousVisibility = view.Visibility;
            _liveViewPrearranged = false;
            view.Opacity = 0;
            _fallback.Visibility = Visibility.Visible;
            _snapshot.Visibility = Visibility.Collapsed;
            Children.Add(view);
            var generation = ++_liveRevealGeneration;
            _ = Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (generation != _liveRevealGeneration ||
                        !ReferenceEquals(LiveView, view))
                    {
                        return;
                    }
                    var layoutStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
                    var forcedLayout = !IsLiveViewLayoutValid;
                    if (forcedLayout)
                    {
                        view.UpdateLayout();
                    }
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"migration.reveal.layout paper={_diagnosticId} " +
                        $"forced={forcedLayout} " +
                        $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(layoutStartedAt):F3} " +
                        $"actual={view.ActualWidth:F1}x{view.ActualHeight:F1} " +
                        $"valid={view.IsMeasureValid && view.IsArrangeValid}");
                    view.Opacity = _previousOpacity;
                    _ = Dispatcher.BeginInvoke(
                        (Action)(() =>
                        {
                            if (generation == _liveRevealGeneration &&
                                ReferenceEquals(LiveView, view))
                            {
                                _fallback.Visibility = Visibility.Collapsed;
                            }
                        }),
                        DispatcherPriority.Render);
                }),
                DispatcherPriority.Loaded);
        }

        public bool PrepareLiveViewLayout()
        {
            var view = LiveView;
            if (view == null)
            {
                return false;
            }

            var contentWidth = Math.Max(
                1,
                Size.WidthDip - CapsuleCloseWidth - WindowChromeMargin);
            var contentHeight = Math.Max(
                1,
                Size.HeightDip - WindowChromeMargin * 2);
            var finalSize = new Size(contentWidth, contentHeight);
            Width = contentWidth;
            Height = contentHeight;
            var layoutStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            Measure(finalSize);
            Arrange(new Rect(0, 0, contentWidth, contentHeight));
            UpdateLayout();
            _liveViewPrearranged =
                view.IsMeasureValid &&
                view.IsArrangeValid;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"migration.warmup.layout paper={_diagnosticId} " +
                $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(layoutStartedAt):F3} " +
                $"size={contentWidth:F1}x{contentHeight:F1} " +
                $"desired={view.DesiredSize.Width:F1}x{view.DesiredSize.Height:F1} " +
                $"actual={view.ActualWidth:F1}x{view.ActualHeight:F1} " +
                $"valid={_liveViewPrearranged}");
            return _liveViewPrearranged;
        }

        public void ShowSnapshot(ImageSource source)
        {
            _snapshot.Source = source;
            _snapshot.Visibility = Visibility.Visible;
            _fallback.Visibility = Visibility.Collapsed;
            if (LiveView != null)
            {
                LiveView.Visibility = Visibility.Collapsed;
            }
        }

        public FrameworkElement? PrepareLiveViewForSnapshot()
        {
            var view = LiveView;
            if (view == null)
            {
                return null;
            }

            // A very quick activation can arrive before the deferred reveal restored the plugin's
            // original opacity. Capture that real visual state, not the temporary zero-opacity
            // hand-off state, and cancel the pending reveal before the View is reparented.
            _liveRevealGeneration++;
            view.Visibility = Visibility.Visible;
            view.Opacity = _previousOpacity;
            if (!view.IsMeasureValid || !view.IsArrangeValid)
            {
                var layoutStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
                view.UpdateLayout();
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"migration.snapshot.layout paper={_diagnosticId} " +
                    $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(layoutStartedAt):F3} " +
                    $"valid={view.IsMeasureValid && view.IsArrangeValid}");
            }
            return view;
        }

        public void ShowFallback()
        {
            _snapshot.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            if (LiveView != null)
            {
                LiveView.Visibility = Visibility.Collapsed;
            }
        }

        public void RestoreLiveView()
        {
            var view = LiveView;
            var restore = _restoreLiveView;
            _liveRevealGeneration++;
            LiveView = null;
            _restoreLiveView = null;
            _liveViewPrearranged = false;
            if (view != null)
            {
                Children.Remove(view);
                view.IsHitTestVisible = _previousHitTestVisible;
                view.Opacity = _previousOpacity;
                view.Visibility = _previousVisibility;
            }
            restore?.Invoke();
        }
    }
}
