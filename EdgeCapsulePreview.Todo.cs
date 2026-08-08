using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class TodoEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    // Keep row creation bounded; totals still describe the complete model and the paper remains
    // the place for browsing the full list.
    internal const int MaximumRenderedItems = 12;
    internal const int MaximumItemCharacters = 512;

    public static TodoEdgeCapsulePreviewProvider Instance { get; } = new();

    private TodoEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var items = MeaningfulItems(context.Paper);
        var body = string.Join(
            Environment.NewLine,
            items.Take(12).Select(item => item.Text));
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            body,
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 450);
        var availableTextWidth = Math.Max(64, width - 60);
        var estimatedLines = items.Count == 0
            ? 1
            : items.Take(12).Sum(item => Math.Clamp(
                EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                    item.Text,
                    availableTextWidth),
                1,
                3));
        var height = Math.Clamp(
            62 + Math.Min(12, estimatedLines) * AppTypography.Scale(28),
            150,
            400);

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new TodoEdgeCapsulePreviewView(context, size));
    }

    private static List<PaperItem> MeaningfulItems(PaperData paper) =>
        paper.Items
            .Where(TodoRules.HasMeaningfulContent)
            .OrderBy(item => item.Order)
            .Take(MaximumRenderedItems)
            .ToList();
}

internal sealed class TodoEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly TextBlock _summary;
    private readonly StackPanel _items;
    private readonly ScrollViewer _scrollViewer;
    private bool _rebuilding;

    public TodoEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var heading = new Grid
        {
            Margin = new Thickness(2, 0, 1, 7)
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Max(48, size.WidthDip - 86)
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        heading.Children.Add(_title);

        _summary = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            VerticalAlignment = VerticalAlignment.Center
        };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetColumn(_summary, 1);
        heading.Children.Add(_summary);
        Children.Add(heading);

        _items = new StackPanel
        {
            Margin = new Thickness(0, 0, 2, 0)
        };
        _scrollViewer = new ScrollViewer
        {
            Content = _items,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0)
        };
        Grid.SetRow(_scrollViewer, 1);
        Children.Add(_scrollViewer);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var offset = _scrollViewer.VerticalOffset;
        var meaningful = Context.Paper.Items
            .Where(TodoRules.HasMeaningfulContent)
            .OrderBy(item => item.Order)
            .ToList();
        var done = meaningful.Count(item => item.Done);

        _title.Text = Context.Title;
        _title.ToolTip = Context.Title;
        _summary.Text = $"{done}/{meaningful.Count}";

        _rebuilding = true;
        try
        {
            _items.Children.Clear();
            if (meaningful.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "—",
                    Margin = new Thickness(8, 18, 8, 8),
                    FontFamily = AppTypography.UiFontFamily,
                    FontSize = AppTypography.Scale(16),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
                _items.Children.Add(empty);
            }
            else
            {
                foreach (var item in meaningful.Take(
                    TodoEdgeCapsulePreviewProvider.MaximumRenderedItems))
                {
                    _items.Children.Add(BuildRow(item));
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        Dispatcher.BeginInvoke(
            (Action)(() => _scrollViewer.ScrollToVerticalOffset(offset)),
            DispatcherPriority.Loaded);
    }

    private FrameworkElement BuildRow(PaperItem item)
    {
        var row = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(3, 3, 4, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };
        row.MouseEnter += (_, _) =>
            row.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = Context.ReadTodoCheckStyle()
        };
        EdgeCapsulePreviewInteraction.SetConsumesPointer(check, true);
        check.Click += (_, _) =>
        {
            if (_rebuilding)
            {
                return;
            }

            var requested = check.IsChecked == true;
            if (!Context.SetTodoDone(item.Id, requested))
            {
                _rebuilding = true;
                check.IsChecked = item.Done;
                _rebuilding = false;
                return;
            }
        };
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = PreviewItemText(item.Text),
            Margin = new Thickness(1, 0, 5, 0),
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: false),
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(
            TextBlock.ForegroundProperty,
            item.Done ? "WeakTextBrushKey" : "TextBrushKey");
        if (item.Done)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var marker = BuildItemMarker(item);
        Grid.SetColumn(marker, 2);
        grid.Children.Add(marker);

        row.Child = grid;
        return row;
    }

    private static string PreviewItemText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var text = value.Trim();
        var maximum = TodoEdgeCapsulePreviewProvider.MaximumItemCharacters;
        return text.Length <= maximum
            ? text
            : text[..(maximum - 1)] + "…";
    }

    private FrameworkElement BuildItemMarker(PaperItem item)
    {
        var markers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (item.ReminderAt.HasValue || item.ReminderTriggered)
        {
            markers.Children.Add(CreateMarkerText("◷"));
        }

        string? linkedMarker = null;
        if (!string.IsNullOrWhiteSpace(item.LinkedPaperId))
        {
            linkedMarker = "↗";
        }
        else if (!string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            linkedMarker = "⌁";
        }

        if (linkedMarker != null)
        {
            var link = CreateMarkerText(linkedMarker);
            link.Cursor = Cursors.Hand;
            EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
            link.MouseEnter += (_, _) =>
                link.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "LinkBrushKey");
            link.MouseLeave += (_, _) =>
                link.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "WeakTextBrushKey");
            link.MouseLeftButtonUp += (_, e) =>
            {
                Context.OpenTodoLinkedTarget(item.Id);
                e.Handled = true;
            };
            markers.Children.Add(link);
        }

        return markers;
    }

    private static TextBlock CreateMarkerText(string text)
    {
        var marker = new TextBlock
        {
            Text = text,
            Margin = new Thickness(1, 0, 1, 0),
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(10.5),
            VerticalAlignment = VerticalAlignment.Center
        };
        marker.SetResourceReference(
            TextBlock.ForegroundProperty,
            "WeakTextBrushKey");
        return marker;
    }
}
