using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One live preview surface. Size is frozen by the queue session, while the content may refresh
/// from the current paper model. The timer exists only while the single global preview is visible.
/// </summary>
internal abstract class EdgeCapsuleLivePreviewView : Grid
{
    private const int RefreshIntervalMilliseconds = 180;

    private readonly DispatcherTimer _refreshTimer;
    private int _lastContentStamp = int.MinValue;
    private bool _refreshing;

    protected EdgeCapsuleLivePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        Context = context;
        PreviewSize = size;
        Background = System.Windows.Media.Brushes.Transparent;
        ClipToBounds = true;

        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(RefreshIntervalMilliseconds),
            DispatcherPriority.Background,
            (_, _) => RefreshIfChanged(),
            Dispatcher)
        {
            IsEnabled = false
        };

        Loaded += (_, _) => UpdateRefreshState();
        Unloaded += (_, _) => _refreshTimer.Stop();
        IsVisibleChanged += (_, _) => UpdateRefreshState();
    }

    protected EdgeCapsulePreviewContext Context { get; }
    protected EdgeCapsulePreviewSize PreviewSize { get; }

    protected void InitializeLiveContent()
    {
        RefreshNow();
        UpdateRefreshState();
    }

    protected void RefreshNow()
    {
        _lastContentStamp = int.MinValue;
        RefreshIfChanged();
    }

    protected abstract int CaptureContentStamp();
    protected abstract void RebuildContent();

    private void UpdateRefreshState()
    {
        if (IsLoaded && IsVisible)
        {
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
            RefreshIfChanged();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private void RefreshIfChanged()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var stamp = CaptureContentStamp();
            if (stamp == _lastContentStamp)
            {
                return;
            }

            RebuildContent();
            _lastContentStamp = CaptureContentStamp();
        }
        catch
        {
            // The preview is optional. A model refresh racing this UI pass retries on the next
            // tick instead of taking down the owning paper or the edge queue.
            _lastContentStamp = int.MinValue;
        }
        finally
        {
            _refreshing = false;
        }
    }
}

internal static class EdgeCapsulePreviewMeasure
{
    private const double ApproximateGlyphWidthDip = 6.4;

    public static double MeasureWidth(
        string? title,
        string? body,
        double minimum,
        double maximum)
    {
        var longest = Math.Max(
            DisplayWidth(title),
            (body ?? string.Empty)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n')
                .Take(32)
                .Select(DisplayWidth)
                .DefaultIfEmpty(0)
                .Max());
        var desired = 118 + Math.Min(64, longest) * ApproximateGlyphWidthDip;
        return Math.Clamp(Math.Ceiling(desired), minimum, maximum);
    }

    public static int EstimateWrappedLines(string? text, double contentWidthDip)
    {
        var unitsPerLine = Math.Max(
            12,
            (int)Math.Floor(contentWidthDip / ApproximateGlyphWidthDip));
        var total = 0;
        foreach (var line in (text ?? string.Empty)
                     .Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n')
                     .Take(80))
        {
            total += Math.Max(
                1,
                (int)Math.Ceiling(DisplayWidth(line) / (double)unitsPerLine));
        }
        return Math.Max(1, total);
    }

    public static int DisplayWidth(string? text) =>
        EdgeCapsuleLayout.DisplayWidth(text ?? string.Empty);
}

internal sealed class PluginFallbackEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly Button _open;

    public PluginFallbackEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
        Margin = new Thickness(16, 13, 14, 14);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid();
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
            Width = 30,
            Height = 26,
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        _open.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        EdgeCapsulePreviewInteraction.SetConsumesPointer(_open, true);
        _open.Click += (_, _) => Context.OpenPaper();
        Grid.SetColumn(_open, 1);
        heading.Children.Add(_open);
        Children.Add(heading);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetRow(_status, 1);
        Children.Add(_status);

        var form = new TextBlock
        {
            Text = Context.PaperExpanded ? "●" : "○",
            Margin = new Thickness(0, 10, 0, 0),
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(10),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        form.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetRow(form, 2);
        Children.Add(form);

        InitializeLiveContent();
    }

    protected override int CaptureContentStamp()
    {
        var hash = new HashCode();
        hash.Add(Context.Title, StringComparer.Ordinal);
        hash.Add(Context.ReadPluginStatus(), StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    protected override void RebuildContent()
    {
        var title = Context.Title;
        var status = Context.ReadPluginStatus();
        _title.Text = title;
        _title.ToolTip = title;
        _open.ToolTip = title;
        _status.Text = string.IsNullOrWhiteSpace(status) ? "◇" : status;
    }
}
