using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class IncrementalSnapshotChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLocalExact(
            "plain insertion",
            "alpha beta gamma\nsecond plain line\nthird line",
            source => source.Insert(source.IndexOf("beta", StringComparison.Ordinal) + 2, "Z"));
        CheckLocalExact(
            "plain deletion",
            "alpha beta gamma\nsecond plain line\nthird line",
            source => source.Remove(source.IndexOf("beta", StringComparison.Ordinal) + 1, 1));
        CheckLocalExact(
            "plain replacement",
            "alpha beta gamma\nsecond plain line\nthird line",
            source => source.Replace("beta", "beto", StringComparison.Ordinal));
        CheckLocalExact(
            "ordinary middle space",
            "alpha betagamma delta\nsecond plain line",
            source => source.Insert(source.IndexOf("gamma", StringComparison.Ordinal), " "));
        CheckLocalExact(
            "CJK content edit",
            "第一行普通文字\n第二行继续记录今天的事情\n第三行普通文字",
            source => source.Insert(source.IndexOf("今天", StringComparison.Ordinal) + 1, "明"));
        CheckLocalExact(
            "CRLF content edit",
            "first plain\r\nsecond ordinary line\r\nthird plain\r\n",
            source => source.Insert(source.IndexOf("ordinary", StringComparison.Ordinal) + 3, "X"));
        CheckLocalExact(
            "newline insertion",
            "plain first words\nplain second words\nplain third words",
            source => source.Insert(source.IndexOf("first", StringComparison.Ordinal) + 2, "\n"));
        CheckLocalExact(
            "Markdown delimiter insertion",
            "plain alpha beta gamma\nnext ordinary line",
            source => source.Insert(source.IndexOf("beta", StringComparison.Ordinal), "**"));
        CheckLocalExact(
            "inline delimiter content edit",
            "before **bold words** after\nnext ordinary line",
            source => source.Insert(source.IndexOf("bold", StringComparison.Ordinal) + 2, "Z"));
        CheckLocalExact(
            "list marker structure edit",
            "before\nplain item one\nplain item two\nafter",
            source => source.Insert(source.IndexOf("plain item one", StringComparison.Ordinal), "- "));
        CheckLocalExact(
            "quote marker structure edit",
            "before\nplain quoted row\nlazy continuation\nafter",
            source => source.Insert(source.IndexOf("plain quoted row", StringComparison.Ordinal), "> "));

        CheckLargeInsertionWithin16K();
        CheckLongFenceContentEdit();
        CheckFenceBoundaryEdit();
        CheckMediumFenceBoundaryUses16KRetry();
        CheckNewLongFenceFallsBackAfter16K();
        CheckContainerContentEdit();
        CheckReferenceCases();
        CheckReferenceDefinitionStateSurvivesIncremental();
        CheckLongPlainParagraphDelimiter();
        CheckSafeEditSequence();
        CheckDeterministicEditFuzz();
        ProfileLargeIncrementalEdit();
    }

    private static void CheckLargeInsertionWithin16K()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 350; index++)
        {
            builder.Append("prefix row ").Append(index).Append(" ordinary words.\n\n");
        }
        builder.Append("target ordinary row\n\n");
        for (var index = 0; index < 350; index++)
        {
            builder.Append("suffix row ").Append(index).Append(" ordinary words.\n\n");
        }

        var oldSource = builder.ToString();
        var insertAt = oldSource.IndexOf("ordinary row", StringComparison.Ordinal) + 4;
        var newSource = oldSource.Insert(insertAt, new string('x', 3_000));
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException(
                "FAIL incremental 3K insertion: bounded edit unexpectedly fell back");
        }

        AssertEquivalent(
            MarkdownSemanticSnapshot.Parse(newSource),
            incremental,
            "3K insertion within 16K");
        if (info.ChangedNewLength < 3_000 || info.NewLength > 16_384)
        {
            throw new InvalidOperationException(
                $"FAIL incremental 3K insertion: changed={info.ChangedNewLength} window={info.NewLength}");
        }
        Console.WriteLine(
            $"PASS incremental 3K insertion changed={info.ChangedNewLength} window={info.NewLength}");
    }

    private static void CheckLongFenceContentEdit()
    {
        var oldSource = "before\n```text\n" + new string('x', 12_000) + "\n```\nafter\n";
        var offset = oldSource.IndexOf(new string('x', 100), StringComparison.Ordinal) + 6_000;
        var newSource = oldSource.Insert(offset, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException("FAIL incremental long fence content edit: unexpectedly fell back");
        }
        AssertEquivalent(
            MarkdownSemanticSnapshot.Parse(newSource),
            incremental,
            "long fence content edit");
        if (info.NewLength < 12_000)
        {
            throw new InvalidOperationException(
                $"FAIL incremental long fence content edit: fence was not fully expanded ({info.NewLength})");
        }
        Console.WriteLine($"PASS incremental long fence content edit window={info.NewLength}");
    }

    private static void CheckFenceBoundaryEdit()
    {
        const string oldSource = "before\n```text\ncode\n```\nafter\n";
        var newSource = oldSource.Insert(oldSource.IndexOf("```text", StringComparison.Ordinal) + 1, "`");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException("FAIL incremental fence boundary edit: unexpectedly fell back");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "fence boundary edit");
        Console.WriteLine($"PASS incremental fence boundary edit window={info.NewLength}");
    }

    private static void CheckMediumFenceBoundaryUses16KRetry()
    {
        var oldSource = "```text\n" + new string('a', 5_000) + "\n```\nafter\n";
        var newSource = oldSource.Remove(0, 1);
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException("FAIL incremental medium fence boundary: 16K retry fell back");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "medium fence boundary");
        Console.WriteLine($"PASS incremental medium fence boundary via 16K retry window={info.NewLength}");
    }

    private static void CheckNewLongFenceFallsBackAfter16K()
    {
        var body = new StringBuilder();
        body.Append("before\n");
        body.Append("start body\n");
        for (var index = 0; index < 260; index++)
        {
            body.Append("ordinary body row ").Append(index).Append(" with neutral text\n");
        }
        body.Append("```\nafter\n");
        var oldSource = body.ToString();
        var insertAt = oldSource.IndexOf("start body", StringComparison.Ordinal);
        var newSource = oldSource.Insert(insertAt, "```text\n");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        if (MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "FAIL incremental new long fence: 16K tier unexpectedly accepted a guard-unstable edit");
        }

        // The document owner synchronously performs the third tier (full Markdig parse) when the
        // two local attempts decline the edit. The parser-level contract here is to reject rather
        // than publish a locally plausible but unproven snapshot.
        var full = MarkdownSemanticSnapshot.Parse(newSource);
        if (full.LineCount == 0)
        {
            throw new InvalidOperationException("FAIL incremental new long fence: full fallback produced no lines");
        }
        Console.WriteLine("PASS incremental new long fence falls back after 16K");
    }

    private static void CheckContainerContentEdit()
    {
        var builder = new StringBuilder();
        builder.Append("> quoted preface\n");
        for (var index = 0; index < 120; index++)
        {
            builder.Append("> - item ").Append(index).Append(" ordinary content\n");
        }
        builder.Append("after\n");
        var oldSource = builder.ToString();
        var editAt = oldSource.IndexOf("ordinary content", oldSource.Length / 3, StringComparison.Ordinal) + 5;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException("FAIL incremental quote/list content edit: unexpectedly fell back");
        }
        AssertEquivalent(
            MarkdownSemanticSnapshot.Parse(newSource),
            incremental,
            "quote/list content edit");
        Console.WriteLine($"PASS incremental quote/list content edit window={info.NewLength}");
    }

    private static void CheckReferenceCases()
    {
        const string smallReference = "[target][id]\n\nplain nearby text\n\n[id]: https://example.com\n";
        CheckLocalExact(
            "reference nearby ordinary edit",
            smallReference,
            source => source.Insert(source.IndexOf("nearby", StringComparison.Ordinal) + 2, "Z"));
        CheckFallback(
            "small reference definition edit",
            smallReference,
            source => source.Insert(source.IndexOf("example", StringComparison.Ordinal) + 3, "Z"));
        CheckFallback(
            "small reference use source edit",
            smallReference,
            source => source.Insert(source.IndexOf("target", StringComparison.Ordinal) + 2, "Z"));
        CheckLocalExact(
            "small new square bracket without definitions",
            "plain alpha beta gamma",
            source => source.Insert(source.IndexOf("beta", StringComparison.Ordinal), "["));

        var largeReference = "[target][id]\n\n" + new string('p', 40_000) + "\n\n[id]: https://example.com\n";
        CheckFallback(
            "distant reference definition edit",
            largeReference,
            source => source.Insert(source.IndexOf("example", StringComparison.Ordinal) + 3, "Z"));
        CheckFallback(
            "distant reference use source edit",
            largeReference,
            source => source.Insert(source.IndexOf("target", StringComparison.Ordinal) + 2, "Z"));

        var largePlainBuilder = new StringBuilder();
        for (var index = 0; index < 1_600; index++)
        {
            largePlainBuilder.Append("plain row ").Append(index).Append(" with ordinary words\n\n");
        }
        var largePlain = largePlainBuilder.ToString();
        CheckLocalExact(
            "large new square bracket without definitions",
            largePlain,
            source => source.Insert(
                source.IndexOf("plain row 800", StringComparison.Ordinal) + 6,
                "["));

        var newDefinitionBuilder = new StringBuilder();
        newDefinitionBuilder.Append("[unresolved][new-id]\n\n");
        for (var index = 0; index < 700; index++)
        {
            newDefinitionBuilder.Append("leading neutral row ").Append(index).Append("\n\n");
        }
        newDefinitionBuilder.Append("definition insertion anchor\n\n");
        for (var index = 0; index < 700; index++)
        {
            newDefinitionBuilder.Append("trailing neutral row ").Append(index).Append("\n\n");
        }
        var newDefinitionSource = newDefinitionBuilder.ToString();
        CheckFallback(
            "new distant reference definition",
            newDefinitionSource,
            source => source.Insert(
                source.IndexOf("definition insertion anchor", StringComparison.Ordinal),
                "ordinary inserted row\n\n[new-id]: https://example.com/new\n"));
    }

    private static void CheckReferenceDefinitionStateSurvivesIncremental()
    {
        var builder = new StringBuilder();
        builder.Append("[target][id]\n\n[id]: https://example.com\n\n");
        for (var index = 0; index < 1_200; index++)
        {
            builder.Append("ordinary reference state row ").Append(index).Append("\n\n");
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var ordinaryAt = source.IndexOf("reference state row 600", StringComparison.Ordinal) + 8;
        var afterOrdinary = source.Insert(ordinaryAt, "Z");
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                source,
                snapshot,
                afterOrdinary,
                out var incremental,
                out _))
        {
            throw new InvalidOperationException(
                "FAIL incremental reference state: ordinary edit unexpectedly fell back");
        }
        AssertEquivalent(
            MarkdownSemanticSnapshot.Parse(afterOrdinary),
            incremental,
            "reference definition state ordinary edit");

        var bracketAt = afterOrdinary.IndexOf("reference state row 900", StringComparison.Ordinal) + 8;
        var afterBracket = afterOrdinary.Insert(bracketAt, "[");
        if (MarkdownSemanticSnapshot.TryParseIncremental(
                afterOrdinary,
                incremental,
                afterBracket,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "FAIL incremental reference state: bracket edit ignored retained definitions");
        }
        Console.WriteLine("PASS incremental reference-definition state survives local snapshot");
    }

    private static void CheckLongPlainParagraphDelimiter()
    {
        var oldSource = "prefix " + new string('a', 6_000) + " middle " + new string('b', 6_000) + " suffix";
        var editAt = oldSource.IndexOf(" middle ", StringComparison.Ordinal) + 1;
        var newSource = oldSource.Insert(editAt, "*");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException("FAIL incremental long plain paragraph delimiter: fallback");
        }
        AssertEquivalent(
            MarkdownSemanticSnapshot.Parse(newSource),
            incremental,
            "long plain paragraph delimiter");
        if (info.NewLength < 12_000)
        {
            throw new InvalidOperationException(
                $"FAIL incremental long plain paragraph delimiter: paragraph was not expanded ({info.NewLength})");
        }
        Console.WriteLine($"PASS incremental long plain paragraph delimiter window={info.NewLength}");
    }

    private static void CheckSafeEditSequence()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 180; index++)
        {
            builder.Append("Paragraph ").Append(index).Append(" ordinary words for local semantic checking.\n\n");
            if ((index % 9) == 0)
            {
                builder.Append("## Heading ").Append(index).Append("\n\n");
            }
            if ((index % 13) == 0)
            {
                builder.Append("A **bold phrase** and https://example.com/").Append(index).Append(" nearby.\n\n");
            }
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        for (var step = 0; step < 12; step++)
        {
            var needle = "ordinary words";
            var searchFrom = Math.Min(source.Length - 1, (step + 1) * source.Length / 14);
            var offset = source.IndexOf(needle, searchFrom, StringComparison.Ordinal);
            if (offset < 0)
            {
                offset = source.LastIndexOf(needle, searchFrom, StringComparison.Ordinal);
            }
            offset += 3;
            var next = source.Insert(offset, ((char)('a' + step)).ToString());
            if (!MarkdownSemanticSnapshot.TryParseIncremental(
                    source,
                    snapshot,
                    next,
                    out var incremental,
                    out _))
            {
                throw new InvalidOperationException($"FAIL incremental safe edit sequence step {step}: fallback");
            }
            AssertEquivalent(MarkdownSemanticSnapshot.Parse(next), incremental, $"safe sequence {step}");
            source = next;
            snapshot = incremental;
        }
        Console.WriteLine("PASS incremental safe edit sequence");
    }

    private static void CheckDeterministicEditFuzz()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 80; index++)
        {
            builder.Append("Paragraph ").Append(index).Append(" with ordinary words and neutral content.\n\n");
            if ((index % 7) == 0)
            {
                builder.Append("## Heading ").Append(index).Append("\n\n");
            }
            if ((index % 9) == 0)
            {
                builder.Append("> quoted row ").Append(index).Append("\n> continuation row\n\n");
            }
            if ((index % 11) == 0)
            {
                builder.Append("- item one\n- item two with **bold** and `code`\n\n");
            }
            if ((index % 17) == 0)
            {
                builder.Append("```text\nfenced content row\nsecond fenced row\n```\n\n");
            }
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var random = new Random(0x50A9);
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
            if (MarkdownSemanticSnapshot.TryParseIncremental(
                    source,
                    snapshot,
                    next,
                    out var incremental,
                    out _))
            {
                AssertEquivalent(expected, incremental, $"fuzz step {step}");
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
                $"FAIL incremental fuzz acceptance unexpectedly low: {accepted}/80");
        }
        Console.WriteLine($"PASS incremental fuzz accepted={accepted} fallback={fallback}");
    }

    private static void ProfileLargeIncrementalEdit()
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

        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm,
                out var warmInfo))
        {
            throw new InvalidOperationException("FAIL incremental 98k profile: fallback");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), warm, "98k profile exactness");

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
                throw new InvalidOperationException("FAIL incremental 98k profile: fallback during timing");
            }
            stopwatch.Stop();
            GC.KeepAlive(result);
            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        var p50 = samples[samples.Length / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        Console.WriteLine(
            $"PROFILE Incremental98k window={warmInfo.NewLength} p50={p50:F3}ms p95={p95:F3}ms");
    }

    private static void CheckLocalExact(string name, string oldSource, Func<string, string> edit)
    {
        var newSource = edit(oldSource);
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental,
                out var info))
        {
            throw new InvalidOperationException($"FAIL incremental {name}: unexpectedly fell back");
        }

        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, name);
        Console.WriteLine($"PASS incremental {name} window={info.NewLength}");
    }

    private static void CheckFallback(string name, string oldSource, Func<string, string> edit)
    {
        var newSource = edit(oldSource);
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        if (MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out _,
                out _))
        {
            throw new InvalidOperationException($"FAIL incremental fallback {name}: local path was accepted");
        }
        Console.WriteLine($"PASS incremental fallback {name}");
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
                throw new InvalidOperationException($"FAIL incremental {name}: line semantic mismatch at {line}");
            }
            if (!expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)))
            {
                throw new InvalidOperationException(
                    $"FAIL incremental {name}: span line index mismatch at {line}");
            }
            if (!expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
            {
                throw new InvalidOperationException(
                    $"FAIL incremental {name}: link line index mismatch at {line}");
            }
        }
        if (!expected.Spans.SequenceEqual(actual.Spans))
        {
            throw new InvalidOperationException($"FAIL incremental {name}: span snapshot mismatch");
        }
        if (!expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException($"FAIL incremental {name}: link snapshot mismatch");
        }
    }
}
