using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace PaperTodo;

// Standalone queue-header capsule. It is permanently pinned at deep-capsule slot 0
// (real capsules shift down to slot 1..N), exposes paging when a queue exceeds the
// current screen capacity, and optionally toggles whether the real capsules are
// retracted behind it. It owns only its own pill chrome and vertical stack anchor;
// the controller drives paging and retract/release of the real capsule windows.
public sealed class MasterCapsuleWindow : Window
{
    private enum MasterGestureState
    {
        Idle,
        Pending,
        Dragging
    }

    private sealed record MasterDragSession(
        DeviceScreenPoint StartScreenPosition,
        double StartTopMargin);

    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    // Compact internal metrics controlling how tightly the glyph + label sit inside the pill.
    // The master owns exactly the width it renders; no full pill is hidden outside its HWND.
    private const double WindowChromeMargin = EdgeCapsuleLayout.WindowChromeMargin;
    private const double MasterLeftPadding = 5;
    private const double MasterGlyphGap = 4;
    private const double MasterRightPadding = 3;
    private const double MasterInteriorBorderThickness = 1;
    private const double PageButtonHorizontalPadding = 3;

    private readonly AppController _controller;
    private readonly DeepCapsuleContextMenuSession _contextMenuSession;
    private double MasterGlyphFontSize => AppTypography.Scale(12);
    private double MasterLabelFontSize => VisualTextSizes.FontSize(12, _controller.State.CapsuleTextSize);
    private FontFamily MasterLabelFontFamily =>
        AppTypography.FontFamilyFor(content: false, bold: _controller.State.CapsuleTextBold);

    private FontWeight MasterLabelFontWeight =>
        AppTypography.FontWeightFor(_controller.State.CapsuleTextBold);

    // Which queue this master serves: (monitor device, edge). Each docked-capsule queue has its
    // own master pill at slot 0 of that queue. Geometry resolves against this queue's monitor+edge.
    private EdgeCapsuleEdge _queueEdge;
    private string _queueMonitorDeviceName = "";

    private Border _pill = null!;
    private Border _hoverOverlay = null!;
    private TextBlock _glyph = null!;
    private TextBlock _label = null!;
    private Border _previousPageButton = null!;
    private Border _nextPageButton = null!;
    private TextBlock _previousPageGlyph = null!;
    private TextBlock _nextPageGlyph = null!;
    private TextBlock _pageLabel = null!;
    private StackPanel _headerStack = null!;
    private StackPanel _contentStack = null!;

    private bool _isHovering;
    private bool _experimentalPassive;
    private int _count;
    private bool _active;
    private bool _collapseEnabled = true;
    private int _pageIndex;
    private int _pageCount = 1;
    private int _visibleSlotCount;
    private double _topOffsetDip;
    private Border? _pressedPageButton;
    private MasterGestureState _gestureState;
    private double _currentTopDip = double.NaN;
    private MonitorGeometry? _animatedMonitorGeometry;
    private double _animatedWidthDip;
    private int _moveGeneration;
    private bool _isClosingForReal;
    // The master pill is dragged vertically only: it slides its queue's stack by driving the
    // shared start-top margin. It never detaches or changes edge/monitor — that is done by
    // dragging an individual side capsule to another edge / screen.
    private MasterDragSession? _dragSession;

    private static readonly DependencyProperty AnimatedTopProperty =
        DependencyProperty.Register(
            nameof(AnimatedTop),
            typeof(double),
            typeof(MasterCapsuleWindow),
            new PropertyMetadata(double.NaN, OnAnimatedTopChanged));

    private double AnimatedTop
    {
        get => (double)GetValue(AnimatedTopProperty);
        set => SetValue(AnimatedTopProperty, value);
    }

