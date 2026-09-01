using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class SegmentedIncrementalChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLocalExact(
            "segmented plain insertion",
            "alpha beta gamma\nsecond plain line\nthird line\n",
            source => source.Insert(source.IndexOf("beta", StringComparison.Ordinal) + 2, "Z"));
        CheckLocalExact(
            "segmented newline insertion",
            "plain first words\nplain second words\nplain third words\n",
            source => source.Insert(source.IndexOf("first", StringComparison.Ordinal) + 2, "\n"));
        CheckLongFence();
        CheckSuffixLazyRebaseQueries();
        CheckSequentialEdits();
        CheckGlobalReferenceFallback();
        ProfileDenseIncremental();
    }

    private static void CheckLongFence()
    {
        var oldSource = "before\n```text\n" + new string('x', 12_000) + "\n```\nafter\n";
        var editAt = oldSource.IndexOf(new string('x', 100), StringComparison.Ordinal) + 6_000;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var actual,
                out var info))
        {
            throw new InvalidOperationException("FAIL segmented long fence: unexpectedly fell back");
        }

        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), actual, "segmented long fence");
        if (info.NewLength < 12_000)
        {
            throw new InvalidOperationException(
                $"FAIL segmented long fence: fence was not fully expanded ({info.NewLength})");
        }
        Console.WriteLine($"PASS segmented long fence window={info.NewLength}");
    }

    private static void CheckSuffixLazyRebaseQueries()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 180; index++)
        {
            builder.Append("## Heading ").Append(index).Append('\n');
            builder.Append("line **bold** [inline](https://example.com/")
                .Append(index)
                .Append(") and `code`\n\n");
        }

        var oldSource = builder.ToString();
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var newSource = oldSource.Insert(oldSource.IndexOf("Heading 3", StringComparison.Ordinal), "Z");
        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var actual,
                out _))
        {
            throw new InvalidOperationException("FAIL segmented suffix query rebase: fallback");
        }

        var expected = MarkdownSemanticSnapshot.Parse(newSource);
        AssertEquivalent(expected, actual, "segmented suffix query rebase");

        // Query far behind the edit to force a rebased suffix segment to materialize its offsets.
        var probeLine = Math.Max(0, expected.LineCount - 12);
        AssertLineBucketsEquivalent(expected, actual, probeLine, "segmented suffix lazy query");
        Console.WriteLine("PASS segmented suffix lazy query");
    }

    private static void CheckSequentialEdits()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 120; index++)
        {
            builder.Append("Paragraph ").Append(index)
                .Append(" with ordinary words and neutral text.\n\n");
            if ((index % 7) == 0)
            {
                builder.Append("## Heading ").Append(index).Append("\n\n");
            }
            if ((index % 9) == 0)
            {
                builder.Append("> quote **bold** and https://example.com/")
                    .Append(index)
                    .Append("\n\n");
            }
            if ((index % 13) == 0)
            {
                builder.Append("- item one\n- item two with `code`\n\n");
            }
            if ((index % 19) == 0)
            {
                builder.Append("```text\nfenced content\nsecond row\n```\n\n");
            }
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var random = new Random(0x51E67);
        string[] insertions = ["x", " ", "\n", "*", "**", "_", "~~", "`", "> ", "- ", "## ", "```\n"];
        var accepted = 0;
        var fallback = 0;

        for (var step = 0; step < 80; step++)
        {
            string next;
            if ((step % 5) == 4 && source.Length > 20)
            {
                var removeAt = random.Next(1, source.Length - 1);
                next = source.Remove(removeAt, 1);
            }
            else
            {
                var insertion = insertions[random.Next(insertions.Length)];
                var insertAt = random.Next(0, source.Length + 1);
                next = source.Insert(insertAt, insertion);
            }

            var expected = MarkdownSemanticSnapshot.Parse(next);
            if (MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                    source,
                    snapshot,
                    next,
                    out var incremental,
                    out _))
            {
                AssertEquivalent(expected, incremental, $"segmented sequence {step}");
                snapshot = incremental;
                accepted++;
            }
            else
            {
                snapshot = expected;
                fallback++;
            }
            source = next;
        }

        if (accepted < 40)
        {
            throw new InvalidOperationException(
                $"FAIL segmented sequential acceptance unexpectedly low: {accepted}/80");
        }
        Console.WriteLine($"PASS segmented sequential edits accepted={accepted} fallback={fallback}");
    }

    private static void CheckGlobalReferenceFallback()
    {
        var oldSource = "[target][id]\n\n" + new string('p', 40_000) +
            "\n\n[id]: https://example.com\n";
        var newSource = oldSource.Insert(oldSource.IndexOf("example", StringComparison.Ordinal) + 3, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "FAIL segmented distant reference definition edit: local path was accepted");
        }
        Console.WriteLine("PASS segmented distant reference fallback");
    }

    private static void ProfileDenseIncremental()
    {
        var oldSource = PerformanceProfileChecks.BuildLargeStressSource();
        var probe = oldSource.IndexOf("- [ ] item ", oldSource.Length / 2, StringComparison.Ordinal);
        var editAt = probe + "- [ ] item ".Length;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm,
                out var warmInfo))
        {
            throw new InvalidOperationException("FAIL segmented dense 98k profile: fallback");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), warm, "segmented dense 98k exactness");

        const int iterations = 21;
        var samples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    out var result,
                    out _))
            {
                throw new InvalidOperationException("FAIL segmented dense 98k profile timing: fallback");
            }
            stopwatch.Stop();
            GC.KeepAlive(result);
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var p50 = samples[samples.Length / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        Console.WriteLine(
            $"PROFILE SegmentedDense98k window={warmInfo.NewLength} p50={p50:F3}ms p95={p95:F3}ms");
    }

    private static void CheckLocalExact(
        string name,
        string oldSource,
        Func<string, string> edit)
    {
        var newSource = edit(oldSource);
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseSegmentedIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var actual,
                out var info))
        {
            throw new InvalidOperationException($"FAIL {name}: unexpectedly fell back");
        }

        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), actual, name);
        Console.WriteLine($"PASS {name} window={info.NewLength}");
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
            AssertLineBucketsEquivalent(expected, actual, line, name);
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

    private static void AssertLineBucketsEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        int line,
        string name)
    {
        if (!expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)))
        {
            throw new InvalidOperationException($"FAIL {name}: per-line span mismatch at {line}");
        }
        if (!expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
        {
            throw new InvalidOperationException($"FAIL {name}: per-line link mismatch at {line}");
        }
    }
}
