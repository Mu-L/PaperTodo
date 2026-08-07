using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class MarkdownEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    public static MarkdownEdgeCapsulePreviewProvider Instance { get; } = new();

    private MarkdownEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var text = context.ReadMarkdownText();
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            MarkdownEdgeCapsulePreviewRenderer.MeasureText(text),
            minimum: 290,
            maximum: 460);
        var lines = MarkdownEdgeCapsulePreviewRenderer.EstimateVisualLines(
            text,
            Math.Max(140, width - 44));
        var height = Math.Clamp(
            74 + Math.Min(15, lines) * AppTypography.Scale(22),
            150,
            410);

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new MarkdownEdgeCapsulePreviewView(context, size));
    }
}

internal sealed class MarkdownEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly Button _open;
    private readonly StackPanel _body;
    private readonly ScrollViewer _scrollViewer;

    public MarkdownEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
        Margin = new Thickness(14, 11, 11, 12);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var heading = new Grid
        {
            Margin = new Thickness(2, 0, 1, 8)
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        heading.Children.Add(_title);

        _open = new Button
        {
            Content = "↗",
            Width = 28,
            Height = 25,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        _open.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        EdgeCapsulePreviewInteraction.SetConsumesPointer(_open, true);
        _open.Click += (_, _) => Context.OpenPaper();
        Grid.SetColumn(_open, 1);
        heading.Children.Add(_open);
        Children.Add(heading);

        _body = new StackPanel
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        _scrollViewer = new ScrollViewer
        {
            Content = _body,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        Grid.SetRow(_scrollViewer, 1);
        Children.Add(_scrollViewer);

        InitializeLiveContent();
    }

    protected override int CaptureContentStamp()
    {
        var text = Context.ReadMarkdownText();
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Context.Title),
            StringComparer.Ordinal.GetHashCode(text),
            text.Length);
    }

    protected override void RebuildContent()
    {
        var offset = _scrollViewer.VerticalOffset;
        var title = Context.Title;
        _title.Text = title;
        _title.ToolTip = title;
        _open.ToolTip = title;
        MarkdownEdgeCapsulePreviewRenderer.RenderInto(
            _body,
            Context.ReadMarkdownText(),
            Context.OpenExternal);
        Dispatcher.BeginInvoke(
            (Action)(() => _scrollViewer.ScrollToVerticalOffset(offset)),
            DispatcherPriority.Loaded);
    }
}