    public MasterCapsuleWindow(AppController controller, EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName)
    {
        _controller = controller;
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
        _contextMenuSession = new DeepCapsuleContextMenuSession(
            controller,
            $"master:{Guid.NewGuid():N}",
            Dispatcher,
            IsPointInsideMasterOwnerSurface);
        ConfigureWindow();
        BuildContent();
        UpdateExperimentalOpacity();
        UpdateToolTipSetting();
        // Clicking the pill must never pull foreground focus: activating this window would
        // deactivate whatever app was in front, forcing it to repaint — the click "flash".
        // WS_EX_NOACTIVATE makes the window unable to become the active/foreground window,
        // so the click toggles collapse-all without disturbing the current foreground app.
        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            if (_experimentalPassive)
            {
                WindowNative.SetInputPassthrough(this, enabled: true);
                WindowNative.ApplyBottomZOrder(this);
            }
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(OnWindowMessage);
            }

            // The HWND can acquire a different per-monitor DPI than the pre-show WPF visual.
            // Re-measure and re-anchor once that real source exists.
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!_isClosingForReal)
                    {
                        MoveToTarget(animate: false);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void ConfigureWindow()
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);
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
        var host = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = false
        };

        _pill = new Border
        {
            Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin),
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.10
            }
        };

        // The pill background stays opaque (PaperBrush) at all times. Hover tint is a separate
        // overlay layered on top — the same shape as the pill — so the (semi-transparent)
        // HoverBrush never replaces the only opaque layer and let the desktop show through.
        var content = new Grid();

        _hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
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
        _contentStack = stack;

        _glyph = new TextBlock
        {
            Text = "▾",
            Foreground = Theme.TextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = MasterGlyphFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_glyph);

        _label = new TextBlock
        {
            Text = Strings.Get("CapsuleCollapseAllLabel"),
            Foreground = Theme.WeakTextBrush,
            FontFamily = MasterLabelFontFamily,
            FontSize = MasterLabelFontSize,
            FontWeight = MasterLabelFontWeight,
            Margin = new Thickness(MasterGlyphGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AppTypography.ApplyTextRendering(_label);
        stack.Children.Add(_label);

        _pageLabel = new TextBlock
        {
            Foreground = Theme.WeakTextBrush,
            FontFamily = MasterLabelFontFamily,
            FontSize = MasterLabelFontSize,
            FontWeight = MasterLabelFontWeight,
            Margin = new Thickness(MasterGlyphGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AppTypography.ApplyTextRendering(_pageLabel);
        stack.Children.Add(_pageLabel);

        _previousPageButton = CreatePageButton("‹", out _previousPageGlyph);
        _nextPageButton = CreatePageButton("›", out _nextPageGlyph);
        ConfigurePageButton(_previousPageButton, delta: -1);
        ConfigurePageButton(_nextPageButton, delta: 1);

        var headerStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _headerStack = headerStack;
        headerStack.Children.Add(_previousPageButton);
        headerStack.Children.Add(stack);
        headerStack.Children.Add(_nextPageButton);
        content.Children.Add(headerStack);

        _pill.Child = content;
        // Same chrome as the tray menu. The NOACTIVATE host delegates promotion,
        // guards and stale-focus cleanup to DeepCapsuleContextMenuSession.
        var contextMenu = _controller.CreateTrayMenu(registerForLiveRefresh: true);
        _pill.ContextMenu = contextMenu;
        _pill.ContextMenuOpening += (_, _) => _controller.RebuildTrayMenu(contextMenu);
        contextMenu.Opened += (_, _) => _contextMenuSession.HandleOpened(contextMenu);
        contextMenu.Closed += (_, _) => _contextMenuSession.HandleClosed(contextMenu);
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
            if (IsPageNavigationSource(e.OriginalSource))
            {
                return;
            }

            _pressedPageButton = null;
            _dragSession = new MasterDragSession(
                DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this))),
                _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge));
            _gestureState = MasterGestureState.Pending;
            _pill.CaptureMouse();
            e.Handled = true;
        };
        _pill.PreviewMouseMove += (_, e) =>
        {
            var session = _dragSession;
            if (_gestureState == MasterGestureState.Idle ||
                session == null ||
                _pill.IsMouseCaptured != true)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishMasterGesture(commit: false);
                return;
            }

            var currentScreenPos = DeviceScreenPoint.FromPoint(PointToScreen(e.GetPosition(this)));
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(_queueMonitorDeviceName, this, out var geometry))
            {
                return;
            }

            var deltaX = (currentScreenPos.X - session.StartScreenPosition.X) / geometry.DpiScaleX;
            var deltaY = (currentScreenPos.Y - session.StartScreenPosition.Y) / geometry.DpiScaleY;
            if (_gestureState == MasterGestureState.Pending &&
                Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (_gestureState == MasterGestureState.Pending)
            {
                _gestureState = MasterGestureState.Dragging;
                ++_moveGeneration;
                _animatedMonitorGeometry = null;
                BeginAnimation(AnimatedTopProperty, null);
            }

            // The master stays pinned to its queue's edge; vertical drag slides that queue's stack
            // by driving the shared start-top margin. It never detaches or changes edge/monitor —
            // moving capsules between queues is done by dragging an individual side capsule.
            var targetMargin = session.StartTopMargin + deltaY;
            _controller.SetDeepCapsuleStartTopMargin(_queueMonitorDeviceName, _queueEdge, targetMargin);

            e.Handled = true;
        };
        _pill.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_pressedPageButton != null && !IsPageNavigationSource(e.OriginalSource))
            {
                _pressedPageButton = null;
                e.Handled = true;
                return;
            }

            if (IsPageNavigationSource(e.OriginalSource))
            {
                return;
            }

            var wasDragging = FinishMasterGesture(commit: true, clearFocus: false);
            if (!wasDragging && _collapseEnabled)
            {
                _controller.ToggleCapsuleCollapseAllActive(_queueMonitorDeviceName, _queueEdge);
            }

            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };
        _pill.LostMouseCapture += (_, _) => FinishMasterGesture(commit: false);
        _pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
        };
    }

    private Border CreatePageButton(string glyph, out TextBlock glyphBlock)
    {
        glyphBlock = new TextBlock
        {
            Text = glyph,
            Foreground = Theme.TextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = MasterGlyphFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var button = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius / 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(PageButtonHorizontalPadding, 0, PageButtonHorizontalPadding, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Child = glyphBlock
        };
        button.MouseEnter += (_, _) =>
        {
            if (CanNavigateFromPageButton(button))
            {
                button.Background = Theme.HoverBrush;
            }
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (ReferenceEquals(_pressedPageButton, button))
            {
                _pressedPageButton = null;
            }
        };
        return button;
    }

    private bool CanNavigateFromPageButton(Border button) =>
        ReferenceEquals(button, _previousPageButton)
            ? _pageIndex > 0
            : ReferenceEquals(button, _nextPageButton) &&
              _pageIndex + 1 < _pageCount;

    private void ConfigurePageButton(Border button, int delta)
    {
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressedPageButton = button;
            e.Handled = true;
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var activate = ReferenceEquals(_pressedPageButton, button);
            _pressedPageButton = null;
            var nextPage = _pageIndex + delta;
            if (activate && _pageCount > 1 && nextPage >= 0 && nextPage < _pageCount)
            {
                _controller.ChangeEdgeCapsuleQueuePage(
                    _queueMonitorDeviceName,
                    _queueEdge,
                    delta);
            }

            ClearCapsuleInteractionKeyboardFocus();
            e.Handled = true;
        };
    }

    private bool IsPageNavigationSource(object source)
    {
        for (var current = source as DependencyObject;
             current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _previousPageButton) ||
                ReferenceEquals(current, _nextPageButton))
            {
                return true;
            }

            if (ReferenceEquals(current, _pill))
            {
                break;
            }
        }

        return false;
    }

    public void UpdateTheme()
    {
        // Pill background is always the opaque PaperBrush; the hover tint lives on the overlay.
        _pill.Background = Theme.PaperBrush;
        _pill.BorderBrush = Theme.PaperBorderBrush;
        _hoverOverlay.Background = _isHovering ? Theme.HoverBrush : Brushes.Transparent;
        _glyph.Foreground = Theme.TextBrush;
        _label.Foreground = Theme.WeakTextBrush;
        _previousPageButton.Background = _previousPageButton.IsMouseOver &&
            CanNavigateFromPageButton(_previousPageButton)
            ? Theme.HoverBrush
            : Brushes.Transparent;
        _nextPageButton.Background = _nextPageButton.IsMouseOver &&
            CanNavigateFromPageButton(_nextPageButton)
            ? Theme.HoverBrush
            : Brushes.Transparent;
        _previousPageGlyph.Foreground = Theme.TextBrush;
        _nextPageGlyph.Foreground = Theme.TextBrush;
        _pageLabel.Foreground = Theme.WeakTextBrush;
    }

    public void UpdateTypography()
    {
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(12);
        Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(this);
        _glyph.FontFamily = AppTypography.SymbolFontFamily;
        _glyph.FontSize = MasterGlyphFontSize;
        _previousPageGlyph.FontFamily = AppTypography.SymbolFontFamily;
        _previousPageGlyph.FontSize = MasterGlyphFontSize;
        _nextPageGlyph.FontFamily = AppTypography.SymbolFontFamily;
        _nextPageGlyph.FontSize = MasterGlyphFontSize;
        _label.FontFamily = MasterLabelFontFamily;
        _label.FontSize = MasterLabelFontSize;
        _label.FontWeight = MasterLabelFontWeight;
        AppTypography.ApplyTextRendering(_label);
        _pageLabel.FontFamily = MasterLabelFontFamily;
        _pageLabel.FontSize = MasterLabelFontSize;
        _pageLabel.FontWeight = MasterLabelFontWeight;
        AppTypography.ApplyTextRendering(_pageLabel);
        MoveToTarget(animate: false);
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
    }

    public void UpdateExperimentalOpacity()
    {
        if (_controller.AreAdvancedMasterCapsulesTransparent)
        {
            _pill.Opacity = _controller.AdvancedShortcutOpacity;
            return;
        }

        var enabled =
            _controller.State.ExperimentalRestingCapsuleOpacity &&
            _controller.State.ExperimentalRestingCapsuleOpacityIncludesMaster;
        var restingOpacity = enabled
            ? ExperimentalOpacityLevels.Normalize(
                _controller.State.ExperimentalRestingCapsuleOpacityLevel,
                ExperimentalOpacityLevels.DefaultRestingCapsule)
            : 1.0;
        var interactive = _isHovering || _gestureState != MasterGestureState.Idle;
        _pill.Opacity = enabled &&
            (_controller.State.ExperimentalRestingCapsuleOpacityAlways || !interactive)
                ? restingOpacity
                : 1.0;
    }

    internal bool TryMoveToVirtualDesktop(
        VirtualDesktopAdapter adapter,
        Guid desktopId)
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle == IntPtr.Zero ||
            adapter.TryMoveWindowToDesktop(handle, desktopId);
    }

    public void RefreshEffectiveTopmost()
    {
        var avoidanceWindow = _controller.FullscreenAvoidanceWindowForQueue(
            _queueMonitorDeviceName);
        var topmost = !_experimentalPassive &&
            !_controller.State.ExperimentalDockedCapsulesNonTopmost &&
            avoidanceWindow == IntPtr.Zero &&
            !_controller.SuppressDeepCapsuleTopmostForContextMenu;
        Topmost = topmost;
        if (IsVisible)
        {
            if (_experimentalPassive)
            {
                WindowNative.ApplyBottomZOrder(this);
            }
            else
            {
                WindowNative.ApplyTopmostZOrder(this, topmost, avoidanceWindow);
            }
        }
    }

    public void SetExperimentalPassive(bool enabled)
    {
        if (_isClosingForReal || _experimentalPassive == enabled)
        {
            return;
        }

        if (enabled)
        {
            FinishMasterGesture(commit: false, clearFocus: true);
        }

        _experimentalPassive = enabled;
        WindowNative.SetInputPassthrough(this, enabled);
        RefreshEffectiveTopmost();
    }

    // count = total papers in this queue; retracted = whether they are collapsed behind the
    // master. Pagination only changes which real capsules occupy the visible slots.
    public void UpdateState(
        int count,
        bool retracted,
        bool animate,
        int pageIndex,
        int pageCount,
        int visibleSlotCount,
        double topOffsetDip,
        bool collapseEnabled)
    {
        SetPresentationState(
            count,
            retracted,
            pageIndex,
            pageCount,
            visibleSlotCount,
            topOffsetDip,
            collapseEnabled);

        MoveToTarget(animate);
        RefreshEffectiveTopmost();
    }

    private void SetPresentationState(
        int count,
        bool retracted,
        int pageIndex,
        int pageCount,
        int visibleSlotCount,
        double topOffsetDip,
        bool collapseEnabled)
    {
        _count = Math.Max(0, count);
        _active = retracted;
        _collapseEnabled = collapseEnabled;
        _pageCount = Math.Max(1, pageCount);
        _pageIndex = Math.Clamp(pageIndex, 0, _pageCount - 1);
        _visibleSlotCount = Math.Max(0, visibleSlotCount);
        _topOffsetDip = double.IsFinite(topOffsetDip) ? topOffsetDip : 0;
        ApplyStateVisuals();
    }

    private void ApplyStateVisuals()
    {
        var showPagination = _pageCount > 1;
        _glyph.Visibility = _collapseEnabled ? Visibility.Visible : Visibility.Collapsed;
        _label.Visibility = _collapseEnabled ? Visibility.Visible : Visibility.Collapsed;
        _previousPageButton.Visibility = showPagination ? Visibility.Visible : Visibility.Collapsed;
        _nextPageButton.Visibility = showPagination ? Visibility.Visible : Visibility.Collapsed;
        _pageLabel.Visibility = showPagination ? Visibility.Visible : Visibility.Collapsed;

        _glyph.Text = _active ? "▸" : "▾";
        _label.Text = _active
            ? string.Format(CultureInfo.CurrentUICulture, Strings.Get("CapsuleCollapseAllCountFormat"), _count)
            : Strings.Get("CapsuleCollapseAllLabel");
        _pageLabel.Text = string.Format(
            CultureInfo.CurrentUICulture,
            "{0}/{1}",
            _pageIndex + 1,
            _pageCount);
        _previousPageButton.ToolTip = Strings.Get("CapsulePreviousPageTip");
        _nextPageButton.ToolTip = Strings.Get("CapsuleNextPageTip");
        _previousPageButton.Opacity = _pageIndex > 0 ? 1 : 0.35;
        _nextPageButton.Opacity = _pageIndex + 1 < _pageCount ? 1 : 0.35;
        if (_pageIndex == 0)
        {
            _previousPageButton.Background = Brushes.Transparent;
        }
        if (_pageIndex + 1 >= _pageCount)
        {
            _nextPageButton.Background = Brushes.Transparent;
        }
        _previousPageButton.Cursor = _pageIndex > 0
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        _nextPageButton.Cursor = _pageIndex + 1 < _pageCount
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        _pill.ToolTip = _collapseEnabled
            ? _active
                ? Strings.Get("CapsuleCollapseAllCollapsedTip")
                : Strings.Get("CapsuleCollapseAllExpandedTip")
            : null;
    }

    private void SetHover(bool hovering)
    {
        // Hover only changes the pill background (handled in the MouseEnter/Leave handlers);
        // the master pill does not move, so there is nothing to reposition here.
        _isHovering = hovering;
        UpdateExperimentalOpacity();
    }

    private bool FinishMasterGesture(bool commit, bool clearFocus = true)
    {
        var session = _dragSession;
        var wasDragging = _gestureState == MasterGestureState.Dragging && session != null;
        _gestureState = MasterGestureState.Idle;
        _dragSession = null;
        var hadCapture = _pill.IsMouseCaptured;
        if (_pill.IsMouseCaptured)
        {
            _pill.ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            // Live movement updates the queue immediately so the stack follows the pointer.
            // Only the explicit MouseUp path persists that value; every other exit restores the
            // session snapshot before the autosave timer can make the preview authoritative.
            _controller.SetDeepCapsuleStartTopMargin(
                _queueMonitorDeviceName,
                _queueEdge,
                commit
                    ? _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge)
                    : session!.StartTopMargin,
                commit);
        }

        if (hadCapture && clearFocus)
        {
            ClearCapsuleInteractionKeyboardFocus();
        }

        UpdateExperimentalOpacity();
        return wasDragging;
    }

    private void ClearCapsuleInteractionKeyboardFocus()
    {
        WindowNative.ClearCurrentThreadKeyboardFocus();
        Dispatcher.BeginInvoke(
            (Action)WindowNative.ClearCurrentThreadKeyboardFocus,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private double MasterDockedWidth(double pixelsPerDip)
    {
        var contentWidth = 0.0;
        if (_collapseEnabled)
        {
            var glyphWidth = Math.Max(
                MeasureText("▾", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip),
                MeasureText("▸", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip));
            var expandedLabelWidth = MeasureText(
                Strings.Get("CapsuleCollapseAllLabel"),
                MasterLabelFontSize,
                MasterLabelFontWeight,
                MasterLabelFontFamily,
                pixelsPerDip);
            var currentLabelWidth = MeasureText(
                _label.Text,
                MasterLabelFontSize,
                MasterLabelFontWeight,
                MasterLabelFontFamily,
                pixelsPerDip);
            contentWidth = glyphWidth + MasterGlyphGap + Math.Max(expandedLabelWidth, currentLabelWidth);
        }

        var pageButtonWidth = 0.0;
        if (_pageCount > 1)
        {
            var pageLabelWidth = MeasureText(
                string.Format(CultureInfo.CurrentUICulture, "{0}/{1}", _pageCount, _pageCount),
                MasterLabelFontSize,
                MasterLabelFontWeight,
                MasterLabelFontFamily,
                pixelsPerDip);
            if (_collapseEnabled)
            {
                contentWidth += MasterGlyphGap;
            }

            contentWidth += pageLabelWidth;
            pageButtonWidth =
                MeasureText("‹", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip) +
                MeasureText("›", MasterGlyphFontSize, FontWeights.SemiBold, AppTypography.SymbolFontFamily, pixelsPerDip) +
                (PageButtonHorizontalPadding * 4);
        }

        var bodyWidth = Math.Ceiling(
            MasterLeftPadding +
            contentWidth +
            MasterRightPadding +
            pageButtonWidth +
            MasterInteriorBorderThickness);
        return Math.Max(1, bodyWidth + WindowChromeMargin);
    }

    internal double DesiredDockedWidth
    {
        get
        {
            var pixelsPerDip = WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry)
                    ? geometry.DpiScaleY
                    : VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return MasterDockedWidth(pixelsPerDip);
        }
    }

    // Mirror one real docked tag. The wall side is square and has no transparent margin; the
    // interior side owns its rounded cap and margin inside the actual top-level window bounds.
    private void ApplyMasterEdgeLayout()
    {
        var leftEdge = _queueEdge == EdgeCapsuleEdge.Left;
        var radius = EdgeCapsuleLayout.CornerRadius;
        var edgeCorner = leftEdge
            ? new CornerRadius(0, radius, radius, 0)
            : new CornerRadius(radius, 0, 0, radius);

        _pill.Margin = leftEdge
            ? new Thickness(0, WindowChromeMargin, WindowChromeMargin, WindowChromeMargin)
            : new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
        _pill.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pill.Width = double.NaN;
        _pill.CornerRadius = edgeCorner;
        _pill.BorderThickness = leftEdge
            ? new Thickness(0, MasterInteriorBorderThickness, MasterInteriorBorderThickness, MasterInteriorBorderThickness)
            : new Thickness(MasterInteriorBorderThickness, MasterInteriorBorderThickness, 0, MasterInteriorBorderThickness);
        _hoverOverlay.CornerRadius = edgeCorner;

        _headerStack.HorizontalAlignment = leftEdge
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        _contentStack.HorizontalAlignment = leftEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _contentStack.Margin = leftEdge
            ? new Thickness(MasterRightPadding, 0, MasterLeftPadding, 0)
            : new Thickness(MasterLeftPadding, 0, MasterRightPadding, 0);

        _contentStack.Children.Clear();
        _label.Margin = leftEdge
            ? new Thickness(0, 0, MasterGlyphGap, 0)
            : new Thickness(MasterGlyphGap, 0, 0, 0);
        _pageLabel.Margin = _collapseEnabled
            ? leftEdge
                ? new Thickness(0, 0, MasterGlyphGap, 0)
                : new Thickness(MasterGlyphGap, 0, 0, 0)
            : new Thickness(0);
        if (leftEdge)
        {
            _contentStack.Children.Add(_pageLabel);
            _contentStack.Children.Add(_label);
            _contentStack.Children.Add(_glyph);
        }
        else
        {
            _contentStack.Children.Add(_glyph);
            _contentStack.Children.Add(_label);
            _contentStack.Children.Add(_pageLabel);
        }
    }

    private static double MeasureText(
        string text,
        double fontSize,
        FontWeight weight,
        FontFamily fontFamily,
        double pixelsPerDip)
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
                new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                Theme.WeakTextBrush,
                null,
                AppTypography.TextFormattingMode,
                pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    public void SetQueue(EdgeCapsuleEdge queueEdge, string queueMonitorDeviceName)
    {
        _queueEdge = queueEdge;
        _queueMonitorDeviceName = queueMonitorDeviceName ?? "";
    }

    private double QueueStartTopMargin =>
        _controller.DeepCapsuleStartTopMarginForQueue(_queueMonitorDeviceName, _queueEdge);

    private int QueueSlotCount => Math.Max(1, _visibleSlotCount + 1);

    private double QueueHeaderTop(Rect localWorkArea) =>
        EdgeCapsuleLayout.TopForIndex(
            0,
            QueueStartTopMargin,
            localWorkArea,
            QueueSlotCount,
            _controller.DeepCapsuleGap) + _topOffsetDip;

    private void MoveToTarget(bool animate)
    {
        if (_isClosingForReal)
        {
            return;
        }

        var moveGeneration = ++_moveGeneration;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry))
        {
            ScheduleMasterSettle(moveGeneration);
            return;
        }

        animate = animate && _controller.State.EnableAnimations;
        var localArea = geometry.LocalWorkAreaDip;
        var requestedWidth = MasterDockedWidth(geometry.DpiScaleY);
        var targetTop = QueueHeaderTop(localArea);
        var currentTop = double.IsNaN(_currentTopDip) ? targetTop : _currentTopDip;

        ApplyMasterDeviceBounds(currentTop, requestedWidth, geometry);
        if (!animate || Math.Abs(currentTop - targetTop) < 0.5)
        {
            _animatedMonitorGeometry = null;
            BeginAnimation(AnimatedTopProperty, null);
            ApplyMasterDeviceBounds(targetTop, requestedWidth, geometry);
            ScheduleMasterSettle(moveGeneration);
            return;
        }

        // One animation is one monitor-space transaction. A display/DPI refresh starts a new
        // generation; individual frames never re-enumerate screens or switch scale mid-flight.
        _animatedMonitorGeometry = geometry;
        _animatedWidthDip = requestedWidth;
        var topAnim = new DoubleAnimation
        {
            From = currentTop,
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(EdgeCapsuleLayout.SlotMoveMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        topAnim.Completed += (_, _) =>
        {
            if (moveGeneration != _moveGeneration)
            {
                return;
            }

            _animatedMonitorGeometry = null;
            BeginAnimation(AnimatedTopProperty, null);
            ApplyMasterDeviceBounds(targetTop, requestedWidth, geometry);
            ScheduleMasterSettle(moveGeneration);
        };
        BeginAnimation(AnimatedTopProperty, topAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ScheduleMasterSettle(int moveGeneration, int pass = 0)
    {
        // Windows can move an HWND again while completing WM_DPICHANGED/display removal. Resolve
        // the queue once more after WPF's layout work, guarded by the existing move generation.
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_isClosingForReal || moveGeneration != _moveGeneration ||
                    !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                        _queueMonitorDeviceName,
                        this,
                        out var geometry))
                {
                    return;
                }

                var targetTop = QueueHeaderTop(geometry.LocalWorkAreaDip);
                ApplyMasterDeviceBounds(
                    targetTop,
                    MasterDockedWidth(geometry.DpiScaleY),
                    geometry);
                if (pass == 0)
                {
                    // Display removal can deliver a second WPF/native rewrite after the first idle
                    // turn. One guarded follow-up keeps slot 0 on the same settle depth as its queue.
                    ScheduleMasterSettle(moveGeneration, pass: 1);
                }
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void ApplyMasterDeviceBounds(double topDip)
    {
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                _queueMonitorDeviceName,
                this,
                out var geometry))
        {
            return;
        }

        ApplyMasterDeviceBounds(topDip, MasterDockedWidth(geometry.DpiScaleY), geometry);
    }

    private void ApplyMasterDeviceBounds(
        double topDip,
        double widthDip,
        MonitorGeometry geometry)
    {
        if (_isClosingForReal)
        {
            return;
        }

        ApplyMasterEdgeLayout();
        var layout = EdgeCapsuleGeometry.Calculate(new EdgeCapsuleGeometryInput(
            geometry,
            _queueEdge,
            topDip,
            widthDip,
            0,
            PaperLayoutDefaults.CapsuleHeight));
        if (WindowNative.TrySetWindowDeviceBounds(this, layout.Bounds))
        {
            _currentTopDip = topDip;
        }
    }

    // Local target-monitor DIP. Real capsule hosts use the same coordinate space.
    public double AnchorTop => QueueHeaderTop(
        EdgeCapsuleLayout.LocalWorkAreaForQueue(_queueMonitorDeviceName));

    private static void OnAnimatedTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MasterCapsuleWindow w &&
            !w._isClosingForReal &&
            e.NewValue is double top &&
            !double.IsNaN(top) &&
            !double.IsInfinity(top))
        {
            if (w._animatedMonitorGeometry is MonitorGeometry geometry)
            {
                w.ApplyMasterDeviceBounds(top, w._animatedWidthDip, geometry);
            }
            else
            {
                w.ApplyMasterDeviceBounds(top);
            }
        }
    }
    // First-time show: position at the final edge-aligned spot BEFORE becoming visible,
    // then fade in. This avoids both the top-left flash and the slide-in from the wrong place.
    public void ShowPlaced(
        int count,
        bool retracted,
        bool animate,
        int pageIndex,
        int pageCount,
        int visibleSlotCount,
        double topOffsetDip,
        bool collapseEnabled)
    {
        SetPresentationState(
            count,
            retracted,
            pageIndex,
            pageCount,
            visibleSlotCount,
            topOffsetDip,
            collapseEnabled);

        MoveToTarget(animate: false);
        Show();
        MoveToTarget(animate: false);
        RefreshEffectiveTopmost();

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }

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
        if (_isClosingForReal)
        {
            return;
        }

        _isClosingForReal = true;
        FinishMasterGesture(commit: false, clearFocus: false);
        _contextMenuSession.Dispose();
        ++_moveGeneration;
        _animatedMonitorGeometry = null;
        BeginAnimation(AnimatedTopProperty, null);
        Close();
    }

    private bool IsPointInsideMasterOwnerSurface(System.Windows.Point screenPoint) =>
        DeepCapsuleContextMenuSession.IsPointInsideElement(_pill, screenPoint);

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest && _experimentalPassive)
        {
            handled = true;
            return HtTransparent;
        }

        if (msg is WmDpiChanged or WmDisplayChange or WmSettingChange)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            _controller.ScheduleDisplayMetricsRefresh();
        }

        return IntPtr.Zero;
    }
}
