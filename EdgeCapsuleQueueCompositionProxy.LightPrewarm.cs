using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private const int LightweightWpfSourceCount = 2;
    private const int LightweightWpfWidth = 32;
    private const int LightweightWpfHeight = 24;
    private const int LightweightWpfShift = 4;
    private const int WmMove = 0x0003;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmWindowPosChanged = 0x0047;

    private sealed class LightweightPrewarmState
    {
        public bool Attempted { get; set; }
        public bool Completed { get; set; }
    }

    private readonly record struct LightweightPrewarmProbeTimings(
        double WindowMilliseconds,
        double TargetMilliseconds,
        double VisualMilliseconds,
        double AnimationMilliseconds,
        double RootCommitMilliseconds,
        double ShowFlushMilliseconds,
        double CloakRoundTripMilliseconds,
        double WpfSourceMilliseconds,
        double WpfInitialRenderMilliseconds,
        double WpfSurfaceMilliseconds,
        double WpfEndpointBatchMilliseconds,
        double WpfPostMoveRenderMilliseconds,
        double WpfAnimationMilliseconds,
        double WpfPublishMilliseconds,
        double WpfUncloakMilliseconds,
        double CleanupMilliseconds,
        double TotalMilliseconds,
        bool CloakRoundTripSucceeded,
        bool WpfHwndSucceeded,
        int WpfWindowPosChanging,
        int WpfWindowPosChanged,
        int WpfMoveMessages);

#if DEBUG
    private readonly record struct LightweightPrewarmProcessSnapshot(
        long PrivateBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes,
        int HandleCount);
#endif

    private sealed class LightweightWpfSource : IDisposable
    {
        private HwndSource? _source;
        private readonly HwndSourceHook _hook;
        private bool _disposed;

        private LightweightWpfSource(
            Window window,
            Border root,
            IntPtr handle,
            HwndSource source)
        {
            Window = window;
            Root = root;
            Handle = handle;
            _source = source;
            _hook = OnNativeMessage;
            source.AddHook(_hook);
        }

        public Window Window { get; }
        public Border Root { get; }
        public IntPtr Handle { get; }
        public int WindowPosChanging { get; private set; }
        public int WindowPosChanged { get; private set; }
        public int MoveMessages { get; private set; }

        public static LightweightWpfSource Create(DeviceScreenRect bounds)
        {
            var root = new Border
            {
                Width = LightweightWpfWidth,
                Height = LightweightWpfHeight,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            var window = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Topmost = true,
                Left = -32000,
                Top = -32000,
                Width = LightweightWpfWidth,
                Height = LightweightWpfHeight,
                Content = root
            };

            try
            {
                window.Show();
                WindowNative.ApplyNoActivateStyle(window);
                var handle = new WindowInteropHelper(window).Handle;
                var source = handle == IntPtr.Zero
                    ? null
                    : HwndSource.FromHwnd(handle);
                if (source == null)
                {
                    throw new InvalidOperationException(
                        "The WPF prewarm HwndSource could not be resolved.");
                }

                var result = new LightweightWpfSource(
                    window,
                    root,
                    handle,
                    source);
                if (!WindowNative.TrySetWindowDeviceBounds(window, bounds))
                {
                    result.Dispose();
                    throw new InvalidOperationException(
                        "The WPF prewarm HWND could not be positioned.");
                }
                return result;
            }
            catch
            {
                try { window.Close(); } catch { }
                throw;
            }
        }

        public void ResetGeometryMessageCounts()
        {
            WindowPosChanging = 0;
            WindowPosChanged = 0;
            MoveMessages = 0;
        }

        private IntPtr OnNativeMessage(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
#if DEBUG
            WindowNative.ObserveNativeGeometryMessage(hwnd, msg);
#endif
            switch (msg)
            {
                case WmWindowPosChanging:
                    WindowPosChanging++;
                    break;
                case WmWindowPosChanged:
                    WindowPosChanged++;
                    break;
                case WmMove:
                    MoveMessages++;
                    break;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                if (_source != null)
                {
                    _source.RemoveHook(_hook);
                }
            }
            catch { }
            _source = null;
            try { Window.Hide(); } catch { }
            try { Window.Close(); } catch { }
        }
    }

    private static readonly ConditionalWeakTable<
        Dispatcher,
        LightweightPrewarmState> LightweightPrewarmStates = new();

    internal static void PrewarmLightweight(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => PrewarmLightweight(dispatcher)));
            return;
        }

        var state = LightweightPrewarmStates.GetValue(
            dispatcher,
            static _ => new LightweightPrewarmState());
        if (state.Attempted)
        {
            return;
        }
        state.Attempted = true;

        var totalStartedAt = Stopwatch.GetTimestamp();
        var runtimeMilliseconds = 0d;
        var spareMilliseconds = 0d;
        var probe = default(LightweightPrewarmProbeTimings);
        var runtimeReady = false;
        var publicationReady = false;
