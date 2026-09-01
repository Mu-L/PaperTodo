using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal readonly record struct InitialWindowProbeResult(
    bool LocalSuccess,
    int SuccessfulTarget,
    int ActualWindow,
    int Attempts);

internal sealed partial class MarkdownSemanticSnapshot
{
    internal static InitialWindowProbeResult ParseWithInitialWindowForTests(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        int initialTargetChars,
        out MarkdownSemanticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(oldSource);
        ArgumentNullException.ThrowIfNull(oldSnapshot);
        ArgumentNullException.ThrowIfNull(newSource);
        if (initialTargetChars <= 0 || initialTargetChars > IncrementalMaxWindowChars)
        {
            throw new ArgumentOutOfRangeException(nameof(initialTargetChars));
        }

        snapshot = null!;
        if (string.Equals(oldSource, newSource, StringComparison.Ordinal))
        {
            snapshot = oldSnapshot;
            return new(true, initialTargetChars, 0, 0);
        }

        FindContiguousDifference(
            oldSource,
            newSource,
            out var changedStart,
            out var oldChangedEnd,
            out var newChangedEnd);

        var changedOldLength = oldChangedEnd - changedStart;
        var changedNewLength = newChangedEnd - changedStart;
        if (changedOldLength + changedNewLength > IncrementalMaxChangedChars)
        {
            snapshot = Parse(newSource);
            return new(false, 0, newSource.Length, 0);
        }

        var oldLine = GetLineBounds(oldSource, changedStart);
        var newLine = GetLineBounds(newSource, changedStart);
        var hasGlobalReferenceDependency =
            IsPotentialReferenceDefinition(oldSource, oldLine.Start, oldLine.End) ||
            IsPotentialReferenceDefinition(newSource, newLine.Start, newLine.End) ||
            ChangeTouchesSquareBracket(oldSource, changedStart, oldChangedEnd) ||
            ChangeTouchesSquareBracket(newSource, changedStart, newChangedEnd) ||
            ReferenceStyleLinkOverlapsChange(
                oldSource,
                oldSnapshot._links,
                changedStart,
                oldChangedEnd);
        if (hasGlobalReferenceDependency)
        {
            snapshot = Parse(newSource);
            return new(false, 0, newSource.Length, 0);
        }

        var delta = newSource.Length - oldSource.Length;
        var attempts = 0;
        for (var targetChars = initialTargetChars;
             targetChars <= IncrementalMaxWindowChars;
             targetChars *= 2)
        {
            attempts++;
            if (!TryBuildIncrementalWindow(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    changedStart,
                    oldChangedEnd,
                    newChangedEnd,
                    delta,
                    targetChars,
                    out var oldStart,
                    out var oldEnd,
                    out var newStart,
                    out var newEnd))
            {
                snapshot = Parse(newSource);
                return new(false, 0, newSource.Length, attempts);
            }

            var actualWindow = Math.Max(oldEnd - oldStart, newEnd - newStart);
            var local = Parse(newSource[newStart..newEnd]);
            if (oldStart == 0 &&
                oldEnd == oldSource.Length &&
                newStart == 0 &&
                newEnd == newSource.Length)
            {
                snapshot = local;
                return new(true, targetChars, actualWindow, attempts);
            }

            var referenceStable = ReferenceLinksRemainStable(
                oldSource,
                oldSnapshot._links,
                local._links,
                oldStart,
                oldEnd,
                newStart,
                changedStart,
                oldChangedEnd,
                delta);
            var guardsStable = GuardRegionsMatch(
                oldSnapshot,
                local,
                oldStart,
                oldEnd,
                newStart,
                changedStart,
                oldChangedEnd,
                delta);
            if (!referenceStable || !guardsStable)
            {
                if (targetChars == IncrementalMaxWindowChars)
                {
                    snapshot = Parse(newSource);
                    return new(false, 0, newSource.Length, attempts);
                }
                continue;
            }

            var spans = SpliceSpans(
                oldSnapshot._spans,
                local._spans,
                oldStart,
                oldEnd,
                newStart,
                delta);
            var links = SpliceLinks(
                oldSnapshot._links,
                local._links,
                oldStart,
                oldEnd,
                newStart,
                delta);
            var lineStarts = BuildLineStarts(newSource);
            var lines = new MarkdownSemanticLine[lineStarts.Length];
            foreach (var span in spans)
            {
                ApplySpanToLines(newSource, lineStarts, lines, span);
            }

            snapshot = new MarkdownSemanticSnapshot(
                lines,
                spans,
                links,
                BuildSpanLineIndex(newSource, lineStarts, spans),
                BuildLinkLineIndex(newSource, lineStarts, links));
            return new(true, targetChars, actualWindow, attempts);
        }

        snapshot = Parse(newSource);
        return new(false, 0, newSource.Length, attempts);
    }
}

internal static class InitialWindowProbeChecks
{
    private static readonly int[] Targets = { 512, 1024, 2048, 4096 };

