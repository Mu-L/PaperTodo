using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private const int LightweightRealHwndSourceCount = 2;
    private const int LightweightRealHwndWidth = 32;
    private const int LightweightRealHwndHeight = 24;
    private const int LightweightRealHwndShift = 4;
    private const int PrewarmWsPopup = unchecked((int)0x80000000);
    private const int PrewarmWsVisible = 0x10000000;
    private const int PrewarmWsExToolWindow = 0x00000080;
    private const int PrewarmWsExNoActivate = 0x08000000;

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
        double RealHwndSourceMilliseconds,
        double RealHwndSurfaceMilliseconds,
        double RealHwndEndpointBatchMilliseconds,
        double RealHwndAnimationMilliseconds,
        double RealHwndPublishMilliseconds,
        double RealHwndUncloakMilliseconds,
        double CleanupMilliseconds,
        double TotalMilliseconds,
        bool CloakRoundTripSucceeded,
        bool RealHwndSucceeded);

#if DEBUG
    private readonly record struct LightweightPrewarmProcessSnapshot(
        long PrivateBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes,
        int HandleCount);
#endif

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
                out probe);
            state.Completed =
                publicationReady &&
                probe.CloakRoundTripSucceeded &&
                probe.RealHwndSucceeded;
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
                $"prewarm.light phase=real-hwnd-probe " +
                $"outcome={(probe.RealHwndSucceeded ? "success" : "failed")} " +
                $"sources={LightweightRealHwndSourceCount} " +
                $"sourceMs={probe.RealHwndSourceMilliseconds:F3} " +
                $"surfaceMs={probe.RealHwndSurfaceMilliseconds:F3} " +
                $"endpointBatchMs={probe.RealHwndEndpointBatchMilliseconds:F3} " +
                $"animationMs={probe.RealHwndAnimationMilliseconds:F3} " +
                $"publishMs={probe.RealHwndPublishMilliseconds:F3} " +
                $"uncloakMs={probe.RealHwndUncloakMilliseconds:F3}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"prewarm.light phase=complete " +
                $"outcome={(state.Completed ? "success" : "failed")} " +
                $"runtimeReady={runtimeReady} runtimeMs={runtimeMilliseconds:F3} " +
                $"spareMs={spareMilliseconds:F3} totalMs={totalMilliseconds:F3} " +
                $"privateDeltaMiB={ToLightweightPrewarmMiB(processAfter.PrivateBytes - processBefore.PrivateBytes):F3} " +
                $"workingSetDeltaMiB={ToLightweightPrewarmMiB(processAfter.WorkingSetBytes - processBefore.WorkingSetBytes):F3} " +
                $"managedHeapDeltaMiB={ToLightweightPrewarmMiB(processAfter.ManagedHeapBytes - processBefore.ManagedHeapBytes):F3} " +
                $"handlesDelta={processAfter.HandleCount - processBefore.HandleCount} " +
                "wpfPrewarmed=False");
