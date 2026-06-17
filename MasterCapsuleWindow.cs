using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

// Standalone "collapse-all" master capsule. It is permanently pinned at deep-capsule
// slot 0 (real capsules shift down to slot 1..N). Clicking it toggles whether the
// real capsules are retracted behind it. It owns only its own pill chrome and the
// peek/slide animation that mirrors the real capsules; the controller drives the
// retract/release of the real capsule windows.
public sealed class MasterCapsuleWindow : Window
{
    private const double ShellHeight = 30;

    // Compact internal metrics controlling how tightly the glyph + label sit inside the pill.
    // The label is always shown in full; only the right padding is tucked past the screen edge.
    private const double WindowChromeMargin = DeepCapsuleLayout.WindowChromeMargin;
    private const double WindowChromeInset = WindowChromeMargin * 2;
    private const double MasterLeftPadding = 5;
    private const double MasterGlyphGap = 4;
    private const double MasterRightPadding = 10;
    private const double MasterGlyphFontSize = 12;
    private const double MasterLabelFontSize = 11;
    // Reserve a couple of device-independent pixels so text anti-aliasing is not clipped
    // when the visible width is rounded to the screen edge.
    private const double MasterTextPixelReserve = 2;

    private readonly AppController _controller;

    private Border _pill = null!;
    private Border _hoverOverlay = null!;
    private TextBlock _glyph = null!;
    private TextBlock _label = null!;
    private TranslateTransform _pillOffset = null!;

    private bool _isHovering;
    private bool _suppressGeometrySave = true; // master capsule position is always derived, never persisted
    private int _count;
    private bool _active;
    private bool _isPointerDown;
    private bool _isDraggingStartTop;
    private Point _dragStartScreenPos;
    private double _dragStartTopMargin;

    private static readonly DependencyProperty AnimatedLeftProperty =
        DependencyProperty.Register(
            nameof(AnimatedLeft),
            typeof(double),
            typeof(MasterCapsuleWindow),
            new PropertyMetadata(double.NaN, OnAnimatedLeftChanged));

    private double AnimatedLeft
    {
        get => (double)GetValue(AnimatedLeftProperty);
        set => SetValue(AnimatedLeftProperty, value);
    }

