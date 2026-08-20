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
    private sealed class ProductHostPrewarmState
    {
        public bool Attempted { get; set; }
        public bool Completed { get; set; }
    }

    private readonly record struct ProductHostPrewarmTimings(
        int HostCount,
        double DiscoveryMilliseconds,
        double RenderBarrierMilliseconds,
        double EndpointNoopMilliseconds,
        double SurfaceMilliseconds,
        double RootCommitMilliseconds,
        double ShowFlushMilliseconds,
        double AnimationMilliseconds,
        double FinalPublishMilliseconds,
        double UncloakMilliseconds,
        double CleanupMilliseconds,
        double TotalMilliseconds,
        bool EndpointNoopSucceeded,
        bool RootPublished,
        bool FinalPublicationSucceeded);

    private readonly record struct ProductHostPrewarmSource(
        Window Window,
        IntPtr Handle,
        DeviceScreenRect Bounds);

    private static readonly ConditionalWeakTable<
        Dispatcher,
        ProductHostPrewarmState> ProductHostPrewarmStates = new();

    internal static void PrewarmProductHostAssembly(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => PrewarmProductHostAssembly(dispatcher)));
            return;
        }

        var state = ProductHostPrewarmStates.GetValue(
            dispatcher,
            static _ => new ProductHostPrewarmState());
        if (state.Attempted)
        {
            return;
        }
        state.Attempted = true;

        var totalStartedAt = Stopwatch.GetTimestamp();
        var runtimeReady = false;
        var probeSucceeded = false;
        var probe = default(ProductHostPrewarmTimings);
#if DEBUG
        var processBefore = CaptureLightweightPrewarmProcessSnapshot();
        EdgeCapsulePerformanceDiagnostics.Trace(
            "prewarm.product-host phase=start");