#endif
        }
    }

    private static bool TryRunLightweightPublicationProbe(
        SharedRuntime runtime,
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
        var realHwndSourceMilliseconds = 0d;
        var realHwndSurfaceMilliseconds = 0d;
        var realHwndEndpointBatchMilliseconds = 0d;
        var realHwndAnimationMilliseconds = 0d;
        var realHwndPublishMilliseconds = 0d;
        var realHwndUncloakMilliseconds = 0d;
        var cleanupMilliseconds = 0d;
        var cloakRoundTripSucceeded = false;
        var realHwndSucceeded = false;
        var publicationReady = false;

        EdgeCapsuleQueueProxyWindow? window = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? root = null;
        IDCompositionAnimation? animation = null;
        var sourceHandles = new List<IntPtr>(
            LightweightRealHwndSourceCount);
        var sourceBounds = new List<DeviceScreenRect>(
            LightweightRealHwndSourceCount);
        var targetBounds = new List<DeviceScreenRect>(
            LightweightRealHwndSourceCount);
        var surfaces = new List<IUnknown>(
            LightweightRealHwndSourceCount);
        var visuals = new List<IDCompositionVisual>(
            LightweightRealHwndSourceCount);
        var realHwndAnimations = new List<IDCompositionAnimation>(
            LightweightRealHwndSourceCount);

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
            runtime.Device.CreateVisual(
                out root).CheckError();
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
            animation.End(
                animationDurationSeconds,
                delta).CheckError();
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

            // The cheap publication probe above warms DComp/DWM API entry points. The remaining
            // cold cost in real queue startup is different: wrapping redirected HWND surfaces,
            // committing a multi-HWND endpoint batch inside the coordinated cloak callback, then
            // publishing live-surface animations. Exercise that exact shape with two tiny ordinary
            // redirected HWNDs entirely outside the virtual desktop. WPF itself stays untouched.
            var sourceStartedAt = Stopwatch.GetTimestamp();
            for (var index = 0;
                 index < LightweightRealHwndSourceCount;
                 index++)
            {
                var left = offscreen.Left + 8 + index * 40;
                var top = offscreen.Top + 12;
                var bounds = new DeviceScreenRect(
                    left,
                    top,
                    left + LightweightRealHwndWidth,
                    top + LightweightRealHwndHeight);
                var moved = new DeviceScreenRect(
                    bounds.Left,
                    bounds.Top + LightweightRealHwndShift,
                    bounds.Right,
                    bounds.Bottom + LightweightRealHwndShift);
                var handle = CreateLightweightPrewarmSourceWindow(bounds);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "A redirected source HWND for prewarm could not be created.");
                }
                sourceHandles.Add(handle);
                sourceBounds.Add(bounds);
                targetBounds.Add(moved);
            }
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The redirected prewarm source HWNDs could not be published.");
            }
            realHwndSourceMilliseconds = Stopwatch.GetElapsedTime(
                sourceStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var surfaceStartedAt = Stopwatch.GetTimestamp();
            IDCompositionVisual? reference = null;
            for (var index = 0;
                 index < sourceHandles.Count;
                 index++)
            {
                runtime.Device.CreateSurfaceFromHwnd(
                    sourceHandles[index],
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
                    "The redirected source visual cover could not be published.");
            }
            realHwndSurfaceMilliseconds = Stopwatch.GetElapsedTime(
                surfaceStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;

            var coordinatedStartedAt = Stopwatch.GetTimestamp();
            var coordinatedResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    sourceHandles
                        .Select(handle =>
                            new WindowNative.WindowCloakChange(
                                handle,
                                Cloaked: true,
                                RollbackCloaked: false))
                        .ToArray(),
                    () =>
                    {
                        var endpointStartedAt = Stopwatch.GetTimestamp();
                        using (var batch =
                               WindowNative.BeginWindowDeviceBoundsBatch(
                                   sourceHandles.Count))
                        {
                            for (var index = 0;
                                 index < sourceHandles.Count;
                                 index++)
                            {
                                if (!batch.TryDefer(
                                        sourceHandles[index],
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
                        realHwndEndpointBatchMilliseconds =
                            Stopwatch.GetElapsedTime(
                                endpointStartedAt,
                                Stopwatch.GetTimestamp()).TotalMilliseconds;

                        var realAnimationStartedAt =
                            Stopwatch.GetTimestamp();
                        var absoluteBeginTimestamp =
                            Stopwatch.GetTimestamp();
                        const double realDurationSeconds = 0.016;
                        var realDelta =
                            (float)LightweightRealHwndShift;
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
                            realHwndAnimations.Add(sourceAnimation);
                            sourceAnimation.SetAbsoluteBeginTime(
                                absoluteBeginTimestamp).CheckError();
                            sourceAnimation.AddCubic(
                                0,
                                from,
                                (float)(3 * realDelta /
                                    realDurationSeconds),
                                (float)(-3 * realDelta /
                                    (realDurationSeconds *
                                     realDurationSeconds)),
                                (float)(realDelta /
                                    (realDurationSeconds *
                                     realDurationSeconds *
                                     realDurationSeconds))).CheckError();
                            sourceAnimation.End(
                                realDurationSeconds,
                                to).CheckError();
                            visuals[index]
                                .SetOffsetY(sourceAnimation)
                                .CheckError();
                        }
                        runtime.Device.Commit().CheckError();
                        realHwndAnimationMilliseconds =
                            Stopwatch.GetElapsedTime(
                                realAnimationStartedAt,
                                Stopwatch.GetTimestamp()).TotalMilliseconds;
                        return true;
                    });
            realHwndPublishMilliseconds = Stopwatch.GetElapsedTime(
                coordinatedStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (coordinatedResult !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The real-HWND coordinated prewarm publication failed.");
            }

            var realUncloakStartedAt = Stopwatch.GetTimestamp();
            var realUncloakResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    sourceHandles
                        .Select(handle =>
                            new WindowNative.WindowCloakChange(
                                handle,
                                Cloaked: false,
                                RollbackCloaked: true))
                        .ToArray());
            realHwndUncloakMilliseconds = Stopwatch.GetElapsedTime(
                realUncloakStartedAt,
                Stopwatch.GetTimestamp()).TotalMilliseconds;
            realHwndSucceeded =
                realUncloakResult ==
                WindowNative.WindowCloakBatchResult.Success;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule lightweight composition prewarm failed. Exception={0}",
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
            for (var index = realHwndAnimations.Count - 1;
                 index >= 0;
                 index--)
            {
                try { realHwndAnimations[index].Dispose(); } catch { }
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
            for (var index = sourceHandles.Count - 1;
                 index >= 0;
                 index--)
            {
                try
                {
                    _ = DestroyWindowForLightweightPrewarm(
                        sourceHandles[index]);
                }
                catch { }
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
                realHwndSourceMilliseconds,
                realHwndSurfaceMilliseconds,
                realHwndEndpointBatchMilliseconds,
                realHwndAnimationMilliseconds,
                realHwndPublishMilliseconds,
                realHwndUncloakMilliseconds,
                cleanupMilliseconds,
                Stopwatch.GetElapsedTime(
                    totalStartedAt,
                    Stopwatch.GetTimestamp()).TotalMilliseconds,
                cloakRoundTripSucceeded,
                realHwndSucceeded);
        }

        return publicationReady;
    }

    private static IntPtr CreateLightweightPrewarmSourceWindow(
        DeviceScreenRect bounds) =>
        CreateWindowExForLightweightPrewarm(
            PrewarmWsExToolWindow | PrewarmWsExNoActivate,
            "Static",
            string.Empty,
            PrewarmWsPopup | PrewarmWsVisible,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

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

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateWindowExForLightweightPrewarm(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport(
        "user32.dll",
        EntryPoint = "DestroyWindow",
        SetLastError = true)]
    private static extern bool DestroyWindowForLightweightPrewarm(
        IntPtr handle);
}
