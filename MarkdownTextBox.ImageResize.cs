using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private DispatcherTimer? _imageResizeSettleTimer;
    private bool _isImageResizePreview;
    private bool _imageRenderingSuspended;

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

        // Keep image blocks following the viewport while reusing their existing BitmapSource.
        QueuePostPasteRefresh();

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

        // One final redraw may replace a too-small cached decode, but never adds a second version.
        QueuePostPasteRefresh();
    }

    private void SetBitmapScalingMode(BitmapScalingMode mode)
    {
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, mode);
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(TextArea.TextView, mode);
    }
}
