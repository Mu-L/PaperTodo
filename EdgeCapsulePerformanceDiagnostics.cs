using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PaperTodo;

/// <summary>
/// Debug-only buffered timings for the complete edge-preview pipeline. UI-thread callers only
/// enqueue a line; a ThreadPool writer batches disk IO so diagnostics cannot become the stall they
/// are intended to measure.
/// </summary>
internal static class EdgeCapsulePerformanceDiagnostics
{
#if DEBUG
    private readonly record struct DiagnosticLine(
        string FileName,
        string Text);

    private const int MaximumQueuedLines = 12_000;
    private const int MaximumFlushBatch = 512;
    private static readonly ConcurrentQueue<DiagnosticLine> PendingLines = new();
    private static readonly Timer FlushTimer = new(
        static _ => FlushPendingLines(),
        null,
        Timeout.Infinite,
        Timeout.Infinite);
    private static int _pendingLineCount;
    private static int _flushScheduled;
#endif

    internal static long Timestamp()
    {
#if DEBUG
        return Stopwatch.GetTimestamp();
#else
        return 0;
#endif
    }

    internal static double ElapsedMilliseconds(long startTimestamp)
    {
#if DEBUG
        return Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
#else
        return 0;
#endif
    }

    internal static double ElapsedMilliseconds(
        long startTimestamp,
        long endTimestamp)
    {
#if DEBUG
        return Stopwatch.GetElapsedTime(startTimestamp, endTimestamp)
            .TotalMilliseconds;
#else
        return 0;
#endif
    }

    internal static string ShortId(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<none>";
        }

        return value[..Math.Min(6, value.Length)];
    }

    [Conditional("DEBUG")]
    internal static void Trace(string message)
    {
#if DEBUG
        try
        {
            Enqueue(
                "edge-preview-performance.log",
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"tick={Stopwatch.GetTimestamp()} " +
                $"thread={Environment.CurrentManagedThreadId} " +
                message);
        }
        catch
        {
        }
#endif
    }

    [Conditional("DEBUG")]
    internal static void TraceInteraction(string message)
    {
#if DEBUG
        try
        {
            Enqueue(
                "edge-preview-trace.log",
                $"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
        catch
        {
        }
#endif
    }

#if DEBUG
    private static void Enqueue(string fileName, string line)
    {
        if (Interlocked.Increment(ref _pendingLineCount) > MaximumQueuedLines)
        {
            Interlocked.Decrement(ref _pendingLineCount);
            return;
        }

        PendingLines.Enqueue(new DiagnosticLine(fileName, line));
        ScheduleFlush();
    }

    private static void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Let one short burst accumulate before touching disk. Animation and pointer work can
            // therefore enqueue dozens of detailed timings while the writer performs one append.
            FlushTimer.Change(50, Timeout.Infinite);
        }
        catch
        {
            Volatile.Write(ref _flushScheduled, 0);
        }
    }

    private static void FlushPendingLines()
    {
        try
        {
            var batch = new List<DiagnosticLine>(MaximumFlushBatch);
            while (batch.Count < MaximumFlushBatch &&
                   PendingLines.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _pendingLineCount);
                batch.Add(line);
            }

            if (batch.Count > 0)
            {
                foreach (var group in batch.GroupBy(
                             line => line.FileName,
                             StringComparer.Ordinal))
                {
                    var path = Path.Combine(
                        AppContext.BaseDirectory,
                        group.Key);
                    File.AppendAllLines(
                        path,
                        group.Select(line => line.Text),
                        Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Performance diagnostics must never affect preview availability or animation.
        }
        finally
        {
            Volatile.Write(ref _flushScheduled, 0);
            if (!PendingLines.IsEmpty)
            {
                ScheduleFlush();
            }
        }
    }
#endif
}
