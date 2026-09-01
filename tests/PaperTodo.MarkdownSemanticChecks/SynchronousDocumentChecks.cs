using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class SynchronousDocumentChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckPublicationIsSynchronous();
        CheckBatchedChangePublishesFinalSemantics();
        ProfileActualDocumentEdit("ordinary", BuildOrdinary98k());
        ProfileActualDocumentEdit("dense", PerformanceProfileChecks.BuildLargeStressSource());
    }

    private static void CheckPublicationIsSynchronous()
    {
        var document = new TextDocument("title\nplain");
        using var semantics = new MarkdownSemanticDocument(document);
        var publications = 0;
        semantics.SnapshotChanged += () => publications++;

        document.Insert(0, "# ");

        if (publications != 1 ||
            !semantics.TryGetCurrent(out var snapshot) ||
            snapshot.GetLine(0).HeadingLevel != 1)
        {
            throw new InvalidOperationException(
                "FAIL synchronous semantic publication was not current when TextDocument.Insert returned.");
        }

        Console.WriteLine("PASS synchronous semantic publication before document mutation returns");
    }

    private static void CheckBatchedChangePublishesFinalSemantics()
    {
        var document = new TextDocument("item");
        using var semantics = new MarkdownSemanticDocument(document);
        var publications = 0;
        semantics.SnapshotChanged += () => publications++;

        document.BeginUpdate();
        try
        {
            document.Insert(0, "- ");
            document.Insert(document.TextLength, " **bold**");
        }
        finally
        {
            document.EndUpdate();
        }

        if (publications != 1 || !semantics.TryGetCurrent(out var snapshot))
        {
            throw new InvalidOperationException(
                $"FAIL batched synchronous semantic publication count={publications}.");
        }

        var expected = MarkdownSemanticSnapshot.Parse(document.Text);
        if (!expected.Spans.SequenceEqual(snapshot.Spans) ||
            !expected.Links.SequenceEqual(snapshot.Links) ||
            expected.LineCount != snapshot.LineCount)
        {
            throw new InvalidOperationException(
                "FAIL batched synchronous semantics do not match a full parse.");
        }

        Console.WriteLine("PASS batched synchronous semantic publication matches final source");
    }

    private static void ProfileActualDocumentEdit(string label, string source)
    {
        var probe = label == "dense"
            ? source.IndexOf("item ", source.Length / 2, StringComparison.Ordinal)
            : source.IndexOf("editable", source.Length / 2, StringComparison.Ordinal);
        if (probe < 0)
        {
            throw new InvalidOperationException($"FAIL sync document {label} profile probe missing.");
        }

        var editAt = probe + 2;
        var document = new TextDocument(source);
        using var semantics = new MarkdownSemanticDocument(document);

        // Warm the exact TextDocument -> semantic publication path once in each direction.
        document.Insert(editAt, "Z");
        document.Remove(editAt, 1);

        const int iterations = 31;
        var elapsed = new double[iterations];
        var allocated = new long[iterations];
        var inserted = false;
        for (var index = 0; index < iterations; index++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            if (!inserted)
            {
                document.Insert(editAt, "Z");
                inserted = true;
            }
            else
            {
                document.Remove(editAt, 1);
                inserted = false;
            }
            elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[index] = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocationBefore);

            if (!semantics.TryGetCurrent(out var current))
            {
                throw new InvalidOperationException(
                    $"FAIL sync document {label} profile lost current semantics.");
            }
            GC.KeepAlive(current);
        }

        Array.Sort(elapsed);
        Array.Sort(allocated);
        Console.WriteLine(
            $"PROFILE SyncDocument98k-{label} p50={elapsed[elapsed.Length / 2]:F3}ms " +
            $"p95={elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1]:F3}ms " +
            $"alloc-p50={allocated[allocated.Length / 2] / 1024d:F1}KiB");
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
}
