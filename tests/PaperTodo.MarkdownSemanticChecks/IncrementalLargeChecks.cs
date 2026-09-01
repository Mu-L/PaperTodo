using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class IncrementalLargeChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLargeDeterministicFuzz();
        ProfileDenseMarkdownIncrementalEdit();
    }

    private static void CheckLargeDeterministicFuzz()
    {
        var builder = new StringBuilder(60_000);
        var index = 0;
        while (builder.Length < 55_000)
        {
            builder.Append("Paragraph ").Append(index)
                .Append(" ordinary words with neutral content for a large incremental differential test.\n\n");
            if ((index % 7) == 0)
            {
                builder.Append("## Heading ").Append(index).Append("\n\n");
            }
            if ((index % 9) == 0)
            {
                builder.Append("> quoted row ").Append(index).Append("\n> continuation row with text\n\n");
            }
            if ((index % 11) == 0)
            {
                builder.Append("- item one\n- item two with **bold** and `code`\n\n");
            }
            if ((index % 13) == 0)
            {
                builder.Append("Bare https://example.com/path/").Append(index).Append(" and ~~strike~~ nearby.\n\n");
            }
            if ((index % 29) == 0)
            {
                builder.Append("```text\nfenced content row\nsecond fenced row\n```\n\n");
            }
            index++;
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var random = new Random(0x4D444C);
        string[] localInsertions = ["x", " ", "\n", "*", "**", "_", "~~", "`", "> ", "- ", "## "];
        var accepted = 0;
        var fallback = 0;

        for (var step = 0; step < 60; step++)
        {
            string next;
            string operation;
            if ((step % 12) == 11)
            {
                var offset = random.Next(1, source.Length);
                next = source.Insert(offset, "[");
                operation = $"insert '[' at {offset}";
            }
            else if ((step % 6) == 5 && source.Length > 100)
            {
                var offset = random.Next(1, source.Length - 1);
                var removed = source[offset];
                next = source.Remove(offset, 1);
                operation = $"delete U+{(int)removed:X4} '{Printable(removed)}' at {offset}";
            }
            else
            {
                var insertion = localInsertions[random.Next(localInsertions.Length)];
                var offset = random.Next(0, source.Length + 1);
                next = source.Insert(offset, insertion);
                operation = $"insert '{Escape(insertion)}' at {offset}";
            }

            var expected = MarkdownSemanticSnapshot.Parse(next);
            if (MarkdownSemanticSnapshot.TryParseIncremental(
                    source,
                    snapshot,
                    next,
                    out var incremental,
                    out var info))
            {
                AssertEquivalent(
                    expected,
                    incremental,
                    $"large fuzz step {step}; {operation}; window old={info.OldStart}+{info.OldLength} new={info.NewStart}+{info.NewLength}");
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

        if (accepted < 40 || fallback < 1)
        {
            throw new InvalidOperationException(
                $"FAIL incremental large fuzz routing accepted={accepted} fallback={fallback}");
        }
        Console.WriteLine($"PASS incremental large fuzz accepted={accepted} fallback={fallback}");
    }

    private static void ProfileDenseMarkdownIncrementalEdit()
    {
        var oldSource = PerformanceProfileChecks.BuildLargeStressSource();
        var probe = oldSource.IndexOf("- [ ] item ", oldSource.Length / 2, StringComparison.Ordinal);
        if (probe < 0)
        {
            probe = oldSource.LastIndexOf("- [ ] item ", StringComparison.Ordinal);
        }
        var editAt = probe + "- [ ] item ".Length;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm,
                out var info))
        {
            throw new InvalidOperationException("FAIL dense incremental profile: fallback");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), warm, "dense profile exactness");

        const int iterations = 21;
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            if (!MarkdownSemanticSnapshot.TryParseIncremental(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    out var result,
                    out _))
            {
                throw new InvalidOperationException("FAIL dense incremental profile: fallback during timing");
            }
            stopwatch.Stop();
            GC.KeepAlive(result);
            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var p50 = samples[samples.Length / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        Console.WriteLine(
            $"PROFILE IncrementalDense98k window={info.NewLength} p50={p50:F3}ms p95={p95:F3}ms");
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        if (expected.LineCount != actual.LineCount)
        {
            throw new InvalidOperationException(
                $"FAIL incremental {name}: line count {actual.LineCount} != {expected.LineCount}");
        }
        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                throw new InvalidOperationException(
                    $"FAIL incremental {name}: line semantic mismatch at {line}; expected={expected.GetLine(line)} actual={actual.GetLine(line)}");
            }
        }
        if (!expected.Spans.SequenceEqual(actual.Spans))
        {
            throw new InvalidOperationException(
                $"FAIL incremental {name}: span snapshot mismatch; {FirstSpanMismatch(expected.Spans, actual.Spans)}");
        }
        if (!expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException(
                $"FAIL incremental {name}: link snapshot mismatch; {FirstLinkMismatch(expected.Links, actual.Links)}");
        }
    }

    private static string FirstSpanMismatch(
        IReadOnlyList<MarkdownSemanticSpan> expected,
        IReadOnlyList<MarkdownSemanticSpan> actual)
    {
        var count = Math.Min(expected.Count, actual.Count);
        for (var index = 0; index < count; index++)
        {
            if (!expected[index].Equals(actual[index]))
            {
                return $"index={index} expected={expected[index]} actual={actual[index]} counts={expected.Count}/{actual.Count}";
            }
        }
        return $"common prefix={count} counts={expected.Count}/{actual.Count}";
    }

    private static string FirstLinkMismatch(
        IReadOnlyList<MarkdownSemanticLink> expected,
        IReadOnlyList<MarkdownSemanticLink> actual)
    {
        var count = Math.Min(expected.Count, actual.Count);
        for (var index = 0; index < count; index++)
        {
            if (!expected[index].Equals(actual[index]))
            {
                return $"index={index} expected={expected[index]} actual={actual[index]} counts={expected.Count}/{actual.Count}";
            }
        }
        return $"common prefix={count} counts={expected.Count}/{actual.Count}";
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string Printable(char value) =>
        char.IsControl(value) ? $"\\u{(int)value:X4}" : value.ToString();
}