internal static partial class MarkdownEdgeCapsulePreviewRenderer
{
    private static readonly Regex InlinePattern = new(
        @"!\[([^\]]*)\]\(([^)]+)\)|\[([^\]]+)\]\(([^)]+)\)|\*\*(.+?)\*\*|~~(.+?)~~|`([^`]+)`|\*(.+?)\*|_([^_]+)_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeadingPattern = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrderedListPattern = new(
        @"^\s*(\d+)[\.)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnorderedListPattern = new(
        @"^\s*[-+*]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TaskListPattern = new(
        @"^\s*[-+*]\s+\[([ xX])\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HorizontalRulePattern = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ImageOnlyPattern = new(
        @"^!\[([^\]]*)\]\(([^)]+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string MeasureText(string? markdown)
    {
        return string.Join(
            Environment.NewLine,
            NormalizeLines(markdown)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(24)
                .Select(StripBlockPrefix));
    }

    public static int EstimateVisualLines(string? markdown, double widthDip)
    {
        var estimate = 0;
        var inFence = false;
        foreach (var raw in NormalizeLines(markdown).Take(120))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                estimate += 1;
                continue;
            }
            if (trimmed.Length == 0 || HorizontalRulePattern.IsMatch(trimmed))
            {
                estimate += 1;
                continue;
            }

            var lines = EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                StripBlockPrefix(trimmed),
                widthDip);
            estimate += inFence ? Math.Min(3, lines) : Math.Min(4, lines);
        }
        return Math.Max(1, estimate);
    }

    public static void RenderInto(
        Panel target,
        string? markdown,
        Action<string> openExternal)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            AddEmptyState(target);
            return;
        }

        var code = new StringBuilder();
        var inFence = false;
        foreach (var rawLine in NormalizeLines(markdown))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    target.Children.Add(BuildCodeBlock(code.ToString()));
                    code.Clear();
                    inFence = false;
                }
                else
                {
                    inFence = true;
                }
                continue;
            }

            if (inFence)
            {
                if (code.Length > 0)
                {
                    code.AppendLine();
                }
                code.Append(line);
                continue;
            }

            target.Children.Add(BuildBlock(line, openExternal));
        }
        if (inFence || code.Length > 0)
        {
            target.Children.Add(BuildCodeBlock(code.ToString()));
        }
        if (target.Children.Count == 0)
        {
            AddEmptyState(target);
        }
    }

    private static void AddEmptyState(Panel target)
    {
        var empty = NewTextBlock("—", AppTypography.Scale(16));
        empty.Margin = new Thickness(4, 18, 4, 4);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        target.Children.Add(empty);
    }

    private static FrameworkElement BuildBlock(
        string line,
        Action<string> openExternal)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return new Border { Height = AppTypography.Scale(6) };
        }

        if (HorizontalRulePattern.IsMatch(trimmed))
        {
            var rule = new Border
            {
                Height = 1,
                Margin = new Thickness(2, 7, 2, 7)
            };
            rule.SetResourceReference(Border.BackgroundProperty, "PaperBorderBrushKey");
            return rule;
        }

        var image = ImageOnlyPattern.Match(trimmed);
        if (image.Success || trimmed.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
        {
            var label = image.Success ? image.Groups[1].Value : string.Empty;
            var text = NewTextBlock(
                string.IsNullOrWhiteSpace(label) ? "▧" : $"▧ {label}",
                AppTypography.Scale(11.5));
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            var host = new Border
            {
                Margin = new Thickness(1, 4, 1, 4),
                Padding = new Thickness(8, 7, 8, 7),
                CornerRadius = new CornerRadius(5),
                Child = text
            };
            host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
            return host;
        }

        var heading = HeadingPattern.Match(trimmed);
        if (heading.Success)
        {
            var level = heading.Groups[1].Value.Length;
            var text = NewTextBlock(
                string.Empty,
                AppTypography.Scale(Math.Max(13, 19 - level)));
            text.Margin = new Thickness(0, 5, 0, 3);
            text.FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
            AddInlineContent(text.Inlines, heading.Groups[2].Value, openExternal);
            return text;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var text = NewTextBlock(string.Empty, AppTypography.Scale(12));
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            AddInlineContent(text.Inlines, trimmed[1..].TrimStart(), openExternal);
            var host = new Border
            {
                Margin = new Thickness(4, 3, 0, 3),
                Padding = new Thickness(8, 4, 5, 4),
                CornerRadius = new CornerRadius(4),
                Child = text
            };
            host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
            return host;
        }

        var task = TaskListPattern.Match(trimmed);
        if (task.Success)
        {
            var done = !string.Equals(task.Groups[1].Value, " ", StringComparison.Ordinal);
            return BuildListRow(
                done ? "☑" : "☐",
                task.Groups[2].Value,
                openExternal,
                done);
        }

        var ordered = OrderedListPattern.Match(trimmed);
        if (ordered.Success)
        {
            return BuildListRow(
                $"{ordered.Groups[1].Value}.",
                ordered.Groups[2].Value,
                openExternal,
                done: false);
        }

        var unordered = UnorderedListPattern.Match(trimmed);
        if (unordered.Success)
        {
            return BuildListRow(
                "•",
                unordered.Groups[1].Value,
                openExternal,
                done: false);
        }

        var normal = NewTextBlock(string.Empty, AppTypography.Scale(12));
        normal.Margin = new Thickness(0, 2, 0, 3);
        AddInlineContent(normal.Inlines, trimmed, openExternal);
        return normal;
    }

    private static FrameworkElement BuildListRow(
        string marker,
        string content,
        Action<string> openExternal,
        bool done)
    {
        var grid = new Grid
        {
            Margin = new Thickness(2, 2, 0, 2)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var markerText = NewTextBlock(marker, AppTypography.Scale(11.5));
        markerText.Width = marker.Length > 2 ? AppTypography.Scale(28) : AppTypography.Scale(22);
        markerText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        grid.Children.Add(markerText);

        var body = NewTextBlock(string.Empty, AppTypography.Scale(12));
        AddInlineContent(body.Inlines, content, openExternal);
        if (done)
        {
            body.TextDecorations = TextDecorations.Strikethrough;
            body.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement BuildCodeBlock(string code)
    {
        var text = NewTextBlock(code, AppTypography.Scale(10.8));
        text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        text.LineHeight = AppTypography.Scale(16);
        var host = new Border
        {
            Margin = new Thickness(1, 4, 1, 4),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(5),
            Child = text
        };
        host.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        return host;
    }

    private static TextBlock NewTextBlock(string text, double fontSize) => new()
    {
        Text = text,
        FontFamily = NoteTypography.FontFamily,
        FontSize = fontSize,
        FontWeight = FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Max(fontSize + AppTypography.Scale(4), AppTypography.Scale(17))
    };

    private static void AddInlineContent(
        InlineCollection target,
        string text,
        Action<string> openExternal)
    {
        var cursor = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > cursor)
            {
                target.Add(new Run(text[cursor..match.Index]));
            }

            if (match.Groups[1].Success)
            {
                var image = new Span(new Run(string.IsNullOrWhiteSpace(match.Groups[1].Value)
                    ? "▧"
                    : $"▧ {match.Groups[1].Value}"));
                image.SetResourceReference(TextElement.ForegroundProperty, "WeakTextBrushKey");
                target.Add(image);
            }
            else if (match.Groups[3].Success)
            {
                target.Add(CreateLink(
                    match.Groups[3].Value,
                    match.Groups[4].Value,
                    openExternal));
            }
            else if (match.Groups[5].Success)
            {
                target.Add(new Bold(new Run(match.Groups[5].Value)));
            }
            else if (match.Groups[6].Success)
            {
                target.Add(new Span(new Run(match.Groups[6].Value))
                {
                    TextDecorations = TextDecorations.Strikethrough
                });
            }
            else if (match.Groups[7].Success)
            {
                var code = new Span(new Run(match.Groups[7].Value))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = AppTypography.Scale(10.8)
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "HoverBrushKey");
                target.Add(code);
            }
            else
            {
                var italic = match.Groups[8].Success
                    ? match.Groups[8].Value
                    : match.Groups[9].Value;
                target.Add(new Italic(new Run(italic)));
            }
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            target.Add(new Run(text[cursor..]));
        }
    }

    private static Inline CreateLink(
        string label,
        string value,
        Action<string> openExternal)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return new Run(label);
        }

        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = uri,
            Cursor = Cursors.Hand
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "LinkBrushKey");
        EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
        link.RequestNavigate += (_, e) =>
        {
            openExternal(e.Uri.AbsoluteUri);
            e.Handled = true;
        };
        return link;
    }

    private static string[] NormalizeLines(string? markdown) =>
        (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string StripBlockPrefix(string line)
    {
        var trimmed = line.Trim();
        var heading = HeadingPattern.Match(trimmed);
        if (heading.Success)
        {
            return heading.Groups[2].Value;
        }
        var task = TaskListPattern.Match(trimmed);
        if (task.Success)
        {
            return task.Groups[2].Value;
        }
        var ordered = OrderedListPattern.Match(trimmed);
        if (ordered.Success)
        {
            return ordered.Groups[2].Value;
        }
        var unordered = UnorderedListPattern.Match(trimmed);
        if (unordered.Success)
        {
            return unordered.Groups[1].Value;
        }
        return trimmed.StartsWith(">", StringComparison.Ordinal)
            ? trimmed[1..].TrimStart()
            : trimmed;
    }
}
