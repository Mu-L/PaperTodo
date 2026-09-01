using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PaperTodo;

internal static class PerformanceProfileChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        ProfileFullParse();
        ProfileDenseEscapes();
    }

    private static void ProfileFullParse()
    {
        var source = BuildDenseMarkdown(98_000);
        for (var warmup = 0; warmup < 2; warmup++)
        {
            GC.KeepAlive(MarkdownSemanticSnapshot.Parse(source));
        }

        const int iterations = 7;
        var elapsed = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            GC.KeepAlive(MarkdownSemanticSnapshot.Parse(source));
            elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(elapsed);
        Console.WriteLine(
            $"PROFILE Markdown semantic parse {source.Length} chars; " +
            $"median-total run of {iterations} warmed runs={elapsed[iterations / 2]:F3}ms");
    }

    private static void ProfileDenseEscapes()
    {
        var source = string.Concat(Enumerable.Repeat(@"\*", 15_000));
        for (var warmup = 0; warmup < 2; warmup++)
        {
            GC.KeepAlive(MarkdownSemanticSnapshot.Parse(source));
        }

        const int iterations = 7;
        var elapsed = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            if (snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.EscapeMarker) != 15_000)
            {
                throw new InvalidOperationException("FAIL dense escapes: escape marker count mismatch");
            }
            elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(elapsed);
        Console.WriteLine(
            $"PROFILE DenseEscapes30k p50={elapsed[iterations / 2]:F3}ms " +
            $"p95={elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]:F3}ms");
    }

    private static string BuildDenseMarkdown(int minimumLength)
    {
        var builder = new StringBuilder(minimumLength + 512);
        var index = 0;
        while (builder.Length < minimumLength)
        {
            builder.Append("## Heading ").Append(index).Append('\n');
            builder.Append("> quote **strong** *emphasis* ~~strike~~ `code` [link](https://example.com/")
                .Append(index).Append(")\n");
            builder.Append("- [ ] task ").Append(index).Append(" with https://example.org/").Append(index).Append('\n');
            builder.Append("1. ordered item ").Append(index).Append('\n');
            builder.Append("<strong>html</strong> <a href=\"https://example.net/")
                .Append(index).Append("\">anchor</a>\\*\n\n");
            index++;
        }
        return builder.ToString();
    }
}
