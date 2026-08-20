using System.Diagnostics;

namespace PaperTodo;

/// <summary>
/// Narrow Debug-only tracing window for one collapse-all master toggle. The normal edge diagnostics
/// are intentionally broad; this helper tags every retraction-related line with one batch id so a
/// single click can be reconstructed across PaperWindow, visual-transaction, transition-policy and
/// shared-scheduler logs without changing production behavior.
/// </summary>
internal static class EdgeCapsuleRetractionDiagnostics
{
#if DEBUG
    private const int TraceWindowMilliseconds = 2_000;
    private static long _nextBatchId;
    private static long _activeBatchId;
    private static long _activeUntilTimestamp;

    internal static long BeginMasterToggle(
        string monitorDeviceName,
        EdgeCapsuleEdge edge)
    {
        var batchId = Interlocked.Increment(ref _nextBatchId);
        var now = Stopwatch.GetTimestamp();
        var durationTicks = Math.Max(
            1,
            (long)Math.Ceiling(
                Stopwatch.Frequency * TraceWindowMilliseconds / 1000.0));
        Volatile.Write(ref _activeBatchId, batchId);
        Volatile.Write(ref _activeUntilTimestamp, now + durationTicks);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"collapse.trace batch={batchId} phase=batch-begin " +
            $"monitor={monitorDeviceName} edge={edge} windowMs={TraceWindowMilliseconds}");
        return batchId;
    }

    internal static bool IsActive
    {
        get
        {
            var until = Volatile.Read(ref _activeUntilTimestamp);
            return until > 0 && Stopwatch.GetTimestamp() <= until;
        }
    }

    internal static long BatchId =>
        IsActive ? Volatile.Read(ref _activeBatchId) : 0;

    internal static void Trace(string phase, string details)
    {
        var batchId = BatchId;
        if (batchId <= 0)
        {
            return;
        }
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"collapse.trace batch={batchId} phase={phase} {details}");
    }

    internal static void TraceMotionFactory(
        EdgeCapsuleMotion motion,
        string caller)
    {
        Trace(
            "motion-created",
            $"caller={caller} kind={motion.Kind} reason={motion.Reason} " +
            $"durationMs={motion.DurationMilliseconds}");
    }
#else
    internal static long BeginMasterToggle(
        string monitorDeviceName,
        EdgeCapsuleEdge edge) => 0;

    internal static bool IsActive => false;
    internal static long BatchId => 0;
    internal static void Trace(string phase, string details) { }
    internal static void TraceMotionFactory(
        EdgeCapsuleMotion motion,
        string caller) { }
#endif
}
