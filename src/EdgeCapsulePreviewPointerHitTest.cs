using System.Windows;

namespace PaperTodo;

/// <summary>
/// Host-private pointer routing hook for preview surfaces whose interactive geometry lives outside
/// the WPF visual tree (currently WebView2 mini surfaces). The callback receives a point in the
/// marked element's own coordinate space and must be side-effect free.
/// </summary>
internal static class EdgeCapsulePreviewPointerHitTest
{
    private static readonly DependencyProperty HitTestProperty =
        DependencyProperty.RegisterAttached(
            "HitTest",
            typeof(Func<Point, bool>),
            typeof(EdgeCapsulePreviewPointerHitTest),
            new FrameworkPropertyMetadata(null));

    internal static void Set(
        DependencyObject element,
        Func<Point, bool>? hitTest)
    {
        if (hitTest == null)
        {
            element.ClearValue(HitTestProperty);
            return;
        }
        element.SetValue(HitTestProperty, hitTest);
    }

    internal static bool IsInteractive(
        DependencyObject element,
        Point point)
    {
        if (element.GetValue(HitTestProperty) is not Func<Point, bool> hitTest)
        {
            return false;
        }

        try
        {
            return hitTest(point);
        }
        catch
        {
            // Preview input falls back to host ownership if a dynamic surface cannot classify it.
            return false;
        }
    }
}
