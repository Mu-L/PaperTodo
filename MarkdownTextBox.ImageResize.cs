using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private DispatcherTimer? _imageResizeSettleTimer;
    private bool _isImageResizePreview;
    private bool _imageRenderingSuspended;
    private bool _isImageViewportPreviewQueued;

    public void SetImageRenderingSuspended(bool suspended)
    {
        if (_imageRenderingSuspended == suspended)
        {
            return;
        }

        _imageRenderingSuspended = suspended;
        if (suspended)
        {
            _imageResizeSettleTimer?.Stop();
            _isImageResizePreview = false;
            SetBitmapScalingMode(BitmapScalingMode.HighQuality);
        }

        RefreshTextView();
    }

    private void HandleImageViewportSizeChanged()
    {
        if (!_hadInternalImageReferences || _imageRenderingSuspended)
        {
            return;
        }

        _isImageResizePreview = true;
        SetBitmapScalingMode(BitmapScalingMode.LowQuality);

        // During drag: only retarget visible image block widths (no full text-view rebuild).
        QueueImageViewportPreviewLayout();

        _imageResizeSettleTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (_, _) => CompleteImageResizePreview(),
            Dispatcher);

        _imageResizeSettleTimer.Stop();
        _imageResizeSettleTimer.Start();
    }

    private void CompleteImageResizePreview()
    {
        _imageResizeSettleTimer?.Stop();
        if (_imageRenderingSuspended)
        {
            return;
        }

        _isImageResizePreview = false;
        SetBitmapScalingMode(BitmapScalingMode.HighQuality);

        // One final redraw re-resolves display width and may up/down-grade the single cached decode.
        QueuePostPasteRefresh();
    }

    private void QueueImageViewportPreviewLayout()
    {
        if (_isImageViewportPreviewQueued)
        {
            return;
        }

        _isImageViewportPreviewQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isImageViewportPreviewQueued = false;
                if (_imageRenderingSuspended || !_isImageResizePreview || !_hadInternalImageReferences)
                {
                    return;
                }

                if (!TryApplyImageViewportPreviewLayout())
                {
                    // Visual lines not ready: light redraw only (still no decode upgrade in preview).
                    var textView = TextArea.TextView;
                    if (Document != null && Document.TextLength > 0)
                    {
                        textView.Redraw(0, Document.TextLength, DispatcherPriority.Render);
                    }
                    else
                    {
                        textView.Redraw(DispatcherPriority.Render);
                    }
                }
            }),
            DispatcherPriority.Background);
    }

    private bool TryApplyImageViewportPreviewLayout()
    {
        var textView = TextArea.TextView;
        if (!textView.VisualLinesValid)
        {
            return false;
        }

        var targetWidth = ImageTargetWidth();
        var updated = 0;
        ApplyImageViewportPreviewLayout(textView, targetWidth, ref updated);
        return updated > 0;
    }

    private static void ApplyImageViewportPreviewLayout(
        DependencyObject node,
        double targetWidth,
        ref int updated)
    {
        if (node is Border { Tag: ImageBlockTag } host)
        {
            host.Width = targetWidth;
            switch (host.Child)
            {
                case System.Windows.Controls.Image image:
                    // Preview only retargets layout; LowQuality may upscale a smaller decode until settle.
                    image.Width = Math.Max(24, targetWidth);
                    break;
                case Border placeholder:
                    placeholder.Width = Math.Max(120, Math.Min(targetWidth, placeholder.Width > 0
                        ? Math.Min(placeholder.Width, targetWidth)
                        : targetWidth));
                    break;
            }

            updated++;
            return;
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        for (var i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, i);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            ApplyImageViewportPreviewLayout(child, targetWidth, ref updated);
        }
    }

    private void SetBitmapScalingMode(BitmapScalingMode mode)
    {
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, mode);
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(TextArea.TextView, mode);
    }
}
