using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;

namespace PaperTodo;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();


    private static readonly System.Windows.Media.FontFamily ConsolasFontFamily = new("Consolas");
    private static Brush TextBrush => Theme.TextBrush;
    private static Brush WeakBrush => Theme.WeakTextBrush;
    private static Brush CodeBrush => Theme.CodeBrush;
    private static Brush QuoteBorderBrush => Theme.QuoteBorderBrush;
    private static Brush LinkBrush => Theme.LinkBrush;

    public static FlowDocument Render(string? markdown)
    {
        var document = CreateDocument();
        var parsed = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var block in parsed)
        {
            AddBlock(document.Blocks, block);
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph());
        }

        return document;
    }

    private static FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(14, 8, 14, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = TextBrush,
            Background = Brushes.Transparent,
            LineHeight = 21
        };
    }

    private static void AddBlock(BlockCollection blocks, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddHeading(blocks, heading);
                break;

            case ParagraphBlock paragraph:
                blocks.Add(CreateParagraph(paragraph.Inline, new Thickness(0, 2, 0, 6)));
                break;

            case QuoteBlock quote:
                AddQuote(blocks, quote);
                break;

            case Markdig.Syntax.ListBlock list:
                AddList(blocks, list);
                break;

            case FencedCodeBlock fenced:
                AddCodeBlock(blocks, fenced.Lines.ToString());
                break;

            case CodeBlock code:
                AddCodeBlock(blocks, code.Lines.ToString());
                break;

            case ThematicBreakBlock:
                AddThematicBreak(blocks);
                break;

            case HtmlBlock:
                // PaperTodo intentionally does not support embedded HTML.
                break;

            default:
                if (block is ContainerBlock container)
                {
                    foreach (var child in container)
                    {
                        AddBlock(blocks, child);
                    }
                }
                break;
        }
    }

    private static void AddHeading(BlockCollection blocks, HeadingBlock heading)
    {
        var size = heading.Level switch
        {
            1 => 21,
            2 => 18,
            3 => 16,
            _ => 14
        };

        var paragraph = CreateParagraph(heading.Inline, new Thickness(0, heading.Level == 1 ? 8 : 6, 0, 6));
        paragraph.FontSize = size;
        paragraph.FontWeight = FontWeights.SemiBold;
        blocks.Add(paragraph);
    }

    private static Paragraph CreateParagraph(ContainerInline? inline, Thickness margin)
    {
        var paragraph = new Paragraph
        {
            Margin = margin
        };

        AddInlines(paragraph.Inlines, inline);
        return paragraph;
    }

    private static void AddQuote(BlockCollection blocks, QuoteBlock quote)
    {
        var section = new Section
        {
            Margin = new Thickness(6, 4, 0, 8),
            Padding = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = QuoteBorderBrush,
            Foreground = WeakBrush
        };

        foreach (var child in quote)
        {
            AddBlock(section.Blocks, child);
        }

        blocks.Add(section);
    }

    private static void AddList(BlockCollection blocks, Markdig.Syntax.ListBlock list)
    {
        var wpfList = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(16, 2, 0, 6),
            Padding = new Thickness(12, 0, 0, 0)
        };

        foreach (var rawItem in list)
        {
            if (rawItem is not ListItemBlock item)
            {
                continue;
            }

            var wpfItem = new WpfListItem();

            foreach (var child in item)
            {
                AddBlock(wpfItem.Blocks, child);
            }

            if (wpfItem.Blocks.Count == 0)
            {
                wpfItem.Blocks.Add(new Paragraph());
            }

            wpfList.ListItems.Add(wpfItem);
        }

        blocks.Add(wpfList);
    }

    private static void AddCodeBlock(BlockCollection blocks, string code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = ConsolasFontFamily,
            FontSize = 13,
            Background = CodeBrush,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 6, 0, 8)
        };

        paragraph.Inlines.Add(new Run(code.TrimEnd('\r', '\n')));
        blocks.Add(paragraph);
    }

    private static void AddThematicBreak(BlockCollection blocks)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
            Foreground = WeakBrush
        };
        paragraph.Inlines.Add(new Run("────────────────"));
        blocks.Add(paragraph);
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container == null)
        {
            return;
        }

        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            AddInline(target, inline);
        }
    }

    private static void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = ConsolasFontFamily,
                    FontSize = 13,
                    Background = CodeBrush
                });
                break;

            case EmphasisInline emphasis:
                target.Add(RenderEmphasis(emphasis));
                break;

            case LinkInline link:
                AddLink(target, link);
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case HtmlInline:
                // PaperTodo intentionally does not support embedded HTML.
                break;

            case ContainerInline container:
                AddInlines(target, container);
                break;

            default:
                if (inline is LeafInline leaf)
                {
                    target.Add(new Run(leaf.ToString()));
                }
                break;
        }
    }

    private static WpfInline RenderEmphasis(EmphasisInline emphasis)
    {
        var span = new Span();
        AddInlines(span.Inlines, emphasis);

        if (emphasis.DelimiterChar == '~')
        {
            span.TextDecorations = TextDecorations.Strikethrough;
            span.Foreground = WeakBrush;
            return span;
        }

        if (emphasis.DelimiterCount >= 2)
        {
            return new Bold(span);
        }

        return new Italic(span);
    }

    private static void AddLink(InlineCollection target, LinkInline link)
    {
        if (link.IsImage)
        {
            // Images are intentionally unsupported. Render the alt text only.
            var alt = new Span { Foreground = WeakBrush };
            AddInlines(alt.Inlines, link);
            target.Add(alt);
            return;
        }

        var label = new Span();
        AddInlines(label.Inlines, link);

        if (label.Inlines.Count == 0)
        {
            label.Inlines.Add(new Run(link.Url ?? Strings.Get("MarkdownDefaultLinkLabel")));
        }

        var hyperlink = new Hyperlink(label)
        {
            Foreground = LinkBrush
        };

        if (!string.IsNullOrEmpty(link.Url) && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += OpenLink;
            hyperlink.Cursor = System.Windows.Input.Cursors.Hand;
        }

        target.Add(hyperlink);
    }
    private static void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Keep preview quiet if Windows cannot open the link.
        }
    }
}
