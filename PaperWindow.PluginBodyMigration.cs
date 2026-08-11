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
            DeferContentCreation: true);
        return true;
    }

    private FrameworkElement CreateMigratedPluginBodyPreview(
        IPaperBodySession session,
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        ResetMigratedPluginBodyPreview(keepSnapshot: true);
        var fallback = BuildPluginCapsuleEdgePreviewContent(context, size);
        var preview = new MigratedPluginBodyPreview(size, fallback);
        _migratedPluginBodyPreview = preview;

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
            var liveView = preview.PrepareLiveViewForSnapshot();
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
            return;
        }

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

        if (preview.LiveView == null)
        {
            QueuePluginBodySnapshotRefresh(session, preview.Size, preview);
        }
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
            return true;
        }
        catch
        {
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
        try
        {
            session.OnPresentationChanged(true);
            session.OnVisibilityChanged(true);
        }
        catch
        {
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
        try
        {
            session.OnPresentationChanged(false);
            session.OnVisibilityChanged(_bodyRuntimeVisible);
        }
        catch
        {
            // Migration is optional; normal paper activation can still retry session callbacks.
        }
    }

    private partial void ResetMigratedPluginBodyPreview() =>
        ResetMigratedPluginBodyPreview(keepSnapshot: false);

    private void ResetMigratedPluginBodyPreview(bool keepSnapshot)
    {
        _pluginBodyMiniSnapshotGeneration++;
        _migratedPluginBodyPreview?.RestoreLiveView();
        if (_paperBodyHost.Current is { } session)
        {
            ExitMigratedPluginBodyPresentation(session);
        }
        _migratedPluginBodyPreview = null;
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

        public MigratedPluginBodyPreview(
            EdgeCapsulePreviewSize size,
            FrameworkElement fallback)
        {
            Size = size;
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

        public void ShowLiveView(FrameworkElement view, Action restore)
        {
            RestoreLiveView();
            LiveView = view;
            _restoreLiveView = restore;
            _previousHitTestVisible = view.IsHitTestVisible;
            _previousOpacity = view.Opacity;
            _previousVisibility = view.Visibility;
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
                    view.UpdateLayout();
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
            view.UpdateLayout();
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