    [ModuleInitializer]
    internal static void Run()
    {
        var ordinary = BuildOrdinary98k();
        var dense = PerformanceProfileChecks.BuildLargeStressSource();

        Console.WriteLine("WINDOW-PROBE fixed edit latency (same process/runner)");
        foreach (var target in Targets)
        {
            ProfileSequence("ordinary98k", ordinary, FindProbe(ordinary, "editable"), target);
            ProfileSequence("dense98k", dense, FindProbe(dense, "item "), target);
        }

        Console.WriteLine("WINDOW-PROBE random distribution");
        ProbeRandomDistribution("ordinary98k", ordinary, 700, 0xA110, false);
        ProbeRandomDistribution("dense98k-text", dense, 500, 0xD351, false);
        ProbeRandomDistribution("dense98k-delimiter", dense, 350, 0xB17E, true);

        Console.WriteLine("WINDOW-PROBE full-parse equivalence");
        ProbeFullParseEquivalence("ordinary98k", ordinary, 120, 0xA111, false);
        ProbeFullParseEquivalence("dense98k-text", dense, 120, 0xD352, false);
        ProbeFullParseEquivalence("dense98k-delimiter", dense, 160, 0xB17F, true);
        ProbeStructuralEquivalence();
    }

    private static void ProfileSequence(string label, string initialSource, int probe, int initialTarget)
    {
        var source = initialSource;
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var inserted = false;

        for (var i = 0; i < 4; i++)
        {
            var nextSource = !inserted ? source.Insert(probe, "Z") : source.Remove(probe, 1);
            _ = MarkdownSemanticSnapshot.ParseWithInitialWindowForTests(
                source, snapshot, nextSource, initialTarget, out var nextSnapshot);
            source = nextSource;
            snapshot = nextSnapshot;
            inserted = !inserted;
        }

        const int iterations = 41;
        var elapsed = new double[iterations];
        var fallback = 0;
        var attempts = 0;
        var firstPass = 0;
        for (var i = 0; i < iterations; i++)
        {
            var nextSource = !inserted ? source.Insert(probe, "Z") : source.Remove(probe, 1);
            var start = Stopwatch.GetTimestamp();
            var result = MarkdownSemanticSnapshot.ParseWithInitialWindowForTests(
                source, snapshot, nextSource, initialTarget, out var nextSnapshot);
            elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!result.LocalSuccess)
            {
                fallback++;
            }
            if (result.Attempts == 1)
            {
                firstPass++;
            }
            attempts += result.Attempts;
            source = nextSource;
            snapshot = nextSnapshot;
            inserted = !inserted;
        }