#endif
        try
        {
            runtimeReady = TryGetRuntime(dispatcher, out var runtime);
            if (!runtimeReady)
            {
                return;
            }

            // Reuse the exact dispatcher-wide spare that the first real queue session will acquire.
            // The probe never assigns a queue identity and leaves the host hidden/available again.
            runtime.PrewarmOutputHost();
            probeSucceeded = TryRunProductHostAssemblyProbe(
                runtime,
                dispatcher,
                out probe);
            state.Completed = probeSucceeded;
        }
        finally
        {
#if DEBUG
            var processAfter = CaptureLightweightPrewarmProcessSnapshot();
            var totalMilliseconds = Stopwatch.GetElapsedTime(
                totalStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"prewarm.product-host phase=complete " +
                $"outcome={(state.Completed ? "success" : "failed")} " +
                $"runtimeReady={runtimeReady} hosts={probe.HostCount} " +
                $"discoveryMs={probe.DiscoveryMilliseconds:F3} " +
                $"renderBarrierMs={probe.RenderBarrierMilliseconds:F3} " +
                $"endpointNoopMs={probe.EndpointNoopMilliseconds:F3} " +
                $"endpointNoop={probe.EndpointNoopSucceeded} " +
                $"surfaceMs={probe.SurfaceMilliseconds:F3} " +
                $"rootCommitMs={probe.RootCommitMilliseconds:F3} " +
                $"showFlushMs={probe.ShowFlushMilliseconds:F3} " +
                $"animationMs={probe.AnimationMilliseconds:F3} " +
                $"finalPublishMs={probe.FinalPublishMilliseconds:F3} " +
                $"uncloakMs={probe.UncloakMilliseconds:F3} " +
                $"rootPublished={probe.RootPublished} " +
                $"finalPublication={probe.FinalPublicationSucceeded} " +
                $"cleanupMs={probe.CleanupMilliseconds:F3} " +
                $"probeTotalMs={probe.TotalMilliseconds:F3} " +
                $"totalMs={totalMilliseconds:F3} " +
                $"privateDeltaMiB={ToLightweightPrewarmMiB(processAfter.PrivateBytes - processBefore.PrivateBytes):F3} " +
                $"workingSetDeltaMiB={ToLightweightPrewarmMiB(processAfter.WorkingSetBytes - processBefore.WorkingSetBytes):F3} " +
                $"managedHeapDeltaMiB={ToLightweightPrewarmMiB(processAfter.ManagedHeapBytes - processBefore.ManagedHeapBytes):F3} " +
                $"handlesDelta={processAfter.HandleCount - processBefore.HandleCount}");
#endif
        }
    }

    private static bool TryRunProductHostAssemblyProbe(
        SharedRuntime runtime,
        Dispatcher dispatcher,
        out ProductHostPrewarmTimings timings)
    {
        timings = default;
        var totalStartedAt = Stopwatch.GetTimestamp();
        var discoveryMilliseconds = 0d;
        var renderBarrierMilliseconds = 0d;
        var endpointNoopMilliseconds = 0d;
        var surfaceMilliseconds = 0d;
        var rootCommitMilliseconds = 0d;
        var showFlushMilliseconds = 0d;
        var animationMilliseconds = 0d;
        var finalPublishMilliseconds = 0d;
        var uncloakMilliseconds = 0d;
        var cleanupMilliseconds = 0d;
        var endpointNoopSucceeded = false;
        var rootPublished = false;
        var finalPublicationSucceeded = false;
        var outputCloaked = false;

        QueueHost? spareHost = null;
        IDCompositionVisual2? root = null;
        var surfaces = new List<IUnknown>();
        var visuals = new List<IDCompositionVisual>();
        var animations = new List<IDCompositionAnimation>();
        var sources = Array.Empty<ProductHostPrewarmSource>();

        try
        {
            dispatcher.VerifyAccess();
            if (!runtime.IsUsable || runtime._spareHosts.Count == 0)
            {
                throw new InvalidOperationException(
                    "No warm queue output host is available for product-host prewarm.");
            }

            spareHost = runtime._spareHosts.Peek();
            if (!spareHost.IsAvailable)
            {
                throw new InvalidOperationException(
                    "The dispatcher-wide spare queue output host is not available.");
            }

            var discoveryStartedAt = Stopwatch.GetTimestamp();
            var application = Application.Current;
            if (application == null)
            {
                throw new InvalidOperationException(
                    "The WPF application is unavailable for product-host prewarm.");
            }

            var discovered = new List<ProductHostPrewarmSource>();
            foreach (Window candidate in application.Windows)
            {
                if (!LooksLikeLiveEdgeCapsuleHost(candidate, dispatcher))
                {
                    continue;
                }

                var handle = new WindowInteropHelper(candidate).Handle;
                if (handle == IntPtr.Zero ||
                    !WindowNative.TryGetWindowDeviceBounds(
                        candidate,
                        out var bounds) ||
                    bounds.IsEmpty)
                {
                    continue;
                }

                discovered.Add(new ProductHostPrewarmSource(
                    candidate,
                    handle,
                    bounds));
            }
            sources = discovered
                .GroupBy(source => source.Handle)
                .Select(group => group.First())
                .ToArray();
            discoveryMilliseconds = Stopwatch.GetElapsedTime(
                discoveryStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (sources.Length == 0)
            {
                throw new InvalidOperationException(
                    "No live edge-capsule WPF host HWNDs were found for prewarm.");
            }

            // Drain the already-visible real hosts through WPF Render once, but do not flush yet.
            // The following real-surface publication crosses the desktop boundary for the whole set.
            var renderStartedAt = Stopwatch.GetTimestamp();
            foreach (var source in sources)
            {
                source.Window.UpdateLayout();
                if (source.Window.Content is FrameworkElement content)
                {
                    content.UpdateLayout();
                }
            }
            dispatcher.Invoke(
                DispatcherPriority.Render,
                static () => { });
            renderBarrierMilliseconds = Stopwatch.GetElapsedTime(
                renderStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            // Exercise the exact real HWND inspection/batch plumbing with unchanged endpoints.
            // This intentionally produces no WM_WINDOWPOS* traffic and cannot move a user window.
            var endpointStartedAt = Stopwatch.GetTimestamp();
            endpointNoopSucceeded = true;
            using (dispatcher.DisableProcessing())
            {
                using var batch = WindowNative.BeginWindowDeviceBoundsBatch(
                    sources.Length);
                foreach (var source in sources)
                {
                    endpointNoopSucceeded &=
                        batch.TryDefer(source.Handle, source.Bounds);
                }
                endpointNoopSucceeded &= batch.Commit();
            }
            endpointNoopMilliseconds = Stopwatch.GetElapsedTime(
                endpointStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var surfaceStartedAt = Stopwatch.GetTimestamp();
            runtime.Device.CreateVisual(out root).CheckError();
            IDCompositionVisual? reference = null;
            for (var index = 0; index < sources.Length; index++)
            {
                runtime.Device.CreateSurfaceFromHwnd(
                    sources[index].Handle,
                    out var surface).CheckError();
                surfaces.Add(surface);
                runtime.Device.CreateVisual(
                    out IDCompositionVisual2 visual).CheckError();
                visuals.Add(visual);
                visual.SetContent(surface).CheckError();
                visual.SetBitmapInterpolationMode(
                    BitmapInterpolationMode.Linear).CheckError();
                visual.SetBorderMode(BorderMode.Soft).CheckError();
                visual.SetOffsetX(index * 2f).CheckError();
                visual.SetOffsetY(index * 2f).CheckError();
                root.AddVisual(
                    visual,
                    insertAbove: true,
                    reference!).CheckError();
                reference = visual;
            }
            surfaceMilliseconds = Stopwatch.GetElapsedTime(
                surfaceStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            // Install the real-host tree on the exact spare QueueHost target used by session 1.
            var rootCommitStartedAt = Stopwatch.GetTimestamp();
            spareHost.Target.SetRoot(root).CheckError();
            runtime.Device.Commit().CheckError();
            rootCommitMilliseconds = Stopwatch.GetElapsedTime(
                rootCommitStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var outputBounds =
                new DeviceScreenRect(-32000, -32000, -31872, -31872);
            var showFlushStartedAt = Stopwatch.GetTimestamp();
            if (!spareHost.Window.Show(outputBounds, topmost: true) ||
                !WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The product-host real-surface root could not be published.");
            }
            rootPublished = true;
            showFlushMilliseconds = Stopwatch.GetElapsedTime(
                showFlushStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            // Match cold session final publication: animation configuration + DComp Commit occurs
            // inside one coordinated cloak callback, followed by the batch's single DwmFlush.
            // Only the off-screen queue output is cloaked; real paper HWNDs remain untouched.
            var finalPublishStartedAt = Stopwatch.GetTimestamp();
            var publication = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        spareHost.Window.Handle,
                        Cloaked: true,
                        RollbackCloaked: false)
                },
                () =>
                {
                    var animationStartedAt = Stopwatch.GetTimestamp();
                    var absoluteBeginTimestamp = Stopwatch.GetTimestamp();
                    const double durationSeconds = 0.016;
                    const float delta = 1f;
                    for (var index = 0; index < visuals.Count; index++)
                    {
                        var from = index * 2f;
                        var to = from + delta;
                        var animation = runtime.Device.CreateAnimation();
                        animations.Add(animation);
                        animation.SetAbsoluteBeginTime(
                            absoluteBeginTimestamp).CheckError();
                        animation.AddCubic(
                            0,
                            from,
                            (float)(3 * delta / durationSeconds),
                            (float)(-3 * delta /
                                (durationSeconds * durationSeconds)),
                            (float)(delta /
                                (durationSeconds * durationSeconds *
                                 durationSeconds))).CheckError();
                        animation.End(durationSeconds, to).CheckError();
                        visuals[index].SetOffsetY(animation).CheckError();
                    }
                    runtime.Device.Commit().CheckError();
                    animationMilliseconds = Stopwatch.GetElapsedTime(
                        animationStartedAt,
                        Stopwatch.GetTimestamp()).TotalMilliseconds;
                    return true;
                });
            finalPublishMilliseconds = Stopwatch.GetElapsedTime(
                finalPublishStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            outputCloaked =
                publication == WindowNative.WindowCloakBatchResult.Success;
            if (!outputCloaked)
            {
                throw new InvalidOperationException(
                    "The product-host coordinated final publication failed.");
            }

            var uncloakStartedAt = Stopwatch.GetTimestamp();
            var uncloak = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        spareHost.Window.Handle,
                        Cloaked: false,
                        RollbackCloaked: true)
                });
            uncloakMilliseconds = Stopwatch.GetElapsedTime(
                uncloakStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            outputCloaked = false;
            finalPublicationSucceeded =
                uncloak == WindowNative.WindowCloakBatchResult.Success;
            if (!finalPublicationSucceeded)
            {
                throw new InvalidOperationException(
                    "The product-host prewarm output could not be uncloaked.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule product-host composition prewarm failed. Exception={0}",
                ex);
        }
        finally
        {
            var cleanupStartedAt = Stopwatch.GetTimestamp();
            if (spareHost != null)
            {
                if (outputCloaked)
                {
                    try
                    {
                        _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                            new[]
                            {
                                new WindowNative.WindowCloakChange(
                                    spareHost.Window.Handle,
                                    Cloaked: false,
                                    RollbackCloaked: true)
                            });
                    }
                    catch { }
                }

                try { spareHost.Window.Hide(); } catch { }
                try
                {
                    spareHost.Target.SetRoot(null!).CheckError();
                    runtime.Device.Commit().CheckError();
                    _ = WindowNative.TryFlushDesktopComposition();
                }
                catch { }
            }

            for (var index = animations.Count - 1; index >= 0; index--)
            {
                try { animations[index].Dispose(); } catch { }
            }
            for (var index = visuals.Count - 1; index >= 0; index--)
            {
                try { visuals[index].Dispose(); } catch { }
            }
            for (var index = surfaces.Count - 1; index >= 0; index--)
            {
                try { surfaces[index].Dispose(); } catch { }
            }
            try { root?.Dispose(); } catch { }

            cleanupMilliseconds = Stopwatch.GetElapsedTime(
                cleanupStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            timings = new ProductHostPrewarmTimings(
                sources.Length,
                discoveryMilliseconds,
                renderBarrierMilliseconds,
                endpointNoopMilliseconds,
                surfaceMilliseconds,
                rootCommitMilliseconds,
                showFlushMilliseconds,
                animationMilliseconds,
                finalPublishMilliseconds,
                uncloakMilliseconds,
                cleanupMilliseconds,
                Stopwatch.GetElapsedTime(
                    totalStartedAt,
                    Stopwatch.GetTimestamp()).TotalMilliseconds,
                endpointNoopSucceeded,
                rootPublished,
                finalPublicationSucceeded);
        }

        return
            sources.Length > 0 &&
            endpointNoopSucceeded &&
            rootPublished &&
            finalPublicationSucceeded;
    }

    private static bool LooksLikeLiveEdgeCapsuleHost(
        Window window,
        Dispatcher dispatcher)
    {
        if (window.GetType() != typeof(Window) ||
            !ReferenceEquals(window.Dispatcher, dispatcher) ||
            !window.IsVisible ||
            !window.IsLoaded ||
            window.ShowInTaskbar ||
            window.WindowStyle != WindowStyle.None ||
            !window.AllowsTransparency ||
            window.Content is not Grid root ||
            root.Children.Count != 1 ||
            root.Children[0] is not Grid visualSurface ||
            visualSurface.RenderTransform is not TranslateTransform ||
            visualSurface.Children.Count < 3)
        {
            return false;
        }

        // Current EdgeCapsuleHost shape: Chrome Border + Shell Grid + Outline Border.
        // Keep this probe deliberately narrow so unrelated raw WPF windows are never wrapped.
        return
            visualSurface.Children.OfType<Border>().Count() >= 2 &&
            visualSurface.Children.OfType<Grid>().Any();
    }
}