    public MasterCapsuleWindow(AppController controller)
    {
        _controller = controller;
        ConfigureWindow();
        BuildContent();
        UpdateToolTipSetting();
        // Clicking the pill must never pull foreground focus: activating this window would
        // deactivate whatever app was in front, forcing it to repaint — the click "flash".
        // WS_EX_NOACTIVATE makes the window unable to become the active/foreground window,
        // so the click toggles collapse-all without disturbing the current foreground app.
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    private void ApplyNoActivateStyle()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate);
    }

    private void ConfigureWindow()
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Width = PaperLayoutDefaults.CapsuleWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
        // Don't steal foreground when first shown — activating would force every other
        // paper window to repaint, which reads as a whole-app flash.
        ShowActivated = false;
        // Start invisible; ShowPlaced() positions us first, then fades in, so we never
        // flash for one frame at the top-left (the default NaN → 0,0 position).
        Opacity = 0;
        RefreshEffectiveTopmost();
    }

    private void BuildContent()
    {
        var host = new Grid { Background = Brushes.Transparent, ClipToBounds = true };

        _pillOffset = new TranslateTransform();
        _pill = new Border
        {
            Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin),
            CornerRadius = new CornerRadius(DeepCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            RenderTransform = _pillOffset
        };

        // The pill background stays opaque (PaperBrush) at all times. Hover tint is a separate
        // overlay layered on top — the same shape as the pill — so the (semi-transparent)
        // HoverBrush never replaces the only opaque layer and let the desktop show through.
        var content = new Grid();

        _hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(DeepCapsuleLayout.CornerRadius),
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        content.Children.Add(_hoverOverlay);

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // Hug the left edge; the master pill is never truncated, so content sits flush left.
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(MasterLeftPadding, 0, MasterRightPadding, 0)
        };

        _glyph = new TextBlock
        {
            Text = "▾",
            Foreground = Theme.TextBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_glyph);

        _label = new TextBlock
        {
            Text = Strings.Get("CapsuleCollapseAllLabel"),
            Foreground = Theme.WeakTextBrush,
            FontSize = 11,
            Margin = new Thickness(MasterGlyphGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_label);
        content.Children.Add(stack);

        _pill.Child = content;
        host.Children.Add(_pill);
        Content = host;

        _pill.MouseEnter += (_, _) =>
        {
            _hoverOverlay.Background = Theme.HoverBrush;
            SetHover(true);
        };
        _pill.MouseLeave += (_, _) =>
        {
            _hoverOverlay.Background = Brushes.Transparent;
            SetHover(false);
        };
        _pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _isPointerDown = true;
            _isDraggingStartTop = false;
            _dragStartScreenPos = PointToScreen(e.GetPosition(this));
            _dragStartTopMargin = _controller.State.DeepCapsuleStartTopMargin;
            _pill.CaptureMouse();
            e.Handled = true;
        };
        _pill.PreviewMouseMove += (_, e) =>
        {
            if (!_isPointerDown || _pill.IsMouseCaptured != true || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentScreenPos = PointToScreen(e.GetPosition(this));
            var deltaY = Math.Abs(currentScreenPos.Y - _dragStartScreenPos.Y);
            if (!_isDraggingStartTop && deltaY < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isDraggingStartTop = true;
            var dpiScaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
            var target = _dragStartTopMargin + (currentScreenPos.Y - _dragStartScreenPos.Y) / Math.Max(0.1, dpiScaleY);
            _controller.SetDeepCapsuleStartTopMargin(target);
            e.Handled = true;
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var wasDragging = _isDraggingStartTop;
            EndStartTopDrag();
            if (wasDragging)
            {
                _controller.SaveNow();
            }
            else
            {
                _controller.ToggleCapsuleCollapseAllActive();
            }

            e.Handled = true;
        };
        _pill.LostMouseCapture += (_, _) =>
        {
            if (_isDraggingStartTop)
            {
                _controller.SaveNow();
            }

            EndStartTopDrag();
        };
        _pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
        };
    }

    public void UpdateTheme()
    {
        // Pill background is always the opaque PaperBrush; the hover tint lives on the overlay.
        _pill.Background = Theme.PaperBrush;
        _pill.BorderBrush = Theme.PaperBorderBrush;
        _hoverOverlay.Background = _isHovering ? Theme.HoverBrush : Brushes.Transparent;
        _glyph.Foreground = Theme.TextBrush;
        _label.Foreground = Theme.WeakTextBrush;
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
    }

    public void RefreshEffectiveTopmost()
    {
        var topmost = !_controller.SuppressTopmostForFullscreenForeground;
        Topmost = topmost;
        if (IsVisible)
        {
            ApplyTopmostZOrder(topmost, _controller.FullscreenAvoidanceWindow);
        }
    }

    // count = number of real capsules behind the master; active = whether they are retracted.
    public void UpdateState(int count, bool active, bool animate)
    {
        _count = count;
        _active = active;
        ApplyStateVisuals();

        ApplyDockedWidth(MasterVisibleWidth());

        MoveToTarget(animate);
        RefreshEffectiveTopmost();
    }

    private void ApplyStateVisuals()
    {
        _glyph.Text = _active ? "▸" : "▾";
        _label.Text = _active
            ? string.Format(CultureInfo.CurrentUICulture, Strings.Get("CapsuleCollapseAllCountFormat"), _count)
            : Strings.Get("CapsuleCollapseAllLabel");
        _pill.ToolTip = _active
            ? Strings.Get("CapsuleCollapseAllCollapsedTip")
            : Strings.Get("CapsuleCollapseAllExpandedTip");
    }

    private void SetHover(bool hovering)
    {
        // Hover only changes the pill background (handled in the MouseEnter/Leave handlers);
        // the master pill does not move, so there is nothing to reposition here.
        _isHovering = hovering;
    }

    private void EndStartTopDrag()
    {
        _isPointerDown = false;
        _isDraggingStartTop = false;
        if (_pill.IsMouseCaptured)
        {
            _pill.ReleaseMouseCapture();
        }
    }

    private double CapsuleWindowWidth()
    {
        // glyph + gap + label + left/right paddings + chrome margins. Both pieces are
        // measured the same way so the pill hugs the actual rendered content.
        var glyphWidth = MeasureText(_glyph.Text, MasterGlyphFontSize, FontWeights.SemiBold);
        var textWidth = MeasureText(_label.Text, MasterLabelFontSize, FontWeights.Normal);
        var shellWidth = Math.Ceiling(MasterLeftPadding + glyphWidth + MasterGlyphGap + textWidth + MasterRightPadding);
        return shellWidth + WindowChromeInset;
    }

    private double MasterVisibleWidth()
    {
        var peekLabel = FirstTextElement(Strings.Get("CapsuleCollapseAllLabel"));
        var visibleWidth = WindowChromeMargin + MasterLeftPadding
            + Math.Max(
                MeasureText("▾", MasterGlyphFontSize, FontWeights.SemiBold),
                MeasureText("▸", MasterGlyphFontSize, FontWeights.SemiBold))
            + MasterGlyphGap
            + MeasureText(peekLabel, MasterLabelFontSize, FontWeights.Normal)
            + MasterRightPadding
            + MasterTextPixelReserve;
        return Math.Clamp(visibleWidth, 1, CapsuleWindowWidth());
    }

    private static string FirstTextElement(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        return enumerator.MoveNext() ? (string)enumerator.Current : string.Empty;
    }

    private void ApplyDockedWidth(double visibleWidth)
    {
        var fullWidth = CapsuleWindowWidth();
        visibleWidth = Math.Clamp(visibleWidth, 1, fullWidth);
        Width = visibleWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
        _pill.Width = Math.Max(0, fullWidth - WindowChromeInset);
        _pillOffset.X = 0;
    }

    private double MeasureText(string text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                Theme.WeakTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    private void MoveToTarget(bool animate)
    {
        var area = DeepCapsuleLayout.WorkArea;
        var visibleWidth = MasterVisibleWidth();
        var targetLeft = RoundX(area.Right - visibleWidth);
        var targetTop = RoundY(DeepCapsuleLayout.TopForIndex(0, _controller.State.DeepCapsuleStartTopMargin));
        var currentLeft = double.IsNaN(Left) || double.IsInfinity(Left) ? targetLeft : RoundX(Left);

        MoveWithoutSave(() =>
        {
            ApplyDockedWidth(visibleWidth);
            Top = targetTop;
            if (!animate)
            {
                Left = targetLeft;
            }
        });

        if (!animate)
        {
            BeginAnimation(AnimatedLeftProperty, null);
            return;
        }

        if (Math.Abs(currentLeft - targetLeft) < 0.5)
        {
            BeginAnimation(AnimatedLeftProperty, null);
            MoveWithoutSave(() => Left = targetLeft);
            return;
        }

        var anim = new DoubleAnimation
        {
            From = currentLeft,
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(_isHovering ? DeepCapsuleLayout.SlideOutMilliseconds : DeepCapsuleLayout.SlideInMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(AnimatedLeftProperty, null);
            MoveWithoutSave(() => Left = targetLeft);
        };
        BeginAnimation(AnimatedLeftProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    // The resting Top of the master, used as the retract/release anchor for real capsules.
    public double AnchorTop => RoundY(DeepCapsuleLayout.TopForIndex(0, _controller.State.DeepCapsuleStartTopMargin));

    private static void OnAnimatedLeftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MasterCapsuleWindow w || e.NewValue is not double left || double.IsNaN(left) || double.IsInfinity(left))
        {
            return;
        }

        w.MoveWithoutSave(() => w.Left = w.RoundX(left));
    }

    private void MoveWithoutSave(Action move)
    {
        var was = _suppressGeometrySave;
        _suppressGeometrySave = true;
        try
        {
            move();
        }
        finally
        {
            _suppressGeometrySave = was;
        }
    }

    private double RoundX(double value)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Round(value, scale);
    }

    private double RoundY(double value)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        return Round(value, scale);
    }

    private static double Round(double value, double scale)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || scale <= 0)
        {
            return value;
        }

        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    // First-time show: position at the final edge-aligned spot BEFORE becoming visible,
    // then fade in. This avoids both the top-left flash and the slide-in from the wrong place.
    public void ShowPlaced(int count, bool active)
    {
        _count = count;
        _active = active;
        ApplyStateVisuals();

        ApplyDockedWidth(MasterVisibleWidth());
        MoveToTarget(animate: false);

        Show();
        RefreshEffectiveTopmost();

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public void CloseForReal()
    {
        BeginAnimation(AnimatedLeftProperty, null);
        Close();
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void ApplyTopmostZOrder(bool topmost, IntPtr insertAfter)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            topmost ? HwndTopmost : HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

        if (!topmost && insertAfter != IntPtr.Zero)
        {
            SetWindowPos(
                handle,
                insertAfter,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);
}
