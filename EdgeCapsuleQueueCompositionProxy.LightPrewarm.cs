using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
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
        double CleanupMilliseconds,
        double TotalMilliseconds,
        bool CloakRoundTripSucceeded);

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
            state.Completed = publicationReady;
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
        var cleanupMilliseconds = 0d;
        var cloakRoundTripSucceeded = false;
        var publicationReady = false;

        EdgeCapsuleQueueProxyWindow? window = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? root = null;
        IDCompositionAnimation? animation = null;

        try
        {
            var offscreen =
                new DeviceScreenRect(-32000, -32000, -31996, -31996);

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
                cleanupMilliseconds,
                Stopwatch.GetElapsedTime(
                    totalStartedAt,
                    Stopwatch.GetTimestamp()).TotalMilliseconds,
                cloakRoundTripSucceeded);
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