        Array.Sort(elapsed);
        Console.WriteLine(
            $"WINDOW-PROBE {label} initial={initialTarget} " +
            $"p50={elapsed[elapsed.Length / 2]:F3}ms " +
            $"p95={elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1]:F3}ms " +
            $"first-pass={firstPass}/{iterations} fallback={fallback}/{iterations} " +
            $"avg-attempts={attempts / (double)iterations:F2}");
    }

    private static void ProbeRandomDistribution(
        string label,
        string source,
        int count,
        int seed,
        bool delimiterStress)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var random = new Random(seed);
        var positions = new int[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = PickTextPosition(source, random);
        }
        var delimiters = new[] { "*", "_", "`", "~", ">", "-", "#" };

        foreach (var initialTarget in Targets)
        {
            var fallback = 0;
            var successTargets = new Dictionary<int, int>();
            var attemptsTotal = 0;
            var actualWindows = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var token = delimiterStress ? delimiters[i % delimiters.Length] : ((i & 1) == 0 ? "x" : " ");
                var changed = source.Insert(positions[i], token);
                var result = MarkdownSemanticSnapshot.ParseWithInitialWindowForTests(
                    source, snapshot, changed, initialTarget, out var parsed);
                GC.KeepAlive(parsed);
                attemptsTotal += result.Attempts;
                if (!result.LocalSuccess)
                {
                    fallback++;
                }
                else
                {
                    successTargets[result.SuccessfulTarget] = successTargets.GetValueOrDefault(result.SuccessfulTarget) + 1;
                    if (result.ActualWindow > 0)
                    {
                        actualWindows.Add(result.ActualWindow);
                    }
                }
            }

            actualWindows.Sort();
            var p50Window = actualWindows.Count == 0 ? 0 : actualWindows[actualWindows.Count / 2];
            var p95Window = actualWindows.Count == 0 ? 0 : actualWindows[(int)Math.Ceiling(actualWindows.Count * 0.95) - 1];
            var buckets = string.Join(",", successTargets.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
            Console.WriteLine(
                $"WINDOW-DIST {label} initial={initialTarget} total={count} " +
                $"fallback={fallback} ({fallback * 100d / count:F2}%) " +
                $"avg-attempts={attemptsTotal / (double)count:F3} " +
                $"success-targets=[{buckets}] window-p50={p50Window} window-p95={p95Window}");
        }
    }

    private static void ProbeFullParseEquivalence(
        string label,
        string source,
        int count,
        int seed,
        bool delimiterStress)
    {
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(source);
        var random = new Random(seed);
        var delimiters = new[] { "*", "_", "`", "~", ">", "-", "#" };
        var mismatchCounts = Targets.ToDictionary(target => target, _ => 0);
        var localMismatchCounts = Targets.ToDictionary(target => target, _ => 0);
        var examples = new List<string>();

        for (var index = 0; index < count; index++)
        {
            var position = PickTextPosition(source, random);
            var token = delimiterStress ? delimiters[index % delimiters.Length] : ((index & 1) == 0 ? "x" : " ");
            var changed = source.Insert(position, token);
            var expected = MarkdownSemanticSnapshot.Parse(changed);

            foreach (var target in Targets)
            {
                var result = MarkdownSemanticSnapshot.ParseWithInitialWindowForTests(
                    source, oldSnapshot, changed, target, out var actual);
                if (SnapshotsEquivalent(expected, actual))
                {
                    continue;
                }

                mismatchCounts[target]++;
                if (result.LocalSuccess)
                {
                    localMismatchCounts[target]++;
                }
                if (examples.Count < 12)
                {
                    examples.Add(
                        $"{label} case={index} pos={position} token={EscapeToken(token)} initial={target} " +
                        $"local={result.LocalSuccess} successTarget={result.SuccessfulTarget} " +
                        $"window={result.ActualWindow} attempts={result.Attempts}");
                }
            }
        }

        foreach (var target in Targets)
        {
            Console.WriteLine(
                $"WINDOW-CORRECT {label} initial={target} total={count} " +
                $"mismatch={mismatchCounts[target]} local-mismatch={localMismatchCounts[target]}");
        }
        foreach (var example in examples)
        {
            Console.WriteLine($"WINDOW-MISMATCH {example}");
        }
    }

    private static void ProbeStructuralEquivalence()
    {
        var cases = new List<(string Label, string Source, Func<string, string> Mutate)>();
        foreach (var length in new[] { 3000, 5000, 7000, 9000, 12000, 18000 })
        {
            var fence = "```text\n" + new string('a', length) + "\n```\nend\n";
            cases.Add(($"fence-marker-{length}", fence, source => source.Remove(0, 1)));
            cases.Add(($"fence-content-{length}", fence,
                source => source.Insert(source.IndexOf('a') + (length / 2), "x")));
        }

        var quote = new StringBuilder();
        for (var i = 0; i < 700; i++)
        {
            quote.Append("> item ").Append(i).Append(" with **bold** content and `code`\n");
        }
        cases.Add(("long-quote", quote.ToString(), source => source.Insert(source.Length / 2, "x")));

        var list = new StringBuilder();
        for (var i = 0; i < 700; i++)
        {
            list.Append("- item ").Append(i).Append(" with **bold** content and `code`\n");
        }
        cases.Add(("long-list", list.ToString(), source => source.Insert(source.Length / 2, "x")));

        foreach (var testCase in cases)
        {
            var oldSnapshot = MarkdownSemanticSnapshot.Parse(testCase.Source);
            var changed = testCase.Mutate(testCase.Source);
            var expected = MarkdownSemanticSnapshot.Parse(changed);
            foreach (var target in Targets)
            {
                var result = MarkdownSemanticSnapshot.ParseWithInitialWindowForTests(
                    testCase.Source, oldSnapshot, changed, target, out var actual);
                var equal = SnapshotsEquivalent(expected, actual);
                Console.WriteLine(
                    $"WINDOW-STRUCT {testCase.Label} initial={target} exact={equal} " +
                    $"local={result.LocalSuccess} successTarget={result.SuccessfulTarget} " +
                    $"window={result.ActualWindow} attempts={result.Attempts}");
            }
        }
    }

    private static bool SnapshotsEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual)
    {
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            return false;
        }

        for (var line = 0; line < expected.LineCount; line++)
        {
            if (expected.GetLine(line) != actual.GetLine(line) ||
                !expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)) ||
                !expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
            {
                return false;
            }
        }
        return true;
    }

    private static string EscapeToken(string token) => token switch
    {
        " " => "<space>",
        "\t" => "<tab>",
        "\n" => "<newline>",
        _ => token
    };

    private static int FindProbe(string source, string needle)
    {
        var at = source.IndexOf(needle, source.Length / 2, StringComparison.Ordinal);
        if (at < 0)
        {
            at = source.IndexOf(needle, StringComparison.Ordinal);
        }
        if (at < 0)
        {
            throw new InvalidOperationException($"Probe token '{needle}' not found.");
        }
        return at + Math.Min(2, needle.Length);
    }

    private static int PickTextPosition(string source, Random random)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var position = random.Next(1, source.Length - 1);
            if (char.IsLetterOrDigit(source[position]) && source[position - 1] != '[' && source[position + 1] != ']')
            {
                return position;
            }
        }
        return source.Length / 2;
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
