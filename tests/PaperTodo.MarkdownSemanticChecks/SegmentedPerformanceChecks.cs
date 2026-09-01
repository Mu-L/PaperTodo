using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class SegmentedPerformanceChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        ProfileOrdinaryIncremental();
        ProfileLineQueries();
    }

    private static void ProfileOrdinaryIncremental()
    {
        var builder = new StringBuilder(100_000);
        var index = 0;
        while (builder.Length < 98_000)
        {
            builder.Append("Plain paragraph ").Append(index)
                .Append(" contains ordinary editable words and enough neutral content for profiling.\n\n");
            if ((index++ % 17) == 0)
            {
                builder.Append("A **bold** token and https://example.com/path nearby.\n\n");
            }
        }

        var oldSource = builder.ToString();
        var editAt = oldSource.IndexOf("editable", oldSource.Length / 2, StringComparison.Ordinal) + 4;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm,
                out var warmInfo))
        {
            throw new InvalidOperationException("FAIL segmented ordinary 98k profile: fallback");
        }
        GC.KeepAlive(warm);

        const int iterations = 31;
        var samples = new double[iterations];
        for (var indexSample = 0; indexSample < iterations; indexSample++)
        {
            var stopwatch = Stopwatch.StartNew();
            if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    out var result,
                    out _))
            {
                throw new InvalidOperationException("FAIL segmented ordinary 98k profile timing: fallback");
            }
            stopwatch.Stop();
            GC.KeepAlive(result);
            samples[indexSample] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine(
            $"PROFILE SegmentedOrdinary98k window={warmInfo.NewLength} p50={samples[samples.Length / 2]:F3}ms " +
            $"p95={samples[(int)Math.Ceiling(samples.Length * 0.95) - 1]:F3}ms");
    }

    private static void ProfileLineQueries()
    {
        var source = PerformanceProfileChecks.BuildLargeStressSource();
        var segmented = MarkdownSemanticSnapshot.Parse(source);
        var flat = MarkdownSemanticSnapshot.ProfileParseForTests(source).Snapshot;

        ConsumeAllLineQueries(segmented);
        ConsumeAllLineQueries(flat);

        const int iterations = 31;
        var segmentedSamples = new double[iterations];
        var flatSamples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            ConsumeAllLineQueries(segmented);
            stopwatch.Stop();
            segmentedSamples[index] = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            ConsumeAllLineQueries(flat);
            stopwatch.Stop();
            flatSamples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(segmentedSamples);
        Array.Sort(flatSamples);
        Console.WriteLine(
            $"PROFILE LineQueries98k segmented-p50={segmentedSamples[segmentedSamples.Length / 2]:F3}ms " +
            $"flat-p50={flatSamples[flatSamples.Length / 2]:F3}ms lines={segmented.LineCount}");
    }

    private static void ConsumeAllLineQueries(MarkdownSemanticSnapshot snapshot)
    {
        var total = 0;
        for (var line = 0; line < snapshot.LineCount; line++)
        {
            total += snapshot.GetLine(line).HeadingLevel;
            total += snapshot.SpansForLine(line).Length;
            total += snapshot.LinksForLine(line).Length;
        }
        GC.KeepAlive(total);
    }
}