#if DEBUG
        var processBefore = CaptureLightweightPrewarmProcessSnapshot();
        EdgeCapsulePerformanceDiagnostics.Trace(
            "prewarm.light phase=start wpfPrewarmed=False");
#endif

        try
        {
            var runtimeStartedAt = Stopwatch.GetTimestamp();
            runtimeReady = TryGetRuntime(dispatcher, out var runtime);
            runtimeMilliseconds = Stopwatch.GetElapsedTime(
                runtimeStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (!runtimeReady)
            {
                return;
            }

            var spareStartedAt = Stopwatch.GetTimestamp();
            runtime.PrewarmOutputHost();
            spareMilliseconds = Stopwatch.GetElapsedTime(
                spareStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            publicationReady = TryRunLightweightPublicationProbe(
                runtime,
                dispatcher,
                out probe);
            state.Completed =
                publicationReady &&
                probe.CloakRoundTripSucceeded &&
                probe.WpfHwndSucceeded;
        }
        finally
        {
#if DEBUG
            var processAfter = CaptureLightweightPrewarmProcessSnapshot();
            var totalMilliseconds = Stopwatch.GetElapsedTime(
                totalStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"prewarm.light phase=publication-probe " +
                $"outcome={(publicationReady ? "success" : "failed")} " +
                $"windowMs={probe.WindowMilliseconds:F3} " +
                $"targetMs={probe.TargetMilliseconds:F3} " +
                $"visualMs={probe.VisualMilliseconds:F3} " +
                $"animationMs={probe.AnimationMilliseconds:F3} " +
                $"rootCommitMs={probe.RootCommitMilliseconds:F3} " +
                $"showFlushMs={probe.ShowFlushMilliseconds:F3} " +
                $"cloakRoundTripMs={probe.CloakRoundTripMilliseconds:F3} " +
                $"cloakRoundTrip={probe.CloakRoundTripSucceeded} " +
                $"cleanupMs={probe.CleanupMilliseconds:F3} " +
                $"probeTotalMs={probe.TotalMilliseconds:F3}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"prewarm.light phase=wpf-hwnd-probe " +
                $"outcome={(probe.WpfHwndSucceeded ? "success" : "failed")} " +
                $"sources={LightweightWpfSourceCount} " +
                $"sourceMs={probe.WpfSourceMilliseconds:F3} " +
                $"initialRenderMs={probe.WpfInitialRenderMilliseconds:F3} " +
                $"surfaceMs={probe.WpfSurfaceMilliseconds:F3} " +
                $"endpointBatchMs={probe.WpfEndpointBatchMilliseconds:F3} " +
                $"postMoveRenderMs={probe.WpfPostMoveRenderMilliseconds:F3} " +
                $"animationMs={probe.WpfAnimationMilliseconds:F3} " +
                $"publishMs={probe.WpfPublishMilliseconds:F3} " +
                $"uncloakMs={probe.WpfUncloakMilliseconds:F3} " +
                $"windowPosChanging={probe.WpfWindowPosChanging} " +
                $"windowPosChanged={probe.WpfWindowPosChanged} " +
                $"moveMessages={probe.WpfMoveMessages}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"prewarm.light phase=complete " +
                $"outcome={(state.Completed ? "success" : "failed")} " +
                $"runtimeReady={runtimeReady} runtimeMs={runtimeMilliseconds:F3} " +
                $"spareMs={spareMilliseconds:F3} totalMs={totalMilliseconds:F3} " +
                $"privateDeltaMiB={ToLightweightPrewarmMiB(processAfter.PrivateBytes - processBefore.PrivateBytes):F3} " +
                $"workingSetDeltaMiB={ToLightweightPrewarmMiB(processAfter.WorkingSetBytes - processBefore.WorkingSetBytes):F3} " +
                $"managedHeapDeltaMiB={ToLightweightPrewarmMiB(processAfter.ManagedHeapBytes - processBefore.ManagedHeapBytes):F3} " +
                $"handlesDelta={processAfter.HandleCount - processBefore.HandleCount} " +
                $"wpfPrewarmed={probe.WpfHwndSucceeded}");
#endif
        }
    }

    private static bool TryRunLightweightPublicationProbe(
        SharedRuntime runtime,
        Dispatcher dispatcher,
        out LightweightPrewarmProbeTimings timings)
    {
        timings = default;
        var totalStartedAt = Stopwatch.GetTimestamp();
        var windowMilliseconds = 0d;
        var targetMilliseconds = 0d;
        var visualMilliseconds = 0d;
        var animationMilliseconds = 0d;
        var rootCommitMilliseconds = 0d;
        var showFlushMilliseconds = 0d;
        var cloakRoundTripMilliseconds = 0d;
        var wpfSourceMilliseconds = 0d;
        var wpfInitialRenderMilliseconds = 0d;
        var wpfSurfaceMilliseconds = 0d;
        var wpfEndpointBatchMilliseconds = 0d;
        var wpfPostMoveRenderMilliseconds = 0d;
        var wpfAnimationMilliseconds = 0d;
        var wpfPublishMilliseconds = 0d;
        var wpfUncloakMilliseconds = 0d;
        var cleanupMilliseconds = 0d;
        var cloakRoundTripSucceeded = false;
        var wpfHwndSucceeded = false;
        var publicationReady = false;
        var wpfWindowPosChanging = 0;
        var wpfWindowPosChanged = 0;
        var wpfMoveMessages = 0;

        EdgeCapsuleQueueProxyWindow? window = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? root = null;
        IDCompositionAnimation? animation = null;
        var wpfSources = new List<LightweightWpfSource>(
            LightweightWpfSourceCount);
        var sourceBounds = new List<DeviceScreenRect>(
            LightweightWpfSourceCount);
        var targetBounds = new List<DeviceScreenRect>(
            LightweightWpfSourceCount);
        var surfaces = new List<IUnknown>(
            LightweightWpfSourceCount);
        var visuals = new List<IDCompositionVisual>(
            LightweightWpfSourceCount);
        var wpfAnimations = new List<IDCompositionAnimation>(
            LightweightWpfSourceCount);

        try
        {
            var offscreen =
                new DeviceScreenRect(-32000, -32000, -31904, -31936);

            var windowStartedAt = Stopwatch.GetTimestamp();
            window = EdgeCapsuleQueueProxyWindow.TryCreate(
                offscreen,
                topmost: true,
                static _ => false,
                static (_, _) => { },
                static () => { },
                static () => { },
                static () => { });
            windowMilliseconds = Stopwatch.GetElapsedTime(
                windowStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (window == null)
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm output window could not be created.");
            }

            var targetStartedAt = Stopwatch.GetTimestamp();
            runtime.Device.CreateTargetForHwnd(
                window.Handle,
                topmost: true,
                out target).CheckError();
            targetMilliseconds = Stopwatch.GetElapsedTime(
                targetStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var visualStartedAt = Stopwatch.GetTimestamp();
            runtime.Device.CreateVisual(out root).CheckError();
            visualMilliseconds = Stopwatch.GetElapsedTime(
                visualStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var animationStartedAt = Stopwatch.GetTimestamp();
            animation = runtime.Device.CreateAnimation();
            const double animationDurationSeconds = 0.016;
            const float delta = 1f;
            animation.SetAbsoluteBeginTime(
                Stopwatch.GetTimestamp()).CheckError();
            animation.AddCubic(
                0,
                0,
                (float)(3 * delta / animationDurationSeconds),
                (float)(-3 * delta /
                    (animationDurationSeconds * animationDurationSeconds)),
                (float)(delta /
                    (animationDurationSeconds * animationDurationSeconds *
                     animationDurationSeconds))).CheckError();
            animation.End(animationDurationSeconds, delta).CheckError();
            root.SetOffsetX(animation).CheckError();
            animationMilliseconds = Stopwatch.GetElapsedTime(
                animationStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var rootCommitStartedAt = Stopwatch.GetTimestamp();
            target.SetRoot(root).CheckError();
            runtime.Device.Commit().CheckError();
            rootCommitMilliseconds = Stopwatch.GetElapsedTime(
                rootCommitStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var showFlushStartedAt = Stopwatch.GetTimestamp();
            if (!window.Show(offscreen, topmost: true) ||
                !WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm publication boundary failed.");
            }
            publicationReady = true;
            showFlushMilliseconds = Stopwatch.GetElapsedTime(
                showFlushStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var cloakStartedAt = Stopwatch.GetTimestamp();
            var cloakResult = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        window.Handle,
                        Cloaked: true,
                        RollbackCloaked: false)
                });
            var uncloakResult = cloakResult ==
                WindowNative.WindowCloakBatchResult.Success
                    ? WindowNative.TrySetWindowCloakedBatchDetailed(
                        new[]
                        {
                            new WindowNative.WindowCloakChange(
                                window.Handle,
                                Cloaked: false,
                                RollbackCloaked: true)
                        })
                    : WindowNative.WindowCloakBatchResult.RolledBack;
            cloakRoundTripSucceeded =
                cloakResult == WindowNative.WindowCloakBatchResult.Success &&
                uncloakResult == WindowNative.WindowCloakBatchResult.Success;
            cloakRoundTripMilliseconds = Stopwatch.GetElapsedTime(
                cloakStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            // The plain Win32 probe did not warm the remaining cold path: its endpoint batch
            // generated no WM_WINDOWPOS* or WM_MOVE traffic, while real edge hosts do. Use two
            // tiny off-screen WPF layered windows that match EdgeCapsuleHost's HWND kind, drain
            // their first WPF Render pass, wrap those real HwndSource surfaces in DComp, then move
            // them inside the coordinated cloak callback.
            var sourceStartedAt = Stopwatch.GetTimestamp();
            for (var index = 0; index < LightweightWpfSourceCount; index++)
            {
                var left = offscreen.Left + 8 + index * 40;
                var top = offscreen.Top + 12;
                var bounds = new DeviceScreenRect(
                    left,
                    top,
                    left + LightweightWpfWidth,
                    top + LightweightWpfHeight);
                var moved = new DeviceScreenRect(
                    bounds.Left,
                    bounds.Top + LightweightWpfShift,
                    bounds.Right,
                    bounds.Bottom + LightweightWpfShift);
                wpfSources.Add(LightweightWpfSource.Create(bounds));
                sourceBounds.Add(bounds);
                targetBounds.Add(moved);
            }
            wpfSourceMilliseconds = Stopwatch.GetElapsedTime(
                sourceStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var initialRenderStartedAt = Stopwatch.GetTimestamp();
            foreach (var source in wpfSources)
            {
                source.Window.UpdateLayout();
                source.Root.UpdateLayout();
            }
            dispatcher.Invoke(
                DispatcherPriority.Render,
                static () => { });
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The WPF prewarm source render barrier failed.");
            }
            wpfInitialRenderMilliseconds = Stopwatch.GetElapsedTime(
                initialRenderStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var surfaceStartedAt = Stopwatch.GetTimestamp();
            IDCompositionVisual? reference = null;
            for (var index = 0; index < wpfSources.Count; index++)
            {
                runtime.Device.CreateSurfaceFromHwnd(
                    wpfSources[index].Handle,
                    out var surface).CheckError();
                surfaces.Add(surface);
                runtime.Device.CreateVisual(
                    out IDCompositionVisual2 sourceVisual).CheckError();
                visuals.Add(sourceVisual);
                sourceVisual.SetContent(surface).CheckError();
                sourceVisual.SetBitmapInterpolationMode(
                    BitmapInterpolationMode.Linear).CheckError();
                sourceVisual.SetBorderMode(BorderMode.Soft).CheckError();
                sourceVisual.SetOffsetX(
                    sourceBounds[index].Left - offscreen.Left).CheckError();
                sourceVisual.SetOffsetY(
                    sourceBounds[index].Top - offscreen.Top).CheckError();
                root.AddVisual(
                    sourceVisual,
                    insertAbove: true,
                    reference!).CheckError();
                reference = sourceVisual;
            }
            runtime.Device.Commit().CheckError();
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The WPF redirected source visual cover could not be published.");
            }
            wpfSurfaceMilliseconds = Stopwatch.GetElapsedTime(
                surfaceStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            foreach (var source in wpfSources)
            {
                source.ResetGeometryMessageCounts();
            }

            var coordinatedStartedAt = Stopwatch.GetTimestamp();
            var coordinatedResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    wpfSources
                        .Select(source =>
                            new WindowNative.WindowCloakChange(
                                source.Handle,
                                Cloaked: true,
                                RollbackCloaked: false))
                        .ToArray(),
                    () =>
                    {
                        var endpointStartedAt = Stopwatch.GetTimestamp();
                        using (var batch =
                               WindowNative.BeginWindowDeviceBoundsBatch(
                                   wpfSources.Count))
                        {
                            for (var index = 0;
                                 index < wpfSources.Count;
                                 index++)
                            {
                                if (!batch.TryDefer(
                                        wpfSources[index].Handle,
                                        targetBounds[index]))
                                {
                                    return false;
                                }
                            }
                            if (!batch.Commit())
                            {
                                return false;
                            }
                        }
                        wpfEndpointBatchMilliseconds =
                            Stopwatch.GetElapsedTime(
                                endpointStartedAt,
                                Stopwatch.GetTimestamp()).TotalMilliseconds;

                        var postMoveRenderStartedAt = Stopwatch.GetTimestamp();
                        foreach (var source in wpfSources)
                        {
                            source.Window.UpdateLayout();
                            source.Root.UpdateLayout();
                        }
                        dispatcher.Invoke(
                            DispatcherPriority.Render,
                            static () => { });
                        wpfPostMoveRenderMilliseconds =
                            Stopwatch.GetElapsedTime(
                                postMoveRenderStartedAt,
                                Stopwatch.GetTimestamp()).TotalMilliseconds;

                        var wpfAnimationStartedAt = Stopwatch.GetTimestamp();
                        var absoluteBeginTimestamp = Stopwatch.GetTimestamp();
                        const double wpfDurationSeconds = 0.016;
                        var wpfDelta = (float)LightweightWpfShift;
                        for (var index = 0;
                             index < visuals.Count;
                             index++)
                        {
                            var from =
                                (float)(sourceBounds[index].Top -
                                    offscreen.Top);
                            var to =
                                (float)(targetBounds[index].Top -
                                    offscreen.Top);
                            var sourceAnimation =
                                runtime.Device.CreateAnimation();
                            wpfAnimations.Add(sourceAnimation);
                            sourceAnimation.SetAbsoluteBeginTime(
                                absoluteBeginTimestamp).CheckError();
                            sourceAnimation.AddCubic(
                                0,
                                from,
                                (float)(3 * wpfDelta /
                                    wpfDurationSeconds),
                                (float)(-3 * wpfDelta /
                                    (wpfDurationSeconds *
                                     wpfDurationSeconds)),
                                (float)(wpfDelta /
                                    (wpfDurationSeconds *
                                     wpfDurationSeconds *
                                     wpfDurationSeconds))).CheckError();
                            sourceAnimation.End(
                                wpfDurationSeconds,
                                to).CheckError();
                            visuals[index]
                                .SetOffsetY(sourceAnimation)
                                .CheckError();
                        }
                        runtime.Device.Commit().CheckError();
                        wpfAnimationMilliseconds =
                            Stopwatch.GetElapsedTime(
                                wpfAnimationStartedAt,
                                Stopwatch.GetTimestamp()).TotalMilliseconds;
                        return true;
                    });
            wpfPublishMilliseconds = Stopwatch.GetElapsedTime(
                coordinatedStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (coordinatedResult !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The WPF-HWND coordinated prewarm publication failed.");
            }

            wpfWindowPosChanging =
                wpfSources.Sum(source => source.WindowPosChanging);
            wpfWindowPosChanged =
                wpfSources.Sum(source => source.WindowPosChanged);
            wpfMoveMessages =
                wpfSources.Sum(source => source.MoveMessages);

            var wpfUncloakStartedAt = Stopwatch.GetTimestamp();
            var wpfUncloakResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    wpfSources
                        .Select(source =>
                            new WindowNative.WindowCloakChange(
                                source.Handle,
                                Cloaked: false,
                                RollbackCloaked: true))
                        .ToArray());
            wpfUncloakMilliseconds = Stopwatch.GetElapsedTime(
                wpfUncloakStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            wpfHwndSucceeded =
                wpfUncloakResult ==
                    WindowNative.WindowCloakBatchResult.Success &&
                wpfWindowPosChanging >= LightweightWpfSourceCount &&
                wpfWindowPosChanged >= LightweightWpfSourceCount &&
                wpfMoveMessages >= LightweightWpfSourceCount;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule lightweight WPF-HWND prewarm failed. Exception={0}",
                ex);
        }
        finally
        {
            var cleanupStartedAt = Stopwatch.GetTimestamp();
            try
            {
                window?.Hide();
            }
            catch { }
            try
            {
                if (target != null)
                {
                    target.SetRoot(null!).CheckError();
                    runtime.Device.Commit().CheckError();
                }
            }
            catch { }
            for (var index = wpfAnimations.Count - 1;
                 index >= 0;
                 index--)
            {
                try { wpfAnimations[index].Dispose(); } catch { }
            }
            for (var index = visuals.Count - 1;
                 index >= 0;
                 index--)
            {
                try { visuals[index].Dispose(); } catch { }
            }
            for (var index = surfaces.Count - 1;
                 index >= 0;
                 index--)
            {
                try { surfaces[index].Dispose(); } catch { }
            }
            for (var index = wpfSources.Count - 1;
                 index >= 0;
                 index--)
            {
                try { wpfSources[index].Dispose(); } catch { }
            }
            try { animation?.Dispose(); } catch { }
            try { root?.Dispose(); } catch { }
            try { target?.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
            cleanupMilliseconds = Stopwatch.GetElapsedTime(
                cleanupStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            timings = new LightweightPrewarmProbeTimings(
                windowMilliseconds,
                targetMilliseconds,
                visualMilliseconds,
                animationMilliseconds,
                rootCommitMilliseconds,
                showFlushMilliseconds,
                cloakRoundTripMilliseconds,
                wpfSourceMilliseconds,
                wpfInitialRenderMilliseconds,
                wpfSurfaceMilliseconds,
                wpfEndpointBatchMilliseconds,
                wpfPostMoveRenderMilliseconds,
                wpfAnimationMilliseconds,
                wpfPublishMilliseconds,
                wpfUncloakMilliseconds,
                cleanupMilliseconds,
                Stopwatch.GetElapsedTime(
                    totalStartedAt,
                    Stopwatch.GetTimestamp()).TotalMilliseconds,
                cloakRoundTripSucceeded,
                wpfHwndSucceeded,
                wpfWindowPosChanging,
                wpfWindowPosChanged,
                wpfMoveMessages);
        }

        return publicationReady;
    }

#if DEBUG
    private static LightweightPrewarmProcessSnapshot
        CaptureLightweightPrewarmProcessSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new LightweightPrewarmProcessSnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.HandleCount);
    }

    private static double ToLightweightPrewarmMiB(long bytes) =>
        bytes / (1024.0 * 1024.0);
#endif
}
