using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class SegmentedBoundaryAllocationChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckSegmentBoundaryEdits();
        CheckCrLfSegmentBoundary();
        CheckDocumentEdges();
        CheckCrossSegmentDeletion();
        CheckLazyCacheAcrossPositiveAndNegativeRebases();
        ProfileAllocationsAndRetainedMemory();
    }

    private static void CheckSegmentBoundaryEdits()
    {
        var source = BuildStructuredLines(40, "\n", trailingNewline: true);
        var ninthLineStart = FindLineStart(source, 8, "\n");

        AssertSegmentedExact(
            "segment boundary line-9 insertion",
            source,
            source.Insert(ninthLineStart, "Z"));

        AssertSegmentedExact(
            "segment boundary newline removal",
            source,
            source.Remove(ninthLineStart - 1, 1));

        var eighthLineStart = FindLineStart(source, 7, "\n");
        var insertionPoint = source.IndexOf("bold", eighthLineStart, StringComparison.Ordinal) + 2;
        AssertSegmentedExact(
            "segment boundary newline insertion",
            source,
            source.Insert(insertionPoint, "\n"));

        Console.WriteLine("PASS segmented 8/9-line boundary edits");
    }

    private static void CheckCrLfSegmentBoundary()
    {
        var source = BuildStructuredLines(32, "\r\n", trailingNewline: true);
        var ninthLineStart = FindLineStart(source, 8, "\r\n");

        AssertSegmentedExact(
            "CRLF segment boundary removal",
            source,
            source.Remove(ninthLineStart - 2, 2));

        var editAt = source.IndexOf("bold", ninthLineStart, StringComparison.Ordinal) + 2;
        AssertSegmentedExact(
            "CRLF segment boundary insertion",
            source,
            source.Insert(editAt, "\r\n"));

        Console.WriteLine("PASS segmented CRLF boundary edits");
    }

    private static void CheckDocumentEdges()
    {
        var source = BuildStructuredLines(20, "\n", trailingNewline: true);
        AssertSegmentedExact("document-start edit", source, source.Insert(0, "Z"));
        AssertSegmentedExact("document-end edit", source, source + "tail");
        AssertSegmentedExact(
            "trailing-newline removal",
            source,
            source.Remove(source.Length - 1, 1));
        AssertSegmentedExact(
            "trailing-newline addition",
            source.TrimEnd('\n'),
            source.TrimEnd('\n') + "\n");

        AssertProductionExact("empty-to-text", string.Empty, "plain");
        AssertProductionExact("text-to-empty", "plain", string.Empty);

        Console.WriteLine("PASS segmented document edge routing");
    }

    private static void CheckCrossSegmentDeletion()
    {
        var source = BuildStructuredLines(48, "\n", trailingNewline: true);
        var deleteStart = FindLineStart(source, 6, "\n");
        var deleteEnd = FindLineStart(source, 25, "\n");
        var changedLength = deleteEnd - deleteStart;
        if (changedLength >= 2048)
        {
            throw new InvalidOperationException(
                $"FAIL cross-segment test fixture exceeded incremental edit limit: {changedLength}");
        }

        AssertSegmentedExact(
            "delete across multiple line-index segments",
            source,
            source.Remove(deleteStart, changedLength));

        Console.WriteLine($"PASS segmented multi-segment deletion chars={changedLength}");
    }

    private static void CheckLazyCacheAcrossPositiveAndNegativeRebases()
    {
        var source0 = BuildStructuredLines(120, "\n", trailingNewline: true);
        var snapshot0 = MarkdownSemanticSnapshot.Parse(source0);

        var edit0 = FindLineStart(source0, 2, "\n") + 4;
        var source1 = source0.Insert(edit0, "X");
        var snapshot1 = RequireSegmented(source0, snapshot0, source1, "lazy rebase first insert");
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(source1), snapshot1, "lazy rebase first insert");

        // Force a far suffix segment to materialize shifted span/link buckets.
        var farLine1 = snapshot1.LineCount - 3;
        var materialized1 = snapshot1.SpansForLine(farLine1).Length +
            snapshot1.LinksForLine(farLine1).Length;
        GC.KeepAlive(materialized1);

        var edit1 = FindLineStart(source1, 1, "\n") + 5;
        var source2 = source1.Insert(edit1, "Q");
        var snapshot2 = RequireSegmented(source1, snapshot1, source2, "lazy rebase second insert");
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(source2), snapshot2, "lazy rebase second insert");
        var materialized2 = snapshot2.SpansForLine(snapshot2.LineCount - 3).Length +
            snapshot2.LinksForLine(snapshot2.LineCount - 3).Length;
        GC.KeepAlive(materialized2);

        var source3 = source2.Remove(edit1, 1);
        var snapshot3 = RequireSegmented(source2, snapshot2, source3, "lazy rebase negative delta");
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(source3), snapshot3, "lazy rebase negative delta");
        var materialized3 = snapshot3.SpansForLine(snapshot3.LineCount - 3).Length +
            snapshot3.LinksForLine(snapshot3.LineCount - 3).Length;
        GC.KeepAlive(materialized3);

        Console.WriteLine("PASS segmented lazy cache survives positive/negative rebases");
    }

    private static void ProfileAllocationsAndRetainedMemory()
    {
        var ordinary = BuildOrdinary98k();
        var dense = PerformanceProfileChecks.BuildLargeStressSource();

        // Warm JIT/static paths before measuring allocations.
        GC.KeepAlive(MarkdownSemanticSnapshot.Parse("## warm **bold** https://example.com\n"));
        GC.KeepAlive(MarkdownSemanticSnapshot.Parse(ordinary));
        ForceGc();

        var ordinaryParseAlloc = MeasureAllocated(() => MarkdownSemanticSnapshot.Parse(ordinary));
        var denseParseAlloc = MeasureAllocated(() => MarkdownSemanticSnapshot.Parse(dense));

        var ordinaryOld = MarkdownSemanticSnapshot.Parse(ordinary);
        var ordinaryEditAt = ordinary.IndexOf("editable", ordinary.Length / 2, StringComparison.Ordinal) + 4;
        var ordinaryNew = ordinary.Insert(ordinaryEditAt, "Z");
        var ordinaryIncrementalAlloc = MeasureAllocated(() =>
        {
            if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                    ordinary,
                    ordinaryOld,
                    ordinaryNew,
                    out var result,
                    out _))
            {
                throw new InvalidOperationException("FAIL ordinary allocation profile fell back");
            }
            return result;
        });

        var denseOld = MarkdownSemanticSnapshot.Parse(dense);
        var denseProbe = dense.IndexOf("- [ ] item ", dense.Length / 2, StringComparison.Ordinal);
        var denseEditAt = denseProbe + "- [ ] item ".Length;
        var denseNew = dense.Insert(denseEditAt, "Z");
        var denseIncrementalAlloc = MeasureAllocated(() =>
        {
            if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                    dense,
                    denseOld,
                    denseNew,
                    out var result,
                    out _))
            {
                throw new InvalidOperationException("FAIL dense allocation profile fell back");
            }
            return result;
        });

        // Rebase near the start so almost all suffix segments carry a delta; first scan materializes
        // lazy buckets, second scan should largely reuse those cached arrays.
        var nearStart = dense.IndexOf("item", StringComparison.Ordinal) + 2;
        var denseShiftedSource = dense.Insert(nearStart, "Z");
        var denseShifted = RequireSegmented(dense, denseOld, denseShiftedSource, "lazy allocation profile");
        var firstQueryAlloc = MeasureAllocated(() => ConsumeAllLineQueries(denseShifted));
        var secondQueryAlloc = MeasureAllocated(() => ConsumeAllLineQueries(denseShifted));

        var retained = MeasureApproxRetainedSnapshot(dense);

        Console.WriteLine(
            $"PROFILE SegmentedAlloc ordinary-parse={ToKiB(ordinaryParseAlloc):F1}KiB " +
            $"dense-parse={ToKiB(denseParseAlloc):F1}KiB ordinary-incremental={ToKiB(ordinaryIncrementalAlloc):F1}KiB " +
            $"dense-incremental={ToKiB(denseIncrementalAlloc):F1}KiB");
        Console.WriteLine(
            $"PROFILE SegmentedLazyAlloc first-all-lines={ToKiB(firstQueryAlloc):F1}KiB " +
            $"second-all-lines={ToKiB(secondQueryAlloc):F1}KiB lines={denseShifted.LineCount}");
        Console.WriteLine(
            $"PROFILE SegmentedRetained dense-snapshot~={ToKiB(retained):F1}KiB");

        if (secondQueryAlloc > firstQueryAlloc + 64 * 1024)
        {
            throw new InvalidOperationException(
                $"FAIL lazy line-query cache did not stabilize: first={firstQueryAlloc} second={secondQueryAlloc}");
        }

        Console.WriteLine("PASS segmented allocation/retained-memory profile");
    }

    private static long MeasureAllocated(Func<object?> action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = action();
        var after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(result);
        return Math.Max(0, after - before);
    }

    private static long MeasureApproxRetainedSnapshot(string source)
    {
        ForceGc();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        ForceGc();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(snapshot);
        return Math.Max(0, after - before);
    }

    private static object ConsumeAllLineQueries(MarkdownSemanticSnapshot snapshot)
    {
        var total = 0;
        for (var line = 0; line < snapshot.LineCount; line++)
        {
            total += snapshot.SpansForLine(line).Length;
            total += snapshot.LinksForLine(line).Length;
        }
        return total;
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double ToKiB(long bytes) => bytes / 1024d;

    private static void AssertSegmentedExact(string name, string oldSource, string newSource)
    {
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var actual = RequireSegmented(oldSource, oldSnapshot, newSource, name);
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), actual, name);
    }

    private static MarkdownSemanticSnapshot RequireSegmented(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        string name)
    {
        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var actual,
                out _))
        {
            throw new InvalidOperationException($"FAIL {name}: unexpectedly fell back");
        }
        return actual;
    }

    private static void AssertProductionExact(string name, string oldSource, string newSource)
    {
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var actual = MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
            oldSource,
            oldSnapshot,
            newSource,
            out var incremental,
            out _)
            ? incremental
            : MarkdownSemanticSnapshot.Parse(newSource);
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), actual, name);
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        if (expected.LineCount != actual.LineCount)
        {
            throw new InvalidOperationException(
                $"FAIL {name}: line count {actual.LineCount} != {expected.LineCount}");
        }

        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                throw new InvalidOperationException($"FAIL {name}: line semantic mismatch at {line}");
            }
            if (!expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)))
            {
                throw new InvalidOperationException($"FAIL {name}: per-line span mismatch at {line}");
            }
            if (!expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
            {
                throw new InvalidOperationException($"FAIL {name}: per-line link mismatch at {line}");
            }
        }

        if (!expected.Spans.SequenceEqual(actual.Spans))
        {
            throw new InvalidOperationException($"FAIL {name}: span snapshot mismatch");
        }
        if (!expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException($"FAIL {name}: link snapshot mismatch");
        }
    }

    private static string BuildStructuredLines(int count, string newline, bool trailingNewline)
    {
        var builder = new StringBuilder(count * 64);
        for (var index = 0; index < count; index++)
        {
            builder.Append("## line ").Append(index)
                .Append(" **bold** https://example.com/").Append(index);
            if (trailingNewline || index + 1 < count)
            {
                builder.Append(newline);
            }
        }
        return builder.ToString();
    }

    private static string BuildOrdinary98k()
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
        return builder.ToString();
    }

    private static int FindLineStart(string source, int zeroBasedLine, string newline)
    {
        if (zeroBasedLine <= 0)
        {
            return 0;
        }

        var offset = 0;
        for (var line = 0; line < zeroBasedLine; line++)
        {
            var delimiter = source.IndexOf(newline, offset, StringComparison.Ordinal);
            if (delimiter < 0)
            {
                throw new InvalidOperationException($"Test fixture has no line {zeroBasedLine}");
            }
            offset = delimiter + newline.Length;
        }
        return offset;
    }
}
