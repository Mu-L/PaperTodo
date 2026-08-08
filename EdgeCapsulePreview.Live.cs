using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One live preview surface. Size is frozen by the queue session, while the content may refresh
/// from the current paper model. Model notifications are coalesced on the Dispatcher so content
/// construction never delays the shell's first expansion frame.
/// </summary>
internal abstract class EdgeCapsuleLivePreviewView : Grid
{
    private DispatcherOperation? _refreshOperation;
    private bool _contentDirty = true;
    private bool _subscribed;
    private bool _refreshing;

    protected EdgeCapsuleLivePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        Context = context;
        PreviewSize = size;
        Background = System.Windows.Media.Brushes.Transparent;
        ClipToBounds = true;

        Loaded += (_, _) =>
        {
            Subscribe();
            QueueRefresh();
        };
        Unloaded += (_, _) =>
        {
            Unsubscribe();
            CancelQueuedRefresh();
            _contentDirty = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _contentDirty = true;
            }
            QueueRefresh();
        };
    }

    protected EdgeCapsulePreviewContext Context { get; }
    protected EdgeCapsulePreviewSize PreviewSize { get; }

    protected void InitializeLiveContent()
    {
        _contentDirty = true;
        QueueRefresh();
    }

    protected abstract void RebuildContent();

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        Context.InvalidationSource.Invalidated += OnContentInvalidated;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        Context.InvalidationSource.Invalidated -= OnContentInvalidated;
        _subscribed = false;
    }

    private void OnContentInvalidated()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                (Action)OnContentInvalidated,
                DispatcherPriority.Background);
            return;
        }

        _contentDirty = true;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!_contentDirty ||
            !IsLoaded ||
            !IsVisible ||
            _refreshOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

        _refreshOperation = Dispatcher.BeginInvoke(
            (Action)RefreshIfDirty,
            DispatcherPriority.Background);
    }

    private void CancelQueuedRefresh()
    {
        if (_refreshOperation is { Status: DispatcherOperationStatus.Pending })
        {
            _refreshOperation.Abort();
        }
        _refreshOperation = null;
    }

    private void RefreshIfDirty()
    {
        _refreshOperation = null;
        if (!_contentDirty || !IsLoaded || !IsVisible || _refreshing)
        {
            return;
        }

        _contentDirty = false;
        _refreshing = true;
        try
        {
            RebuildContent();
        }
        catch
        {
            // The preview is optional. A model refresh racing this UI pass retries on the next
            // invalidation or visibility transition instead of taking down the paper or queue.
        }
        finally
        {
            _refreshing = false;
        }

        QueueRefresh();
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

    public PluginFallbackEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
        : base(context, size)
    {
        Margin = new Thickness(16, 13, 14, 14);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        Children.Add(_title);

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

    protected override void RebuildContent()
    {
        var title = Context.Title;
        var status = Context.ReadPluginStatus();
        _title.Text = title;
        _title.ToolTip = title;
        _status.Text = string.IsNullOrWhiteSpace(status) ? "◇" : status;
    }
}
