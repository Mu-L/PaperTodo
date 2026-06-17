using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PaperTodo;

public sealed partial class PaperWindow : Window
{
    [GeneratedRegex(@"^\s*[-*+]\s+\[(?: |x|X)\]\s*")]
    private static partial Regex TodoCheckboxCleanRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+")]
    private static partial Regex TodoBulletCleanRegex();

    [GeneratedRegex(@"^\s*\d+[\.)、．]\s*")]
    private static partial Regex TodoNumberCleanRegex();

    [GeneratedRegex(@"^\s*[☐☑✓✔]\s*")]
    private static partial Regex TodoGlyphCleanRegex();
    private readonly PaperData _paper;
    private readonly AppController _controller;

    private Grid _windowHost = null!;
    private Border _paperChrome = null!;
    private readonly Grid _containerGrid = new();
    private readonly Grid _shell = new();
    private readonly ScaleTransform _shellScale = new(1.0, 1.0);
    private Canvas? _dragLayer;
    private StackPanel? _todoPanel;
    private Button? _paperIconButton;
    private Button? _newTodoButton;
    private Button? _newNoteButton;
    private Button? _openMarkdownButton;
    private Button? _linkNoteButton;
    private TextBlock? _titleText;
    private TextBox? _titleEditBox;
    private TextBlock? _textZoomIndicator;
    private UIElement? _noteBodyElement;
    private Border? _capsuleLeftArea;
    private Border? _activeDropRow;
    private Border? _dropIndicatorLine;
    private Border? _appendArea;
    private Border? _linkedNoteDropRow;
    private bool _closeForReal;
    private string? _pendingFocusItemId;
    private readonly Dictionary<string, TodoTextBox> _todoEditors = new();
    private readonly List<Border> _todoRows = new();
    private TodoDragState? _todoDrag;
    private NoteLinkDragState? _noteLinkDrag;
    private MarkdownTextBox? _noteBox;
    private Action? _showNotePreview;
    private readonly List<List<PaperItem>> _undoStack = new();
    private readonly List<List<PaperItem>> _redoStack = new();
    private const int MaxUndoDepth = 100;
    private string? _activeOriginalItemId;
    private string? _activeOriginalText;
    private bool _suppressTodoBackspaceUntilKeyUp;
    private bool _isApplyingCollapsedState;
    private Button? _closeButton;
    private Grid _capsuleShell = null!;
    private Window? _deepCapsuleSlotHost;
    private Grid? _deepCapsuleSlotHostRoot;
    private Border? _deepCapsuleSlotChrome;
    private Border? _deepCapsuleSlotOutline;
    private Grid? _deepCapsuleSlotShell;
    private TextBlock? _deepCapsuleSlotIconText;
    private Border? _deepCapsuleSlotCloseArea;
    private TextBlock? _deepCapsuleSlotCloseGlyph;
    private TranslateTransform? _deepCapsuleSlotCloseGlyphOffset;
    private TextBlock? _deepCapsuleSlotLabelText;
    private ContextMenu? _deepCapsuleSlotContextMenu;
    private IntPtr _deepCapsuleForegroundHook;
    private IntPtr _deepCapsuleMouseHook;
    private WinEventDelegate? _deepCapsuleForegroundHookProc;
    private LowLevelMouseProc? _deepCapsuleMouseHookProc;
    private Border? _capsuleCloseArea;
    private TextBlock? _capsuleIconText;
    private TextBlock? _capsuleCloseGlyph;
    private TranslateTransform? _capsuleCloseGlyphOffset;
    private TextBlock _capsuleLabelText = null!;
    private bool _isMaybeDragging;
    private Point _mouseDownScreenPos;
    private bool _suppressGeometrySave;
    private DeepCapsuleSlotState _deepCapsuleSlotState = DeepCapsuleSlotState.None;
    private DeepCapsuleVisualState _deepCapsuleVisualState = DeepCapsuleVisualState.Resting;
    private DeepCapsuleGestureState _deepCapsuleGestureState = DeepCapsuleGestureState.Idle;
    private DeepCapsuleOpenOrigin _deepCapsuleOpenOrigin = DeepCapsuleOpenOrigin.Normal;
    private bool _isCollapseAllRetracted;
    private double _deepCapsuleDragMouseOffsetY;
    private double _deepCapsuleDragLeft;
    private double _deepCapsuleSlotLeft;
    private double _deepCapsuleSlotTop;
    private int _deepCapsuleIndex = -1;
    // Visual slot shift: when the "collapse-all" master capsule occupies slot 0, real
    // capsules render at slot index+offset while _deepCapsuleIndex stays the paper-list index.
    private int _deepCapsuleVisualOffset;
    // Monotonic token guarding deep-capsule move animations; a superseded animation's
    // Completed handler bails when this no longer matches the value captured at its start.
    private int _deepCapsuleMoveGeneration;
    private int _deepCapsuleSlotMoveGeneration;
    private int _collapseTransitionGeneration;
    private double _deepCapsuleSlotTargetLeft;
    private double _deepCapsuleSlotStartViewportWidth;
    private double _deepCapsuleSlotTargetViewportWidth;
    private Point _deepCapsuleSlotMouseDownScreenPos;
    private double _startTransitionWidth;
    private double _startTransitionHeight;
    private double _targetTransitionWidth;
    private double _targetTransitionHeight;
    private double _transitionBaseWidth;
    private double _transitionBaseHeight;
    private bool _isTransitionVisualsActive;
    private bool _isEditingTitle;
    private bool _pendingTitleEdit;
    private int _themeAnimationGeneration;
    private int _clearDoneGeneration;
    private int _todoRowsGeneration;
    private const double DeepCapsuleHoverOutsideOffset = DeepCapsuleLayout.HoverOutsideOffset;
    private const double DeepCapsuleExpandedRightInset = DeepCapsuleLayout.ExpandedRightInset;
    private const double DeepCapsuleTopMargin = DeepCapsuleLayout.TopMargin;
    private const double DeepCapsuleStartTopMargin = DeepCapsuleLayout.StartTopMargin;
    private const double DeepCapsuleGap = DeepCapsuleLayout.Gap;
    private const double WindowChromeMargin = 8;
    private const double WindowChromeInset = WindowChromeMargin * 2;
    private const double TitleBarHeight = PaperLayoutDefaults.TopBarHeight;
    private const int CollapseShellFadeMilliseconds = 70;
    private const int CollapseResizeMilliseconds = 150;
    private const int ExpandAnimationMilliseconds = 220;
    private const double ExpandedChromeCornerRadius = RadiusShell;
    private const double CapsuleChromeCornerRadius = DeepCapsuleLayout.CornerRadius; // 胶囊圆角，自成一套，不纳入圆角阶梯
    private const double CapsuleInnerCornerRadius = DeepCapsuleLayout.CornerRadius;   // 左区 / 关闭按钮的内圆角，与药丸外圆角同档

    // 胶囊态内部度量。布局（leftStack/标签）与宽度计算（CapsuleShellWidth）共用同一组值，
    // 否则二者不一致会让壳体与内容错位。整体偏紧凑，减少图标/文字四周的死白。
    private const double CapsuleNormalMinWidth = 76;
    private const double CapsuleLeftPadding = 6;
    private const double CapsuleIconGap = 4;
    private const double CapsuleCloseWidth = 30;
    private const double CapsuleNormalCloseWidth = 21;
    private const double CapsuleRightPadding = 6;
    private const double CapsuleIconFontSize = 13;
    private const double CapsuleLabelFontSize = 11;
    private const double CapsuleCloseGlyphDeepOffset = -8;
    private const double CapsuleCloseGlyphNormalOffset = -1;
    private const double DeepCapsuleSlotOutlineThickness = 2;
    private const double DeepCapsuleSlotOutlineOverlap = 1;
    private const double DeepCapsuleReorderDragExtraThreshold = 4;
    // Half-hidden (peek) cut-off, measured from the END of the title. Negative pulls the cut
    // INTO the title so the last glyph is roughly half-covered — the capsule reads as clearly
    // tucked away at the edge, yet enough text shows to identify it. ~half a CJK glyph at 11px,
    // less 1px so a sliver of breathing room shows to the right of the text.
    private const double CapsulePeekRightGap = -5;

    // 圆角阶梯：所有元素只从这四档取值，避免散落的随手圆角。
    // 小元素（勾选框）/ 控件（按钮、徽标、行）/ 块（菜单、面板）/ 外壳（纸片、顶栏）。
    private const double RadiusSmall = 4;
    private const double RadiusControl = 8;
    private const double RadiusBlock = 12;
    private const double RadiusShell = 16;
    private static readonly object NoteRenderTraceLock = new();

    public bool IsDeepCapsulePlaced => _paper.IsCollapsed && HasDeepCapsuleSlotPlacement;
    public bool IsDeepCapsuleSlotVisible => _deepCapsuleSlotHost?.IsVisible == true;
    public bool HasVisibleSurface => IsVisible || IsDeepCapsuleSlotVisible;
    public bool IsCollapseAllRetracted => _isCollapseAllRetracted;
    public bool HasExpandedDeepCapsuleSlotReservation => _deepCapsuleSlotState is DeepCapsuleSlotState.ExpandedReserved or DeepCapsuleSlotState.Retracting;
    public bool OccupiesDeepCapsuleSlot => _paper.IsVisible && (_paper.IsCollapsed || _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved);
    public bool SuppressGeometrySave => _suppressGeometrySave;
    public double DesiredCapsuleWindowWidth => CapsuleWindowWidth();
    public double DeepCapsuleRestingVisibleWidth => _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved
        ? ExpandedDeepCapsuleVisibleWidth()
        : DeepCapsuleVisibleWidth();

    private enum TodoFocusPlacement
    {
        End,
        Start
    }

    private enum DeepCapsuleSlotState
    {
        None,
        CollapsedDocked,
        ExpandedReserved,
        Retracting
    }

    private enum DeepCapsuleVisualState
    {
        Resting,
        Hovered,
        Active
    }

    private enum DeepCapsuleGestureState
    {
        Idle,
        PendingClick,
        Reordering
    }

    private enum DeepCapsuleOpenOrigin
    {
        Normal,
        EdgeSlot
    }

    private bool HasDeepCapsuleSlotPlacement => _deepCapsuleSlotState != DeepCapsuleSlotState.None;
    private bool HoldsDeepCapsuleSlotWhileExpanded => _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved;
    private bool IsDeepCapsuleSlotRetracting => _deepCapsuleSlotState == DeepCapsuleSlotState.Retracting;
    private bool IsDeepCapsuleHovered => _deepCapsuleVisualState == DeepCapsuleVisualState.Hovered;
    private bool IsDeepCapsuleSlotActive => _deepCapsuleVisualState == DeepCapsuleVisualState.Active;
    private bool IsDeepCapsuleSlotPendingClick => _deepCapsuleGestureState == DeepCapsuleGestureState.PendingClick;
    private bool IsDeepCapsuleReordering => _deepCapsuleGestureState == DeepCapsuleGestureState.Reordering;
    private bool ExpandedFromDeepCapsuleEdge => _deepCapsuleOpenOrigin == DeepCapsuleOpenOrigin.EdgeSlot;

    private void SetDeepCapsuleSlotState(DeepCapsuleSlotState state) => _deepCapsuleSlotState = state;
    private void SetDeepCapsuleVisualState(DeepCapsuleVisualState state) => _deepCapsuleVisualState = state;
    private void SetDeepCapsuleGestureState(DeepCapsuleGestureState state) => _deepCapsuleGestureState = state;
    private void SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin origin) => _deepCapsuleOpenOrigin = origin;

    private sealed class TodoDragState
    {
        public TodoDragState(string itemId, Border sourceRow, FrameworkElement handle, Point startPoint)
        {
            ItemId = itemId;
            SourceRow = sourceRow;
            Handle = handle;
            StartPoint = startPoint;
        }

        public string ItemId { get; }
        public Border SourceRow { get; }
        public FrameworkElement Handle { get; }
        public Point StartPoint { get; }
        public bool IsDragging { get; set; }
        public string? TargetId { get; set; }
        public DropPlacement TargetPlacement { get; set; } = DropPlacement.After;
        public bool DropAtEnd { get; set; }

        public Border? Ghost { get; set; }
        public Point MouseOffsetInRow { get; set; }
    }

    private sealed class NoteLinkDragState
    {
        public NoteLinkDragState(FrameworkElement handle, Point startScreenPoint)
        {
            Handle = handle;
            StartScreenPoint = startScreenPoint;
        }

        public FrameworkElement Handle { get; }
        public Point StartScreenPoint { get; }
        public bool IsDragging { get; set; }
        public Window? Ghost { get; set; }
    }

    private enum DropPlacement
    {
        Before,
        After
    }

    private static Brush PaperBrush => Theme.PaperBrush;
    private static Brush PaperBorderBrush => Theme.PaperBorderBrush;
    private static Brush TextBrush => Theme.TextBrush;
    private static Brush WeakTextBrush => Theme.WeakTextBrush;
    private static Brush BrightWeakTextBrush => Theme.BrightWeakTextBrush;
    private static Brush HoverBrush => Theme.HoverBrush;
    private static Brush MenuHoverBrush => Theme.HoverBrush;

    // 以下半透明叠加色全部从当前主题的 Tint / Danger 基色派生，
    // 切换配色族（暖纸 / 墨 / 林 / 霞）时自动跟随，无需各自维护 Light/Dark 对。
    private static Brush DropIndicatorBgBrush => Theme.Tint(12);
    private static Brush DropIndicatorBrush => Theme.Tint(180);
    private static Brush AppendDropBrush => Theme.Tint(34);
    private static Brush AppendBorderBrush => Theme.Tint(45);
    private static Brush AppendBgBrush => Theme.Tint(12);
    private static Brush AppendHoverBgBrush => Theme.Tint(26);
    private static Brush NoteLinkTargetBgBrush => Theme.Tint((byte)(Theme.IsDark ? 36 : 28));
    private static Brush NoteLinkTargetBorderBrush => Theme.Tint(150);
    private static Brush LinkedNoteBgBrush => Theme.Tint((byte)(Theme.IsDark ? 28 : 18));
    private static Brush LinkedNoteHoverBgBrush => Theme.Tint((byte)(Theme.IsDark ? 48 : 34));

    private static Brush CheckBoxBorderBrush => Theme.CheckBoxBorderBrush;

    private static Brush TrashBgBrush => Theme.Danger((byte)(Theme.IsDark ? 16 : 12));
    private static Brush TrashBorderBrush => Theme.Danger(50);
    private static Brush TrashTextBrush => Theme.DangerBrush;
    private static Brush TrashHoverBgBrush => Theme.Danger((byte)(Theme.IsDark ? 32 : 26));
    private static Brush TrashHoverBorderBrush => Theme.DangerBrush;

    private static Brush TitleBarBrush => Theme.Tint((byte)(Theme.IsDark ? 18 : 12));
    private static Brush TitleBarDividerBrush => Theme.Tint((byte)(Theme.IsDark ? 34 : 28));
    private const int TodoMoveAnimationMilliseconds = 150;

    private static readonly ControlTemplate SharedContextMenuTemplate = BuildContextMenuTemplate();
    private static readonly Style SharedCompactMenuItemStyle = BuildCompactMenuItemStyle();
    private static readonly Style SharedIconButtonStyle = BuildIconButtonStyle();
    private static readonly Style SharedCheckBoxStyle = BuildCustomCheckBoxStyle();

    private static ControlTemplate BuildContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusBlock));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = border
        };
    }

    private static Style BuildCompactMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));

        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 10, 4)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrushKey")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };

        var hover = new Trigger
        {
            Property = WpfMenuItem.IsHighlightedProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey"), "Bd"));

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));

        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildIconButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("WeakTextBrushKey")));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.FocusableProperty, false));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var mouseOver = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("HoverBrushKey")));
        mouseOver.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrushKey")));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private static Style BuildCustomCheckBoxStyle()
    {
        var style = new Style(typeof(CheckBox));

        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 16.0));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 16.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));

        var grid = new FrameworkElementFactory(typeof(Grid));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "CheckBorder";
        border.SetValue(FrameworkElement.WidthProperty, 16.0);
        border.SetValue(FrameworkElement.HeightProperty, 16.0);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
        border.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("CheckBoxBorderBrushKey"));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        grid.AppendChild(border);

        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,7.5 L 6.5,11 L 13,4"));
        path.SetValue(System.Windows.Shapes.Path.StrokeProperty, new DynamicResourceExtension("PaperBrushKey"));
        path.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
        path.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeLineJoinProperty, PenLineJoin.Round);
        path.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        path.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        grid.AppendChild(path);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = grid
        };

        var checkedTrigger = new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true
        };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxActiveBrushKey"), "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));

        var hoverTrigger = new MultiTrigger();
        hoverTrigger.Conditions.Add(new Condition { Property = UIElement.IsMouseOverProperty, Value = true });
        hoverTrigger.Conditions.Add(new Condition { Property = ToggleButton.IsCheckedProperty, Value = false });
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("CheckBoxUncheckedHoverBorderBrushKey"), "CheckBorder"));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxUncheckedHoverBgKey"), "CheckBorder"));

        var hoverCheckedTrigger = new MultiTrigger();
        hoverCheckedTrigger.Conditions.Add(new Condition { Property = UIElement.IsMouseOverProperty, Value = true });
        hoverCheckedTrigger.Conditions.Add(new Condition { Property = ToggleButton.IsCheckedProperty, Value = true });
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CheckBoxActiveHoverBrushKey"), "CheckBorder"));
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        hoverCheckedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));

        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(hoverCheckedTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    public PaperWindow(PaperData paper, AppController controller)
    {
        _paper = paper;
        _controller = controller;

        ConfigureWindow();
        BuildShell();
        UpdateToolTipSetting();

        Loaded += (_, _) => SaveGeometryIfAllowed();
        LocationChanged += (_, _) => SaveGeometryIfAllowed();
        SizeChanged += (_, _) => SaveGeometryIfAllowed();
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseWheel += OnWindowPreviewMouseWheel;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        Activated += (_, _) => _controller.RefreshFloatingSurfaceZOrder();
        Deactivated += (_, _) =>
        {
            if (_todoDrag != null)
            {
                EndTodoMouseDrag(commit: false);
            }

            if (_noteLinkDrag != null)
            {
                EndNoteLinkMouseGesture(commit: false);
            }
        };
        Closing += OnClosing;

        if (_paper.Type == PaperTypes.Note)
        {
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_noteBox != null && _noteBox.IsFocused)
                {
                    var clicked = e.OriginalSource as DependencyObject;
                    if (!IsDescendantOf(clicked, _noteBox))
                    {
                        ExitNoteEditor();
                    }
                }
            };
        }
    }

    public void CloseForReal()
    {
        CloseExpandedDeepCapsuleSlotHostForReal();
        _closeForReal = true;
        Close();
    }

    public void UpdateToolTipSetting()
    {
        ToolTipPreferences.Apply(this, _controller.State.EnableToolTips);
        ToolTipPreferences.Apply(_deepCapsuleSlotHost, _controller.State.EnableToolTips);
    }

    public void CancelPendingVisibilityTransitions()
    {
        BeginAnimation(Window.OpacityProperty, null);
        if (!_isCollapseAllRetracted)
        {
            Opacity = 1.0;
        }

        _deepCapsuleSlotMoveGeneration++;
        if (_deepCapsuleSlotHost != null)
        {
            _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, null);
            _deepCapsuleSlotHost.Opacity = 1.0;
        }

        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHostRoot.Opacity = 1.0;
        }

        if (IsDeepCapsuleSlotRetracting)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            if (!_paper.IsCollapsed &&
                _controller.State.UseCapsuleMode &&
                _controller.State.UseDeepCapsuleMode &&
                _deepCapsuleSlotHost?.IsVisible == true)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
                UpdateDeepCapsuleSlotClosePlacement();
            }
        }
    }

    private void ConfigureWindow()
    {
        InitializeThemeResources();
        Title = _controller.PaperTitleText(_paper);
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = _paper.X;
        Top = _paper.Y;

        if (_paper.IsCollapsed && _controller.State.UseCapsuleMode)
        {
            Width = CapsuleWindowWidth();
            Height = PaperLayoutDefaults.CapsuleHeight;
            MinWidth = CapsuleWindowWidth();
            MinHeight = PaperLayoutDefaults.CapsuleHeight;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            Width = _paper.Width;
            Height = _paper.Height;
            MinWidth = PaperLayoutDefaults.MinWidth;
            MinHeight = PaperLayoutDefaults.MinHeight;
            ResizeMode = ResizeMode.CanResizeWithGrip;
        }

        RefreshEffectiveTopmost();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    private void InitializeThemeResources()
    {
        Resources["PaperBrushKey"] = PaperBrush;
        Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        Resources["TextBrushKey"] = TextBrush;
        Resources["WeakTextBrushKey"] = WeakTextBrush;
        Resources["HoverBrushKey"] = HoverBrush;
        Resources["DropIndicatorBrushKey"] = DropIndicatorBrush;
        Resources["AppendDropBrushKey"] = AppendDropBrush;
        Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        Resources["TitleBarBrushKey"] = TitleBarBrush;
        Resources["TitleBarDividerBrushKey"] = TitleBarDividerBrush;

        Resources["CheckBoxBorderBrushKey"] = CheckBoxBorderBrush;
        Resources["CheckBoxActiveBrushKey"] = Theme.ActiveBrush;
        Resources["CheckBoxUncheckedHoverBorderBrushKey"] = Theme.CheckBoxHoverBorderBrush;
        Resources["CheckBoxUncheckedHoverBgKey"] = Theme.CheckBoxUncheckedHoverBgBrush;
        Resources["CheckBoxActiveHoverBrushKey"] = Theme.CheckBoxActiveHoverBrush;
    }

    public void UpdateTheme()
    {
        var oldPaperColor = TryGetSolidColor(_paperChrome?.Background, out var capturedPaperColor)
            ? capturedPaperColor
            : (Color?)null;
        var oldBorderColor = TryGetSolidColor(_paperChrome?.BorderBrush, out var capturedBorderColor)
            ? capturedBorderColor
            : (Color?)null;

        _themeAnimationGeneration++;
        var themeAnimationGeneration = _themeAnimationGeneration;

        InitializeThemeResources();

        var canAnimateTheme = _controller.State.EnableAnimations &&
            _paperChrome != null &&
            oldPaperColor.HasValue &&
            oldBorderColor.HasValue &&
            TryGetSolidColor(Resources["PaperBrushKey"] as Brush, out var newPaperColor) &&
            TryGetSolidColor(Resources["PaperBorderBrushKey"] as Brush, out var newBorderColor);

        // 主题动画只能使用临时本地画刷；完成后必须恢复动态资源绑定。
        if (_controller.State.EnableAnimations && _paperChrome != null)
        {
            if (canAnimateTheme)
            {
                var pendingAnimations = 0;

                void MarkThemeAnimationComplete()
                {
                    pendingAnimations--;
                    if (pendingAnimations <= 0 && themeAnimationGeneration == _themeAnimationGeneration)
                    {
                        RestorePaperChromeThemeReferences();
                    }
                }

                pendingAnimations++;
                AnimatePaperChromeBrush(
                    oldPaperColor!.Value,
                    newPaperColor,
                    brush => _paperChrome.Background = brush,
                    MarkThemeAnimationComplete);

                pendingAnimations++;
                AnimatePaperChromeBrush(
                    oldBorderColor!.Value,
                    newBorderColor,
                    brush => _paperChrome.BorderBrush = brush,
                    MarkThemeAnimationComplete);
            }
            else
            {
                RestorePaperChromeThemeReferences();
            }
        }
        else
        {
            RestorePaperChromeThemeReferences();
        }

        RefreshPaperTitle();
        RefreshPaperIconButton();
        UpdateTextZoom();
        UpdateDeepCapsuleSlotHostTheme();

        if (_paper.Type == PaperTypes.Note)
        {
            if (_noteBox != null)
            {
                _noteBox.RefreshVisualStyle();
            }

        }
        else
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }
    }

    private static bool TryGetSolidColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush solidBrush)
        {
            color = solidBrush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private void AnimatePaperChromeBrush(Color from, Color to, Action<SolidColorBrush> assignBrush, Action onComplete)
    {
        var transitionBrush = new SolidColorBrush(from);
        assignBrush(transitionBrush);

        var animation = new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = AnimationHelper.SmoothEase
        };
        animation.Completed += (_, _) => onComplete();
        transitionBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void RestorePaperChromeThemeReferences()
    {
        if (_paperChrome == null)
        {
            return;
        }

        _paperChrome.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _paperChrome.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");
    }

    public void UpdateMarkdownRenderMode()
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            var mode = _controller.State.MarkdownRenderMode;
            TraceNoteRender($"UpdateMarkdownRenderMode rebuild mode={mode}");
            RebuildNoteBodyForMarkdownMode();
        }
    }

    private void TraceNoteRender(string message)
    {
#if DEBUG
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "md-render-trace.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} paper={_paper.Id[..Math.Min(6, _paper.Id.Length)]} {message}{Environment.NewLine}";
            lock (NoteRenderTraceLock)
            {
                System.IO.File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Test-only diagnostics must never affect note interaction.
        }
#endif
    }

    private void RebuildNoteBodyForMarkdownMode()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var oldBox = _noteBox;
        var text = oldBox?.Text ?? _paper.Content ?? "";
        var caret = oldBox?.CaretIndex ?? 0;
        var verticalOffset = oldBox?.VerticalOffset ?? 0;
        var horizontalOffset = oldBox?.HorizontalOffset ?? 0;
        _paper.Content = text;

        TraceNoteRender($"RebuildNoteBody start textLength={text.Length} caret={caret} v={verticalOffset:F1} h={horizontalOffset:F1}");

        var oldBodies = new List<UIElement>();
        if (_noteBodyElement != null)
        {
            oldBodies.Add(_noteBodyElement);
        }
        else
        {
            var zoomHost = _textZoomIndicator?.Parent as UIElement;
            foreach (UIElement child in _shell.Children)
            {
                if (Grid.GetRow(child) == 1 && !ReferenceEquals(child, zoomHost))
                {
                    oldBodies.Add(child);
                }
            }
        }

        _noteBox = null;
        _showNotePreview = null;

        var body = BuildNoteBody();
        body.Opacity = 0;
        body.IsHitTestVisible = false;
        Grid.SetRow(body, 1);
        Panel.SetZIndex(body, 1);
        _noteBodyElement = body;
        _shell.Children.Add(body);

        if (_noteBox == null)
        {
            TraceNoteRender("RebuildNoteBody end: no note box");
            return;
        }

        _noteBox.CaretIndex = Math.Clamp(caret, 0, _noteBox.Text.Length);
        _showNotePreview?.Invoke();
        body.UpdateLayout();

        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_noteBox == null)
                {
                    return;
                }

                foreach (var oldBody in oldBodies)
                {
                    _shell.Children.Remove(oldBody);
                }

                body.Opacity = 1;
                body.IsHitTestVisible = true;
                _noteBox.ScrollToHorizontalOffset(horizontalOffset);
                _noteBox.ScrollToVerticalOffset(verticalOffset);
                _showNotePreview?.Invoke();
                TraceNoteRender($"RebuildNoteBody restored caret={_noteBox.CaretIndex} v={verticalOffset:F1} h={horizontalOffset:F1}");
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ExitNoteEditor()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        if (_noteBox.ContextMenu?.IsOpen == true)
        {
            return;
        }

        Keyboard.ClearFocus();
        _showNotePreview?.Invoke();
    }

    private void BuildShell()
    {
        _windowHost = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = false
        };
        Content = _windowHost;

        _paperChrome = new Border
        {
            Margin = new Thickness(WindowChromeMargin),
            CornerRadius = PaperChromeCornerRadiusForState(_paper.IsCollapsed && _controller.State.UseCapsuleMode),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.18
            }
        };
        _paperChrome.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _paperChrome.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");

        _windowHost.Children.Add(_paperChrome);

        _containerGrid.Background = Brushes.Transparent;
        _containerGrid.ClipToBounds = false;
        _containerGrid.RenderTransform = _shellScale;
        _containerGrid.RenderTransformOrigin = new Point(0, 0);
        _paperChrome.Child = _containerGrid;

        _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _containerGrid.Children.Add(_shell);

        BuildTopBar();
        BuildBody();
        BuildDragLayer();

        BuildCapsuleShell();
        AttachCapsuleShellToWindowHost();

        if (_paper.IsCollapsed && _controller.State.UseCapsuleMode)
        {
            _shell.Visibility = Visibility.Collapsed;
            _shell.Opacity = 0;
            _capsuleShell.Visibility = Visibility.Visible;
            _capsuleShell.Opacity = 1;
        }
        else
        {
            _shell.Visibility = Visibility.Visible;
            _shell.Opacity = 1;
            _capsuleShell.Visibility = Visibility.Collapsed;
            _capsuleShell.Opacity = 0;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        UpdateTextZoom();
    }

    private void AttachCapsuleShellToWindowHost()
    {
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.Margin = new Thickness(WindowChromeMargin);
        _capsuleShell.HorizontalAlignment = HorizontalAlignment.Left;
        _capsuleShell.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(_capsuleShell, 10);
        if (!_windowHost.Children.Contains(_capsuleShell))
        {
            _windowHost.Children.Add(_capsuleShell);
        }
    }

    private Window EnsureDeepCapsuleSlotHost()
    {
        if (_deepCapsuleSlotHost != null)
        {
            return _deepCapsuleSlotHost;
        }

        _deepCapsuleSlotHostRoot = new Grid
        {
            Background = null,
            ClipToBounds = true,
            Opacity = 1
        };

        _deepCapsuleSlotChrome = new Border
        {
            Margin = new Thickness(WindowChromeMargin),
            CornerRadius = new CornerRadius(CapsuleChromeCornerRadius),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_deepCapsuleSlotChrome, 0);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotChrome);

        _deepCapsuleSlotShell = BuildDeepCapsuleSlotShell();
        Panel.SetZIndex(_deepCapsuleSlotShell, 10);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotShell);

        _deepCapsuleSlotOutline = new Border
        {
            Margin = new Thickness(WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap),
            CornerRadius = new CornerRadius(CapsuleChromeCornerRadius + DeepCapsuleSlotOutlineThickness - DeepCapsuleSlotOutlineOverlap),
            BorderThickness = new Thickness(DeepCapsuleSlotOutlineThickness),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_deepCapsuleSlotOutline, 20);
        _deepCapsuleSlotHostRoot.Children.Add(_deepCapsuleSlotOutline);

        var host = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Segoe UI"),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = !_controller.SuppressTopmostForFullscreenForeground,
            Width = CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            Height = PaperLayoutDefaults.CapsuleHeight,
            Content = _deepCapsuleSlotHostRoot
        };
        host.SourceInitialized += (_, _) => ApplyNoActivateStyle(host);
        host.Deactivated += (_, _) => CloseDeepCapsuleSlotContextMenu();
        _deepCapsuleSlotHost = host;
        UpdateDeepCapsuleSlotHostTheme();
        return host;
    }

    private Grid BuildDeepCapsuleSlotShell()
    {
        var shell = new Grid
        {
            Width = DeepCapsuleSlotShellLayoutWidth(),
            Height = 30,
            Margin = new Thickness(WindowChromeMargin),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftArea = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };

        var leftStack = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(CapsuleLeftPadding, 0, 0, 0)
        };
        leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _deepCapsuleSlotIconText = new TextBlock
        {
            Text = _paper.Type == PaperTypes.Note ? "✎" : "✓",
            Foreground = BrightWeakTextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleIconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_deepCapsuleSlotIconText, 0);
        leftStack.Children.Add(_deepCapsuleSlotIconText);

        _deepCapsuleSlotLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleLabelFontSize,
            Margin = new Thickness(CapsuleIconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_deepCapsuleSlotLabelText, 1);
        leftStack.Children.Add(_deepCapsuleSlotLabelText);
        leftArea.Child = leftStack;

        leftArea.MouseEnter += (_, _) => leftArea.Background = HoverBrush;
        leftArea.MouseLeave += (_, _) => leftArea.Background = Brushes.Transparent;
        shell.MouseEnter += (_, _) => SetDeepCapsuleHover(true);
        shell.MouseLeave += (_, _) => SetDeepCapsuleHover(false);
        leftArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _deepCapsuleSlotMouseDownScreenPos = DeepCapsuleSlotPointerScreenPosition(e);
            SetDeepCapsuleGestureState(DeepCapsuleGestureState.PendingClick);
            leftArea.CaptureMouse();
            e.Handled = true;
        };
        leftArea.PreviewMouseMove += (_, e) =>
        {
            if (IsDeepCapsuleReordering)
            {
                UpdateDeepCapsuleReorderDrag(DeepCapsuleSlotPointerScreenPosition(e));
                e.Handled = true;
                return;
            }

            if (!IsDeepCapsuleSlotPendingClick)
            {
                return;
            }

            var currentScreenPos = DeepCapsuleSlotPointerScreenPosition(e);
            var deltaX = Math.Abs(currentScreenPos.X - _deepCapsuleSlotMouseDownScreenPos.X);
            var deltaY = Math.Abs(currentScreenPos.Y - _deepCapsuleSlotMouseDownScreenPos.Y);
            if (CanReorderDeepCapsuleSlot())
            {
                if (deltaY >= SystemParameters.MinimumVerticalDragDistance + DeepCapsuleReorderDragExtraThreshold)
                {
                    SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                    StartDeepCapsuleReorderDrag(currentScreenPos);
                    e.Handled = true;
                }

                return;
            }

            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
                deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                leftArea.ReleaseMouseCapture();
            }
        };
        leftArea.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (IsDeepCapsuleReordering)
            {
                EndDeepCapsuleReorderDrag(commit: true);
                leftArea.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (IsDeepCapsuleSlotPendingClick)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
                leftArea.ReleaseMouseCapture();
                ActivateFromDeepCapsuleSlot();
                e.Handled = true;
            }
        };
        leftArea.LostMouseCapture += (_, _) =>
        {
            if (IsDeepCapsuleSlotPendingClick)
            {
                SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
            }
            if (IsDeepCapsuleReordering && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                EndDeepCapsuleReorderDrag(commit: false);
            }
        };
        leftArea.ContextMenu = BuildDeepCapsuleSlotContextMenu();

        Grid.SetColumn(leftArea, 0);
        shell.Children.Add(leftArea);

        var closeGlyphOffset = new TranslateTransform(CapsuleCloseGlyphDeepOffset, 0);
        _deepCapsuleSlotCloseGlyphOffset = closeGlyphOffset;
        _deepCapsuleSlotCloseGlyph = new TextBlock
        {
            Text = "×",
            Foreground = WeakTextBrush,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = closeGlyphOffset
        };

        _deepCapsuleSlotCloseArea = new Border
        {
            Width = CapsuleCloseWidth,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipHideThisPaper"),
            Child = _deepCapsuleSlotCloseGlyph
        };
        _deepCapsuleSlotCloseArea.MouseEnter += (_, _) =>
        {
            leftArea.Background = Brushes.Transparent;
            _deepCapsuleSlotCloseArea.Background = HoverBrush;
            _deepCapsuleSlotCloseGlyph.Foreground = TextBrush;
        };
        _deepCapsuleSlotCloseArea.MouseLeave += (_, _) =>
        {
            _deepCapsuleSlotCloseArea.Background = Brushes.Transparent;
            _deepCapsuleSlotCloseGlyph.Foreground = WeakTextBrush;
            _deepCapsuleSlotCloseArea.Opacity = 1.0;
        };
        _deepCapsuleSlotCloseArea.MouseLeftButtonDown += (_, e) =>
        {
            _deepCapsuleSlotCloseArea.Opacity = 0.72;
            e.Handled = true;
        };
        _deepCapsuleSlotCloseArea.MouseLeftButtonUp += (_, e) =>
        {
            _deepCapsuleSlotCloseArea.Opacity = 1.0;
            _controller.HidePaper(_paper);
            e.Handled = true;
        };

        Grid.SetColumn(_deepCapsuleSlotCloseArea, 1);
        shell.Children.Add(_deepCapsuleSlotCloseArea);

        RefreshDeepCapsuleSlotLabel();
        return shell;
    }

    private Point DeepCapsuleSlotPointerScreenPosition(MouseEventArgs e)
    {
        if (_deepCapsuleSlotShell != null && PresentationSource.FromVisual(_deepCapsuleSlotShell) != null)
        {
            return _deepCapsuleSlotShell.PointToScreen(e.GetPosition(_deepCapsuleSlotShell));
        }

        return PointToScreen(e.GetPosition(this));
    }

    private void ActivateFromDeepCapsuleSlot()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (_paper.IsCollapsed)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false, alignExpandedToRight: true);
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToRightEdge: true);
            _controller.BringPaperToFront(_paper);
        }
    }

    private void ShowMainWindowForDeepCapsuleActivation()
    {
        if (IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        Width = DesiredCapsuleWindowWidth;
        Height = PaperLayoutDefaults.CapsuleHeight;
        if (_deepCapsuleSlotHost != null)
        {
            Left = RoundToDevicePixelX(_deepCapsuleSlotHost.Left);
            Top = RoundToDevicePixelY(_deepCapsuleSlotHost.Top);
        }
        else
        {
            Left = _paper.X;
            Top = _paper.Y;
        }
        Show();
    }

    private void HideMainWindowForDeepCapsuleRest()
    {
        if (!_paper.IsCollapsed || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        BeginAnimation(Window.OpacityProperty, null);
        Opacity = 1.0;
        Hide();
    }

    internal void HideMainWindowForDeepCapsuleMode()
    {
        HideMainWindowForDeepCapsuleRest();
    }

    public void EnsureExpandedSurfaceGeometry(bool alignToRightEdge = false)
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        var needsRestore =
            !IsVisible ||
            _isApplyingCollapsedState ||
            _isTransitionVisualsActive ||
            Width <= DesiredCapsuleWindowWidth + 8 ||
            Height <= PaperLayoutDefaults.CapsuleHeight + 8 ||
            _shell.Visibility != Visibility.Visible ||
            _capsuleShell.Visibility == Visibility.Visible;
        if (!needsRestore)
        {
            return;
        }

        BeginAnimation(TransitionProgressProperty, null);
        _shell.BeginAnimation(UIElement.OpacityProperty, null);
        _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
        ResetTransitionVisuals();

        _isApplyingCollapsedState = false;
        _shell.Width = double.NaN;
        _shell.Height = double.NaN;
        _shell.Visibility = Visibility.Visible;
        _shell.Opacity = 1.0;
        _capsuleShell.Visibility = Visibility.Collapsed;
        _capsuleShell.Opacity = 0.0;
        MinWidth = PaperLayoutDefaults.MinWidth;
        MinHeight = PaperLayoutDefaults.MinHeight;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var targetWidth = RoundToDevicePixelX(Math.Max(_paper.Width, PaperLayoutDefaults.MinWidth));
        var targetHeight = RoundToDevicePixelY(Math.Max(_paper.Height, PaperLayoutDefaults.MinHeight));
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = targetWidth;
            Height = targetHeight;
            if (alignToRightEdge)
            {
                var requiredRightInset = _controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper)
                    ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                    : 0;
                AlignExpandedToRightEdge(targetWidth, targetHeight, requiredRightInset);
            }
        });

        if (!IsVisible)
        {
            Opacity = 1.0;
            Show();
        }

        RefreshEffectiveTopmost();
    }

    public void ExpandForProgrammaticOpen()
    {
        if (!_paper.IsCollapsed)
        {
            EnsureExpandedSurfaceGeometry(alignToRightEdge: true);
            return;
        }

        if (_controller.State.UseCapsuleMode &&
            _controller.State.UseDeepCapsuleMode &&
            HasDeepCapsuleSlotPlacement)
        {
            ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(false);
            return;
        }

        if (!IsVisible)
        {
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 1.0;
            Left = _paper.X;
            Top = _paper.Y;
            Width = DesiredCapsuleWindowWidth;
            Height = PaperLayoutDefaults.CapsuleHeight;
            Show();
        }

        SetCollapsedState(false);
    }

    private void UpdateDeepCapsuleSlotHostTheme()
    {
        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Background = PaperBrush;
            _deepCapsuleSlotChrome.BorderBrush = PaperBorderBrush;
        }

        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.BorderBrush = Theme.CapsuleFocusBorderBrush;
            _deepCapsuleSlotOutline.Visibility = IsDeepCapsuleSlotActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_deepCapsuleSlotLabelText != null)
        {
            _deepCapsuleSlotLabelText.Foreground = WeakTextBrush;
        }

        if (_deepCapsuleSlotIconText != null)
        {
            _deepCapsuleSlotIconText.Foreground = BrightWeakTextBrush;
        }

        if (_deepCapsuleSlotCloseGlyph != null)
        {
            _deepCapsuleSlotCloseGlyph.Foreground = WeakTextBrush;
        }

    }

    private void MoveExpandedDeepCapsuleSlotHost(
        double targetLeft,
        double targetTop,
        double visibleWidth,
        bool animate,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false)
    {
        var host = EnsureDeepCapsuleSlotHost();
        var rightEdge = targetLeft + visibleWidth;
        var viewportWidth = visibleWidth;
        var targetHostLeft = RoundToDevicePixelX(rightEdge - viewportWidth);
        host.Height = PaperLayoutDefaults.CapsuleHeight;
        if (!keepHiding)
        {
            if (IsDeepCapsuleSlotRetracting)
            {
                SetDeepCapsuleSlotState(_paper.IsCollapsed
                    ? DeepCapsuleSlotState.CollapsedDocked
                    : DeepCapsuleSlotState.None);
            }
        }
        _deepCapsuleSlotTop = targetTop;
        if (_deepCapsuleSlotHostRoot != null)
        {
            _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHostRoot.Opacity = 1;
            _deepCapsuleSlotHostRoot.IsHitTestVisible = !keepHiding;
        }

        if (!host.IsVisible)
        {
            host.BeginAnimation(Window.OpacityProperty, null);
            host.Left = targetHostLeft;
            host.Top = targetTop;
            ApplyDeepCapsuleSlotHostViewport(viewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
            host.Opacity = _isCollapseAllRetracted ? 0 : 1;
            host.Show();
            RefreshEffectiveTopmost();
            return;
        }

        host.BeginAnimation(Window.OpacityProperty, null);
        if (!_isCollapseAllRetracted)
        {
            host.Opacity = 1;
        }

        var generation = ++_deepCapsuleSlotMoveGeneration;
        if (!animate)
        {
            host.BeginAnimation(Window.LeftProperty, null);
            host.BeginAnimation(Window.TopProperty, null);
            ClearDeepCapsuleSlotHorizontalAnimation();
            host.Left = targetHostLeft;
            host.Top = targetTop;
            ApplyDeepCapsuleSlotHostViewport(viewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
            _deepCapsuleSlotTop = targetTop;
            return;
        }

        var currentTop = double.IsNaN(host.Top) || double.IsInfinity(host.Top) ? targetTop : RoundToDevicePixelY(host.Top);
        var currentHostLeft = double.IsNaN(host.Left) || double.IsInfinity(host.Left) ? targetHostLeft : RoundToDevicePixelX(host.Left);
        var currentViewportWidth = double.IsNaN(host.Width) || double.IsInfinity(host.Width) || host.Width <= 0
            ? viewportWidth
            : RoundToDevicePixelX(host.Width);
        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        host.BeginAnimation(Window.LeftProperty, null);
        var targetRight = targetHostLeft + viewportWidth;
        var currentRight = currentHostLeft + currentViewportWidth;
        var needsHorizontalAnimation =
            Math.Abs(currentHostLeft - targetHostLeft) >= 0.5 ||
            Math.Abs(currentRight - targetRight) >= 0.5 ||
            Math.Abs(currentViewportWidth - viewportWidth) >= 0.5;
        if (needsHorizontalAnimation)
        {
            _deepCapsuleSlotTargetLeft = targetHostLeft;
            _deepCapsuleSlotStartViewportWidth = currentViewportWidth;
            _deepCapsuleSlotTargetViewportWidth = viewportWidth;

            ApplyDeepCapsuleSlotHostViewport(currentViewportWidth);
            ApplyDeepCapsuleSlotHorizontalProgress(0.0);
            var horizontalAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = easeOut
            };
            horizontalAnim.Completed += (_, _) =>
            {
                if (generation != _deepCapsuleSlotMoveGeneration)
                {
                    return;
                }

                ClearDeepCapsuleSlotHorizontalAnimation();
                ApplyDeepCapsuleSlotHostViewport(viewportWidth);
                host.Left = targetHostLeft;
                _deepCapsuleSlotLeft = targetHostLeft;
            };
            BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, horizontalAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleSlotHorizontalAnimation();
            host.Left = targetHostLeft;
            ApplyDeepCapsuleSlotHostViewport(viewportWidth);
            _deepCapsuleSlotLeft = targetHostLeft;
        }

        if (Math.Abs(currentTop - targetTop) >= 0.5)
        {
            var topAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = currentTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = easeOut
            };
            topAnim.Completed += (_, _) =>
            {
                if (generation != _deepCapsuleSlotMoveGeneration)
                {
                    return;
                }

                host.BeginAnimation(Window.TopProperty, null);
                host.Top = targetTop;
                _deepCapsuleSlotTop = targetTop;
            };
            host.BeginAnimation(Window.TopProperty, topAnim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            host.BeginAnimation(Window.TopProperty, null);
            host.Top = targetTop;
            _deepCapsuleSlotTop = targetTop;
        }
    }

    private void AnimateSlotHostOpacity(double to, bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (!animate || Math.Abs(_deepCapsuleSlotHost.Opacity - to) < 0.001)
        {
            _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, null);
            _deepCapsuleSlotHost.Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = _deepCapsuleSlotHost.Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        anim.Completed += (_, _) =>
        {
            _deepCapsuleSlotHost?.BeginAnimation(Window.OpacityProperty, null);
            if (_deepCapsuleSlotHost != null)
            {
                _deepCapsuleSlotHost.Opacity = to;
            }
        };
        _deepCapsuleSlotHost.BeginAnimation(Window.OpacityProperty, anim);
    }

    private void CloseExpandedDeepCapsuleSlotHostForReal()
    {
        CloseDeepCapsuleSlotContextMenu();
        if (!_paper.IsCollapsed && _deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
        }
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        ClearDeepCapsuleSlotHorizontalAnimation();
        _deepCapsuleSlotHost.Content = null;
        _deepCapsuleSlotHost.Close();
        _deepCapsuleSlotHost = null;
        _deepCapsuleSlotHostRoot = null;
        _deepCapsuleSlotChrome = null;
        _deepCapsuleSlotOutline = null;
        _deepCapsuleSlotShell = null;
        _deepCapsuleSlotIconText = null;
        _deepCapsuleSlotCloseArea = null;
        _deepCapsuleSlotCloseGlyph = null;
        _deepCapsuleSlotCloseGlyphOffset = null;
        _deepCapsuleSlotLabelText = null;
    }

    private void BuildDragLayer()
    {
        _dragLayer = new Canvas
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
            ClipToBounds = false
        };

        Grid.SetRowSpan(_dragLayer, 3);
        Panel.SetZIndex(_dragLayer, 1000);
        _shell.Children.Add(_dragLayer);
    }

    private void BuildTopBar()
    {
        var top = new Grid
        {
            Height = TitleBarHeight,
            Margin = new Thickness(3, 3, 6, 0),
            Background = Brushes.Transparent
        };

        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        top.PreviewMouseLeftButtonDown += (_, _) => ExitNoteEditor();
        top.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                try { DragMove(); } catch { }
            }
        };

        var titleArea = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _paperIconButton = IconButton(_paper.Type == PaperTypes.Note ? "✎" : "☑", _paper.AlwaysOnTop ? Strings.Get("Unpin") : Strings.Get("Pin"));
        _paperIconButton.Width = 23;
        _paperIconButton.FontSize = _paper.Type == PaperTypes.Note ? 15 : 13;
        _paperIconButton.HorizontalAlignment = HorizontalAlignment.Left;
        _paperIconButton.VerticalAlignment = VerticalAlignment.Center;
        _paperIconButton.Click += (_, _) => ToggleTopmost();
        _paperIconButton.MouseEnter += (_, _) => _paperIconButton.Opacity = 1.0;
        _paperIconButton.MouseLeave += (_, _) => RefreshPaperIconButton();
        RefreshPaperIconButton();

        Grid.SetColumn(_paperIconButton, 0);
        titleArea.Children.Add(_paperIconButton);

        var titleHost = new Border
        {
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(4, 1, 5, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.IBeam,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
            MaxWidth = 86,
            ToolTip = Strings.Get("ToolTipEditTitle")
        };
        titleHost.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");

        var titleEditLayer = new Grid
        {
            MinWidth = 30,
            MaxWidth = 76
        };

        _titleText = new TextBlock
        {
            Foreground = TextBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam
        };

        _titleEditBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MaxLength = PaperTitles.MaxTitleLength,
            // MaxLength is only a coarse UTF-16 guard; the real title limit is applied on commit
            // so IME composition is never interrupted by rewriting TextBox.Text mid-edit.
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        _titleEditBox.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        _titleEditBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndTitleEdit(commit: false);
                e.Handled = true;
            }
        };
        _titleEditBox.LostKeyboardFocus += (_, _) =>
        {
            if (_isEditingTitle)
            {
                CommitTitleEdit();
            }
        };

        titleEditLayer.Children.Add(_titleText);
        titleEditLayer.Children.Add(_titleEditBox);
        titleHost.Child = titleEditLayer;
        titleHost.MouseEnter += (_, _) => titleHost.Background = HoverBrush;
        titleHost.MouseLeave += (_, _) => titleHost.Background = Brushes.Transparent;
        titleHost.MouseLeftButtonDown += (_, e) =>
        {
            BeginTitleEdit();
            e.Handled = true;
        };

        Grid.SetColumn(titleHost, 1);
        titleArea.Children.Add(titleHost);

        RefreshPaperTitle();

        Grid.SetColumn(titleArea, 0);
        top.Children.Add(titleArea);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        _newTodoButton = IconButton("＋✓", Strings.Get("ToolTipNewTodoPaper"));
        _newTodoButton.Click += (_, _) => _controller.CreatePaper(PaperTypes.Todo, show: true, _paper);

        _newNoteButton = IconButton("＋✎", Strings.Get("ToolTipNewNotePaper"));
        _newNoteButton.Click += (_, _) => _controller.CreatePaper(PaperTypes.Note, show: true, _paper);

        if (_paper.Type == PaperTypes.Note)
        {
            _linkNoteButton = IconButton("⌖", Strings.Get("ToolTipDragNoteToTodo"));
            _linkNoteButton.Width = 24;
            _linkNoteButton.FontSize = 13;
            _linkNoteButton.Cursor = Cursors.Cross;
            _linkNoteButton.Visibility = _controller.State.EnableTodoNoteLinks ? Visibility.Visible : Visibility.Collapsed;
            _linkNoteButton.PreviewMouseLeftButtonDown += (_, e) => BeginNoteLinkMouseGesture(_linkNoteButton, e);
            _linkNoteButton.PreviewMouseMove += (_, e) => UpdateNoteLinkMouseGesture(e);
            _linkNoteButton.PreviewMouseLeftButtonUp += (_, e) => EndNoteLinkMouseGestureFromMouseUp(e);
            _linkNoteButton.LostMouseCapture += (_, _) => EndNoteLinkMouseGesture(commit: false);
            buttons.Children.Add(_linkNoteButton);

            _openMarkdownButton = IconButton(ExternalOpenButtonLabel(), OpenMarkdownEditorToolTip());
            _openMarkdownButton.FontSize = 10.5;
            _openMarkdownButton.Click += (_, _) => OpenMarkdownInDefaultEditor();
            buttons.Children.Add(_openMarkdownButton);
        }

        _closeButton = IconButton("×", Strings.Get("ToolTipHideThisPaper"));
        _closeButton.FontSize = 16;
        _closeButton.Click += (_, _) =>
        {
            if (CanDisplayAsCapsule())
            {
                SetCollapsedState(true);
            }
            else
            {
                _controller.HidePaper(_paper);
            }
        };
        RefreshCloseButton();

        buttons.Children.Add(_newTodoButton);
        buttons.Children.Add(_newNoteButton);
        buttons.Children.Add(_closeButton);
        UpdateTopBarNewPaperButtons();

        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        var topHost = new Border
        {
            Margin = new Thickness(0, 0, 0, 1.5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(RadiusShell, RadiusShell, 0, 0),
            Child = top
        };
        topHost.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");
        topHost.SetResourceReference(Border.BorderBrushProperty, "TitleBarDividerBrushKey");

        Grid.SetRow(topHost, 0);
        _shell.Children.Add(topHost);
    }

    private void BeginNoteLinkMouseGesture(FrameworkElement handle, MouseButtonEventArgs e)
    {
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Note)
        {
            return;
        }

        _noteLinkDrag = new NoteLinkDragState(handle, PointToScreen(e.GetPosition(this)));
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateNoteLinkMouseGesture(MouseEventArgs e)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndNoteLinkMouseGesture(commit: state.IsDragging);
            e.Handled = true;
            return;
        }

        var currentScreenPoint = PointToScreen(e.GetPosition(this));
        if (!state.IsDragging)
        {
            var movedEnough =
                Math.Abs(currentScreenPoint.X - state.StartScreenPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentScreenPoint.Y - state.StartScreenPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

            if (!movedEnough)
            {
                return;
            }

            state.IsDragging = true;
            state.Handle.Opacity = 0.82;
            Mouse.OverrideCursor = Cursors.Cross;
            ExitNoteEditor();
            _controller.BeginNoteLinkDrag(_paper);
            state.Ghost = CreateNoteLinkDragGhost();
            state.Ghost.Show();
            state.Ghost.UpdateLayout();
        }

        MoveNoteLinkDragGhost(state, currentScreenPoint);
        _controller.UpdateNoteLinkDrag(_paper, currentScreenPoint);
        e.Handled = true;
    }

    private void EndNoteLinkMouseGestureFromMouseUp(MouseButtonEventArgs e)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        EndNoteLinkMouseGesture(commit: state.IsDragging);
        e.Handled = true;
    }

    private void EndNoteLinkMouseGesture(bool commit)
    {
        var state = _noteLinkDrag;
        if (state == null)
        {
            return;
        }

        _noteLinkDrag = null;

        if (state.Handle.IsMouseCaptured)
        {
            state.Handle.ReleaseMouseCapture();
        }

        CloseNoteLinkDragGhost(state);
        state.Handle.Opacity = 1.0;
        Mouse.OverrideCursor = null;
        _controller.EndNoteLinkDrag(_paper, commit && state.IsDragging);
    }

    private Window CreateNoteLinkDragGhost()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        stack.Children.Add(new TextBlock
        {
            Text = "✎",
            Foreground = TextBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = _controller.PaperCapsuleTitle(_paper),
            Foreground = TextBrush,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var root = new Border
        {
            Padding = new Thickness(9, 5, 10, 5),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = NoteLinkTargetBorderBrush,
            BorderThickness = new Thickness(1),
            Opacity = 0.86,
            Child = stack,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = root
        };
    }

    private static void MoveNoteLinkDragGhost(NoteLinkDragState state, Point screenPoint)
    {
        if (state.Ghost == null)
        {
            return;
        }

        var mousePoint = screenPoint;
        var source = PresentationSource.FromVisual(state.Handle);
        if (source?.CompositionTarget != null)
        {
            mousePoint = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(state.Handle);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                mousePoint = new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);
            }
        }

        var width = state.Ghost.ActualWidth > 1 ? state.Ghost.ActualWidth : state.Ghost.Width;
        var height = state.Ghost.ActualHeight > 1 ? state.Ghost.ActualHeight : state.Ghost.Height;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 1)
        {
            width = 120;
        }
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 1)
        {
            height = 28;
        }

        state.Ghost.Left = mousePoint.X - (width / 2);
        state.Ghost.Top = mousePoint.Y - (height / 2);
    }

    private static void CloseNoteLinkDragGhost(NoteLinkDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        try
        {
            state.Ghost.Close();
        }
        catch
        {
            // Drag feedback is disposable UI.
        }

        state.Ghost = null;
    }

    private void BuildBody()
    {
        UIElement body = _paper.Type == PaperTypes.Note ? BuildNoteBody() : BuildTodoBody();
        Grid.SetRow(body, 1);
        if (_paper.Type == PaperTypes.Note)
        {
            _noteBodyElement = body;
        }
        _shell.Children.Add(body);

        if (_paper.Type == PaperTypes.Note)
        {
            BuildTextZoomOverlay();
        }
    }

    private void BuildTextZoomOverlay()
    {
        var zoomHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 7),
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipResetTextZoom"),
            Visibility = Visibility.Collapsed
        };

        _textZoomIndicator = new TextBlock
        {
            Foreground = WeakTextBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };

        zoomHost.Child = _textZoomIndicator;
        zoomHost.MouseEnter += (_, _) =>
        {
            zoomHost.Background = HoverBrush;
            _textZoomIndicator.Foreground = TextBrush;
            _textZoomIndicator.Opacity = 1.0;
        };
        zoomHost.MouseLeave += (_, _) =>
        {
            zoomHost.Background = Brushes.Transparent;
            _textZoomIndicator.Foreground = WeakTextBrush;
            _textZoomIndicator.Opacity = 0.55;
        };
        zoomHost.MouseLeftButtonUp += (_, e) =>
        {
            _controller.SetPaperTextZoom(_paper, 1.0);
            e.Handled = true;
        };

        Grid.SetRow(zoomHost, 1);
        Panel.SetZIndex(zoomHost, 20);
        _shell.Children.Add(zoomHost);
    }

    private UIElement BuildTodoBody()
    {
        if (_paper.Items.Count == 0)
        {
            _paper.Items.Add(new PaperItem { Order = 0 });
        }

        _todoPanel = new StackPanel
        {
            Margin = new Thickness(6.4, 3.2, 5.6, 3.2)
        };

        RebuildTodoRows();

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _todoPanel,
            FocusVisualStyle = null
        };
    }

    private UIElement BuildNoteBody()
    {
        var host = new Grid();

        _noteBox = new MarkdownTextBox
        {
            Text = _paper.Content ?? "",
            MaxLength = 100000,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            FontFamily = NoteTypography.FontFamily,
            FontSize = NoteTypography.FontSize,
            FontStyle = NoteTypography.FontStyle,
            FontWeight = NoteTypography.FontWeight,
            FontStretch = NoteTypography.FontStretch,
            Language = NoteTypography.Language,
            Margin = NoteTypography.ContentPadding,
            FocusVisualStyle = null
        };
        NoteTypography.ApplyTextRendering(_noteBox);
        var box = _noteBox;
        box.SetMarkdownRenderMode(_controller.State.MarkdownRenderMode);
        box.SetTextZoom(CurrentTextZoom());

        host.Children.Add(box);
        var editorMenu = CreateContextMenu();
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuFormat")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuBold"), (_, _) => box.WrapSelection("**", "**")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuItalic"), (_, _) => box.WrapSelection("*", "*")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuStrikethrough"), (_, _) => box.WrapSelection("~~", "~~")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuHeading"), (_, _) => box.InsertLinePrefix("# ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuQuote"), (_, _) => box.InsertLinePrefix("> ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuList"), (_, _) => box.InsertLinePrefix("- ")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCodeBlock"), (_, _) => box.WrapSelection("```\n", "\n```")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuInsertLink"), (_, _) => box.InsertMarkdownLink()));
        editorMenu.Items.Add(MenuSeparator());
        editorMenu.Items.Add(MenuHeader(Strings.Get("MenuText")));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuCopy"), (_, _) => box.Copy()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuPaste"), (_, _) => box.Paste()));
        editorMenu.Items.Add(MenuItem(Strings.Get("MenuSelectAll"), (_, _) => box.SelectAll()));

        var previewMenu = BuildPaperContextMenu();
        var isPreviewing = false;
        var isEnteringEditorFromPreview = false;

        void ShowPreview()
        {
            TraceNoteRender($"ShowPreview before isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SelectionLength = 0;
            box.SetPreviewMode(true);
            box.ContextMenu = previewMenu;
            isPreviewing = true;
            TraceNoteRender($"ShowPreview after isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        }

        _showNotePreview = ShowPreview;

        void ShowEditor(bool focus = true)
        {
            TraceNoteRender($"ShowEditor before focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            box.SetPreviewMode(false);
            box.ContextMenu = editorMenu;
            isPreviewing = false;

            if (focus && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }
            TraceNoteRender($"ShowEditor after focus={focus} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} focused={box.IsKeyboardFocusWithin}");
        }

        void ShowEditorAtPreviewPoint(Point previewPoint)
        {
            TraceNoteRender($"ShowEditorAtPreviewPoint x={previewPoint.X:F1} y={previewPoint.Y:F1}");
            var hasPreviewCaret = box.TryGetCharacterIndexFromPoint(previewPoint, out var caretIndex);

            isEnteringEditorFromPreview = true;
            ShowEditor(focus: false);

            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
            }

            if (hasPreviewCaret)
            {
                box.CaretIndex = Math.Clamp(caretIndex, 0, box.Text.Length);
                box.SelectionLength = 0;
            }
            TraceNoteRender($"ShowEditorAtPreviewPoint after hasCaret={hasPreviewCaret} caret={box.CaretIndex}");
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    isEnteringEditorFromPreview = false;
                    TraceNoteRender($"ShowEditorAtPreviewPoint release focused={box.IsKeyboardFocusWithin} isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        static void OpenMarkdownLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Link opening is optional; the note should never crash because a URL handler failed.
            }
        }

        box.TextChanged += (_, _) =>
        {
            _paper.Content = box.Text;
            _controller.MarkDirty();
        };

        box.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (e.Key == Key.B)
            {
                box.WrapSelection("**", "**");
                e.Handled = true;
            }
            else if (e.Key == Key.I)
            {
                box.WrapSelection("*", "*");
                e.Handled = true;
            }
            else if (e.Key == Key.K)
            {
                box.InsertMarkdownLink();
                e.Handled = true;
            }
        };

        box.GotKeyboardFocus += (_, _) =>
        {
            TraceNoteRender($"GotKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
        };

        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.ContextMenu != null && box.ContextMenu.IsOpen)
            {
                TraceNoteRender("LostKeyboardFocus ignored: context menu open");
                return;
            }
            if (isEnteringEditorFromPreview)
            {
                TraceNoteRender($"LostKeyboardFocus ignored: entering editor isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }
            TraceNoteRender($"LostKeyboardFocus isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
            ShowPreview();
        };

        MouseButtonEventHandler noteMouseDown = (_, e) =>
        {
            if (IsScrollBarInteractionSource(e.OriginalSource as DependencyObject, box))
            {
                TraceNoteRender($"PreviewMouseLeftButtonDown ignored: scrollbar isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode}");
                return;
            }

            var textViewPoint = e.GetPosition(box.TextArea.TextView);
            TraceNoteRender($"PreviewMouseLeftButtonDown isPreviewing={isPreviewing} boxPreview={box.IsPreviewMode} handled={e.Handled}");
            if (!isPreviewing)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var editUrl))
                {
                    OpenMarkdownLink(editUrl);
                    e.Handled = true;
                }
                return;
            }

            var point = e.GetPosition(box);
            if (box.TryGetMarkdownLinkFromTextViewPoint(textViewPoint, out var url))
            {
                OpenMarkdownLink(url);
                e.Handled = true;
                return;
            }

            ShowEditorAtPreviewPoint(point);
            e.Handled = true;
        };
        box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, noteMouseDown, true);

        box.MouseMove += (sender, e) =>
        {
            var isOverLink = box.TryGetMarkdownLinkFromTextViewPoint(e.GetPosition(box.TextArea.TextView), out _);
            if (isPreviewing)
            {
                box.SetInteractionCursor(isOverLink ? Cursors.Hand : Cursors.Arrow);
            }
            else
            {
                box.SetInteractionCursor(isOverLink && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                    ? Cursors.Hand
                    : Cursors.IBeam);
            }
        };

        box.MouseLeave += (_, _) =>
        {
            box.SetInteractionCursor(isPreviewing ? Cursors.Arrow : Cursors.IBeam);
        };

        editorMenu.Closed += (_, _) =>
        {
            if (!isPreviewing && !box.IsFocused && !box.IsKeyboardFocusWithin)
            {
                ShowPreview();
            }
        };

        if (box.IsFocused || string.IsNullOrEmpty(box.Text))
        {
            ShowEditor();
        }
        else
        {
            ShowPreview();
        }

        return host;
    }

    public void RefreshTodoRowsForExternalChange()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        RebuildTodoRows(CurrentFocusedTodoItemId());
    }

    private void RebuildTodoRows(string? focusItemId = null, TodoFocusPlacement focusPlacement = TodoFocusPlacement.End)
    {
        if (_todoPanel == null)
        {
            return;
        }

        _todoRowsGeneration++;
        var targetFocus = focusItemId ?? _pendingFocusItemId;
        _pendingFocusItemId = null;

        NormalizeTodoItems();
        NormalizeOrders();

        // 记录现有行的ID，用于判断哪些是新增的
        var existingIds = new HashSet<string>(_todoRows.Select(r => (string)r.Tag));

        _todoPanel.Children.Clear();
        _todoEditors.Clear();
        _todoRows.Clear();
        _linkedNoteDropRow = null;

        foreach (var item in OrderedItems())
        {
            var row = BuildTodoRow(item, isNewItem: !existingIds.Contains(item.Id));
            _todoPanel.Children.Add(row);
        }

        _todoPanel.Children.Add(BuildTodoAppendArea());

        if (!string.IsNullOrWhiteSpace(targetFocus))
        {
            FocusTodoItem(targetFocus, focusPlacement);
        }
    }

    private void FocusTodoItem(string? itemId, TodoFocusPlacement placement = TodoFocusPlacement.End)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_todoEditors.TryGetValue(itemId, out var box))
            {
                box.Focus();
                box.CaretIndex = placement == TodoFocusPlacement.Start ? 0 : box.Text.Length;
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private UIElement BuildTodoAppendArea()
    {
        var area = new Border
        {
            Margin = new Thickness(0, 6, 0, 2),
            Padding = new Thickness(0, 4, 0, 4),
            CornerRadius = new CornerRadius(RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = AppendBorderBrush,
            Background = AppendBgBrush,
            MinHeight = 30,
            Cursor = Cursors.IBeam,
            AllowDrop = true,
            ToolTip = Strings.Get("AppendAreaToolTip")
        };

        _appendArea = area;

        var plus = new TextBlock
        {
            Text = "＋",
            Foreground = WeakTextBrush,
            Opacity = 0.42,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        area.Child = plus;

        area.MouseEnter += (_, _) =>
        {
            area.Background = AppendHoverBgBrush;
            plus.Opacity = 0.7;
        };

        area.MouseLeave += (_, _) =>
        {
            ResetAppendAreaDropState();
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            var newItem = AddItemAfter(OrderedItems().LastOrDefault(), "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
        };

        return area;
    }

    private void ShowAppendAreaAsTrashBin(bool active, bool hovered = false)
    {
        if (_appendArea == null)
        {
            return;
        }

        if (active)
        {
            if (hovered)
            {
                _appendArea.Background = TrashHoverBgBrush;
                _appendArea.BorderBrush = TrashHoverBorderBrush;
                _appendArea.BorderThickness = new Thickness(1.5);
            }
            else
            {
                _appendArea.Background = TrashBgBrush;
                _appendArea.BorderBrush = TrashBorderBrush;
                _appendArea.BorderThickness = new Thickness(1);
            }

            if (_appendArea.Child is TextBlock text)
            {
                text.Text = "🗑";
                text.Foreground = TrashTextBrush;
                text.Opacity = hovered ? 1.0 : 0.65;
                text.FontSize = 13;
            }
        }
        else
        {
            _appendArea.Background = AppendBgBrush;
            _appendArea.BorderBrush = AppendBorderBrush;
            _appendArea.BorderThickness = new Thickness(1);

            if (_appendArea.Child is TextBlock text)
            {
                text.Text = "＋";
                text.Foreground = WeakTextBrush;
                text.Opacity = 0.42;
                text.FontSize = 14;
            }
        }
    }

    private void ResetAppendAreaDropState()
    {
        ShowAppendAreaAsTrashBin(active: false);
    }

    private static string CompactLinkedNoteTitle(string title, int fullTextElementLimit, int truncatedTextElementCount)
    {
        var text = title.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        int[] textElements;
        try
        {
            textElements = StringInfo.ParseCombiningCharacters(text);
        }
        catch
        {
            if (text.Length <= fullTextElementLimit)
            {
                return text;
            }

            return text[..Math.Min(Math.Max(1, truncatedTextElementCount), text.Length)] + "…";
        }

        if (textElements.Length <= fullTextElementLimit)
        {
            return text;
        }

        var keep = Math.Max(1, truncatedTextElementCount);
        var end = textElements.Length > keep ? textElements[keep] : Math.Min(keep, text.Length);
        return text[..end] + "…";
    }

    private UIElement BuildTodoRow(PaperItem item, bool isNewItem = false)
    {
        var linkedNoteTitle = "";
        var hasLinkedNote = _controller.State.EnableTodoNoteLinks &&
            _controller.TryGetLinkedNoteTitle(item.LinkedNoteId, out linkedNoteTitle);

        var row = new Border
        {
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 2, 0, 2),
            AllowDrop = true,
            Tag = item.Id,
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(1, 1),
                    new TranslateTransform(0, 0)
                }
            },
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        row.MouseEnter += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = HoverBrush;
            }
        };

        row.MouseLeave += (_, _) =>
        {
            if (!Equals(_activeDropRow, row) && !Equals(_linkedNoteDropRow, row))
            {
                row.Background = Brushes.Transparent;
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        var check = new CheckBox
        {
            IsChecked = item.Done,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = SharedCheckBoxStyle
        };

        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var text = new TodoTextBox
        {
            Text = item.Text,
            IsDone = item.Done,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = item.Done ? BrightWeakTextBrush : TextBrush,
            CaretBrush = TextBrush,
            FontSize = 13,
            Padding = new Thickness(2, 3, 2, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MaxLength = 5000
        };

        _todoEditors[item.Id] = text;

        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text;
            _controller.MarkDirty();
        };

        text.PreviewKeyDown += (_, e) => HandleTodoKeyDown(e, item, text);
        DataObject.AddPastingHandler(text, (sender, e) => HandleTodoPaste(e, item, text));

        text.GotFocus += (_, _) =>
        {
            _activeOriginalItemId = item.Id;
            _activeOriginalText = text.Text;
        };

        text.LostFocus += (_, _) =>
        {
            if (_activeOriginalItemId == item.Id && _activeOriginalText != null && text.Text != _activeOriginalText)
            {
                var oldText = item.Text;
                item.Text = _activeOriginalText;

                _undoStack.Add(CloneItems(_paper.Items));
                if (_undoStack.Count > MaxUndoDepth)
                {
                    _undoStack.RemoveAt(0);
                }
                _redoStack.Clear();

                item.Text = oldText;
                _activeOriginalText = oldText;
            }
        };

        check.Checked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = true;
            text.IsDone = true;
            text.Foreground = BrightWeakTextBrush;
            _controller.MarkDirty();

            // 完成动画：只淡化，不缩小
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.75, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }
        };

        check.Unchecked += (_, _) =>
        {
            PushUndoSnapshot();
            item.Done = false;
            text.IsDone = false;
            text.Foreground = TextBrush;
            _controller.MarkDirty();

            // 取消完成动画
            if (_controller.State.EnableAnimations)
            {
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(row.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
                row.BeginAnimation(OpacityProperty, fadeAnim);
            }
        };

        ContextMenu CreateItemMenu()
        {
            var itemMenu = CreateContextMenu();
            itemMenu.Items.Add(MenuHeader(Strings.Get("MenuTodoItem")));
            if (hasLinkedNote)
            {
                itemMenu.Items.Add(MenuItem(Strings.Format("MenuOpenLinkedNote", linkedNoteTitle), (_, _) => _controller.OpenLinkedNote(item.LinkedNoteId, this)));
                itemMenu.Items.Add(MenuItem(Strings.Get("MenuUnlinkNote"), (_, _) => UnlinkNoteFromTodoItem(item)));
                itemMenu.Items.Add(MenuSeparator());
            }
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuDeleteItem"), (_, _) => RemoveItem(item)));
            itemMenu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));

            itemMenu.Opened += (_, _) => row.Background = HoverBrush;
            itemMenu.Closed += (_, _) =>
            {
                if (!row.IsMouseOver)
                {
                    row.Background = Brushes.Transparent;
                }
            };

            return itemMenu;
        }

        void AttachItemContextMenu(FrameworkElement element)
        {
            element.ContextMenu = CreateItemMenu();
            element.PreviewMouseRightButtonDown += (_, _) => text.Focus();
        }

        AttachItemContextMenu(row);
        AttachItemContextMenu(check);
        AttachItemContextMenu(text);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (hasLinkedNote)
        {
            var showLinkedNoteName = _controller.State.ShowLinkedNoteName;
            var linkedNoteButtonText = showLinkedNoteName ? CompactLinkedNoteTitle(linkedNoteTitle, 3, 3) : "\uE71B";
            var linkGlyph = new TextBlock
            {
                Text = linkedNoteButtonText,
                Foreground = WeakTextBrush,
                Opacity = 0.72,
                FontFamily = showLinkedNoteName ? new FontFamily("Segoe UI") : new FontFamily("Segoe MDL2 Assets"),
                FontSize = showLinkedNoteName ? 10.5 : 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = showLinkedNoteName ? 11.5 : double.NaN,
                MaxWidth = showLinkedNoteName ? 44 : double.PositiveInfinity
            };

            var linkButton = new Border
            {
                Width = showLinkedNoteName ? 50 : 23,
                MinWidth = 23,
                MinHeight = 22,
                Margin = new Thickness(1, 0, 0, 0),
                Padding = showLinkedNoteName ? new Thickness(3, 1, 3, 1) : new Thickness(0),
                CornerRadius = new CornerRadius(RadiusControl),
                Background = LinkedNoteBgBrush,
                Cursor = Cursors.Hand,
                ToolTip = Strings.Format("ToolTipOpenLinkedNote", linkedNoteTitle),
                Child = linkGlyph
            };

            void UpdateLinkedNoteNameLayout()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                var isTodoMultiline = text.LineCount > 1;
                linkGlyph.Text = isTodoMultiline
                    ? CompactLinkedNoteTitle(linkedNoteTitle, 6, 5)
                    : CompactLinkedNoteTitle(linkedNoteTitle, 3, 3);
                linkGlyph.TextWrapping = isTodoMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
                linkGlyph.MaxWidth = isTodoMultiline ? 38 : 44;
                linkButton.Width = isTodoMultiline ? 44 : 50;
            }

            void QueueLinkedNoteNameLayoutUpdate()
            {
                if (!showLinkedNoteName)
                {
                    return;
                }

                Dispatcher.BeginInvoke((Action)UpdateLinkedNoteNameLayout, System.Windows.Threading.DispatcherPriority.Render);
            }

            if (showLinkedNoteName)
            {
                text.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                row.SizeChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                text.TextChanged += (_, _) => QueueLinkedNoteNameLayoutUpdate();
                QueueLinkedNoteNameLayoutUpdate();
            }

            linkButton.MouseEnter += (_, _) =>
            {
                linkButton.Background = LinkedNoteHoverBgBrush;
                linkGlyph.Foreground = TextBrush;
                linkGlyph.Opacity = 1.0;
            };
            linkButton.MouseLeave += (_, _) =>
            {
                linkButton.Background = LinkedNoteBgBrush;
                linkGlyph.Foreground = WeakTextBrush;
                linkGlyph.Opacity = 0.7;
                linkButton.Opacity = 1.0;
            };
            linkButton.MouseLeftButtonDown += (_, e) =>
            {
                linkButton.Opacity = 0.72;
                e.Handled = true;
            };
            linkButton.MouseLeftButtonUp += (_, e) =>
            {
                linkButton.Opacity = 1.0;
                _controller.OpenLinkedNote(item.LinkedNoteId, this);
                e.Handled = true;
            };
            AttachItemContextMenu(linkButton);

            Grid.SetColumn(linkButton, 2);
            grid.Children.Add(linkButton);
        }

        var handleGlyph = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.48,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var handle = new Border
        {
            Width = 14,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = handleGlyph,
            ToolTip = Strings.Get("DragSortToolTip")
        };

        handle.MouseEnter += (_, _) => handleGlyph.Opacity = 0.78;
        handle.MouseLeave += (_, _) =>
        {
            if (_todoDrag?.ItemId != item.Id)
            {
                handleGlyph.Opacity = 0.48;
            }
        };

        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _todoDrag = new TodoDragState(item.Id, row, handle, e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        };
        AttachItemContextMenu(handle);

        Grid.SetColumn(handle, 3);
        grid.Children.Add(handle);

        row.Child = grid;
        _todoRows.Add(row);

        // 新增动画：只对新建的项播放动画
        if (_controller.State.EnableAnimations && isNewItem)
        {
            row.Opacity = 0;
            AnimationHelper.GetTranslateTransform(row).Y = -20;

            Dispatcher.InvokeAsync(() =>
            {
                AnimationHelper.FadeIn(row, 250);
                AnimationHelper.TranslateTo(row, 0, 0, 250, AnimationHelper.SmoothEase);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        return row;
    }

    private void HandleTodoKeyDown(KeyEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (e.Key == Key.Back && _suppressTodoBackspaceUntilKeyUp)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            var newItem = AddItemAfter(item, "");
            _pendingFocusItemId = newItem.Id;
            RebuildTodoRows(newItem.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(box.Text) && _paper.Items.Count > 1)
        {
            var previous = PreviousItem(item);
            var next = NextItem(item);
            var focusTarget = previous?.Id ?? next?.Id;
            _suppressTodoBackspaceUntilKeyUp = true;

            // 退格删除不播放动画，直接删除
            PushUndoSnapshot();
            _paper.Items.RemoveAll(i => i.Id == item.Id);

            if (_paper.Items.Count == 0)
            {
                var replacement = new PaperItem();
                _paper.Items.Add(replacement);
                focusTarget = replacement.Id;
            }

            NormalizeTodoItems();
            NormalizeOrders();
            _controller.MarkDirty();

            var focusPlacement = previous != null ? TodoFocusPlacement.End : TodoFocusPlacement.Start;
            RebuildTodoRows(focusTarget, focusPlacement);
            e.Handled = true;
        }
    }

    private void HandleTodoPaste(DataObjectPastingEventArgs e, PaperItem item, TodoTextBox box)
    {
        if (!ClipboardHelper.TryGetText(out var raw) || string.IsNullOrEmpty(raw))
        {
            return;
        }

        var lines = raw
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanPastedTodoLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length > 5000 ? line[..5000] : line)
            .ToList();

        if (lines.Count > 200)
        {
            lines = lines.Take(200).ToList();
        }

        if (lines.Count <= 1)
        {
            return;
        }

        e.CancelCommand();

        PushUndoSnapshot();
        ReplaceSelection(box, lines[0]);
        item.Text = box.Text;

        var last = item;
        var newItems = new List<PaperItem>();
        foreach (var line in lines.Skip(1))
        {
            last = AddItemAfter(last, line, pushUndo: false);
            newItems.Add(last);
        }

        _pendingFocusItemId = last.Id;
        RebuildTodoRows(last.Id);

        // 粘贴多行时的错峰动画
        if (_controller.State.EnableAnimations && newItems.Count > 1)
        {
            var animationGeneration = _todoRowsGeneration;
            for (int i = 0; i < Math.Min(newItems.Count, 15); i++)
            {
                var animItem = newItems[i];
                var animRow = _todoRows.FirstOrDefault(r => (string)r.Tag == animItem.Id);
                if (animRow == null) continue;

                var delay = i * 40;
                animRow.Opacity = 0;
                AnimationHelper.GetTranslateTransform(animRow).Y = -15;

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(delay),
                    Tag = animRow
                };
                timer.Tick += (s, _) =>
                {
                    timer.Stop();
                    var row = (Border)timer.Tag;
                    if (animationGeneration != _todoRowsGeneration || !_todoRows.Contains(row))
                    {
                        return;
                    }

                    AnimationHelper.FadeIn(row, 200);
                    AnimationHelper.TranslateTo(row, 0, 0, 220, AnimationHelper.QuickEase);
                };
                timer.Start();
            }
        }

        _controller.MarkDirty();
    }

    private static string CleanPastedTodoLine(string line)
    {
        var cleaned = line.Trim();

        cleaned = TodoCheckboxCleanRegex().Replace(cleaned, "");
        cleaned = TodoBulletCleanRegex().Replace(cleaned, "");
        cleaned = TodoNumberCleanRegex().Replace(cleaned, "");
        cleaned = TodoGlyphCleanRegex().Replace(cleaned, "");

        return cleaned.Trim();
    }

    private ContextMenu BuildPaperContextMenu(bool forDeepCapsuleSlot = false)
    {
        var menu = CreateContextMenu();

        menu.Items.Add(MenuHeader(Strings.Get("MenuNew")));
        menu.Items.Add(MenuItem(Strings.Get("MenuNewTodoPaper"), (_, _) => _controller.CreatePaper(PaperTypes.Todo, show: true, _paper)));
        menu.Items.Add(MenuItem(Strings.Get("MenuNewNotePaper"), (_, _) => _controller.CreatePaper(PaperTypes.Note, show: true, _paper)));

        if (_paper.Type == PaperTypes.Todo)
        {
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuHeader(Strings.Get("MenuTodo")));
            menu.Items.Add(MenuItem(Strings.Get("MenuClearDone"), (_, _) => ClearDoneItems()));
        }

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuHeader(_controller.PaperCapsuleTitle(_paper)));

        if (CanDisplayAsCapsule())
        {
            if (_paper.IsCollapsed)
            {
                if (!forDeepCapsuleSlot)
                {
                    menu.Items.Add(MenuItem(Strings.Get("MenuRestoreWindow"), (_, _) => SetCollapsedState(false)));
                }
            }
            else
            {
                menu.Items.Add(MenuItem(Strings.Get("MenuCollapseToCapsule"), (_, _) => SetCollapsedState(true)));
            }
        }

        menu.Items.Add(MenuItem(Strings.Get("MenuHide"), (_, _) => _controller.HidePaper(_paper)));
        menu.Items.Add(MenuItem(Strings.Get("MenuDelete"), (_, _) => DeletePaperFromPaperMenu()));

        return menu;
    }

    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);

        menu.Opened += (_, _) =>
        {
            if (_deepCapsuleSlotContextMenu != null && !ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu.IsOpen = false;
            }

            _deepCapsuleSlotContextMenu = menu;
            StartDeepCapsuleContextMenuGuards();
        };

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_deepCapsuleSlotContextMenu, menu))
            {
                _deepCapsuleSlotContextMenu = null;
                StopDeepCapsuleContextMenuGuards();
            }
        };

        return menu;
    }

    private void CloseDeepCapsuleSlotContextMenu()
    {
        var menu = _deepCapsuleSlotContextMenu;
        if (menu != null)
        {
            menu.IsOpen = false;
        }

        _deepCapsuleSlotContextMenu = null;
        StopDeepCapsuleContextMenuGuards();
    }

    private void StartDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook == IntPtr.Zero)
        {
            _deepCapsuleForegroundHookProc = OnDeepCapsuleForegroundChanged;
            _deepCapsuleForegroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _deepCapsuleForegroundHookProc,
                0,
                0,
                WineventOutOfContext);
        }

        if (_deepCapsuleMouseHook == IntPtr.Zero)
        {
            _deepCapsuleMouseHookProc = OnDeepCapsuleMouseHook;
            _deepCapsuleMouseHook = SetWindowsHookEx(WhMouseLl, _deepCapsuleMouseHookProc, GetModuleHandle(null), 0);
        }
    }

    private void StopDeepCapsuleContextMenuGuards()
    {
        if (_deepCapsuleForegroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_deepCapsuleForegroundHook);
            _deepCapsuleForegroundHook = IntPtr.Zero;
        }

        _deepCapsuleForegroundHookProc = null;

        if (_deepCapsuleMouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_deepCapsuleMouseHook);
            _deepCapsuleMouseHook = IntPtr.Zero;
        }

        _deepCapsuleMouseHookProc = null;
    }

    private void OnDeepCapsuleForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_deepCapsuleSlotContextMenu?.IsOpen != true || hwnd == IntPtr.Zero || IsWindowFromCurrentProcess(hwnd))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
    }

    private IntPtr OnDeepCapsuleMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam) && _deepCapsuleSlotContextMenu?.IsOpen == true)
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var screenPoint = new Point(hook.Point.X, hook.Point.Y);
            if (!IsPointInsideDeepCapsuleContextSurface(screenPoint))
            {
                Dispatcher.BeginInvoke(new Action(CloseDeepCapsuleSlotContextMenu));
            }
        }

        return CallNextHookEx(_deepCapsuleMouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideDeepCapsuleContextSurface(Point screenPoint)
    {
        if (IsPointInsideElement(_deepCapsuleSlotContextMenu, screenPoint))
        {
            return true;
        }

        return _deepCapsuleSlotHost?.IsVisible == true && IsPointInsideWindow(_deepCapsuleSlotHost, screenPoint);
    }

    private static bool IsPointInsideElement(FrameworkElement? element, Point screenPoint)
    {
        if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localPoint = element.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= element.ActualWidth &&
                localPoint.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPointInsideWindow(Window window, Point screenPoint)
    {
        try
        {
            var localPoint = window.PointFromScreen(screenPoint);
            return localPoint.X >= 0 &&
                localPoint.Y >= 0 &&
                localPoint.X <= window.ActualWidth &&
                localPoint.Y <= window.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsMouseButtonDownMessage(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown ||
            value == WmRButtonDown ||
            value == WmMButtonDown ||
            value == WmXButtonDown;
    }

    private static bool IsWindowFromCurrentProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    private void ToggleTopmost()
    {
        _paper.AlwaysOnTop = !_paper.AlwaysOnTop;
        RefreshEffectiveTopmost();
        RefreshPaperIconButton();
        _controller.MarkDirty();
    }

    internal void RefreshEffectiveTopmost()
    {
        var shouldBeTopmost = _paper.AlwaysOnTop || (_controller.State.UseCapsuleMode && _paper.IsCollapsed);
        var effectiveTopmost = shouldBeTopmost && !_controller.SuppressTopmostForFullscreenForeground;
        Topmost = effectiveTopmost;
        if (IsVisible && (shouldBeTopmost || _controller.SuppressTopmostForFullscreenForeground))
        {
            ApplyTopmostZOrder(this, effectiveTopmost, _controller.FullscreenAvoidanceWindow);
        }

        RefreshDeepCapsuleSlotTopmost();
    }

    internal void RefreshDeepCapsuleSlotTopmost()
    {
        if (_deepCapsuleSlotHost != null)
        {
            var slotShouldBeTopmost = !_controller.SuppressTopmostForFullscreenForeground;
            _deepCapsuleSlotHost.Topmost = slotShouldBeTopmost;
            if (_deepCapsuleSlotHost.IsVisible)
            {
                ApplyTopmostZOrder(_deepCapsuleSlotHost, slotShouldBeTopmost, _controller.FullscreenAvoidanceWindow);
            }
        }
    }

    private void RefreshPaperIconButton()
    {
        if (_paperIconButton == null)
        {
            return;
        }

        _paperIconButton.ToolTip = _paper.AlwaysOnTop ? Strings.Get("Unpin") : Strings.Get("Pin");
        _paperIconButton.Opacity = _paper.AlwaysOnTop ? 1.0 : 0.58;
        _paperIconButton.Foreground = _paper.AlwaysOnTop ? TextBrush : WeakTextBrush;
        _paperIconButton.FontWeight = _paper.AlwaysOnTop ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public void RefreshPaperTitle()
    {
        var title = _controller.PaperTitleText(_paper);
        Title = title;

        if (_titleText != null)
        {
            _titleText.Text = title;
            _titleText.ToolTip = Strings.Get("ToolTipEditTitle");
            _titleText.Foreground = TextBrush;
        }

        if (_titleEditBox != null)
        {
            _titleEditBox.Foreground = TextBrush;
            _titleEditBox.CaretBrush = TextBrush;
        }

        RefreshCapsuleLabel();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
        if (_paperChrome != null)
        {
            _paperChrome.ContextMenu = BuildPaperContextMenu();
        }
    }

    private void RequestTitleEdit()
    {
        QueueTitleEditAfterWindowIsExpanded();
    }

    private void BeginTitleEdit()
    {
        if (_titleText == null || _titleEditBox == null)
        {
            return;
        }

        if (_isEditingTitle)
        {
            _titleEditBox.Focus();
            _titleEditBox.SelectAll();
            return;
        }

        if (!CanBeginTitleEditNow())
        {
            QueueTitleEditAfterWindowIsExpanded();
            return;
        }

        ExitNoteEditor();
        _isEditingTitle = true;
        _titleEditBox.Text = _controller.PaperTitleText(_paper);
        _titleText.Visibility = Visibility.Collapsed;
        _titleEditBox.Visibility = Visibility.Visible;
        _titleEditBox.Focus();
        _titleEditBox.SelectAll();
    }

    private void CommitTitleEdit()
    {
        EndTitleEdit(commit: true);
    }

    private void EndTitleEdit(bool commit)
    {
        if (_titleText == null || _titleEditBox == null)
        {
            return;
        }

        if (!_isEditingTitle)
        {
            return;
        }

        var editedTitle = _titleEditBox.Text;
        _isEditingTitle = false;
        _titleEditBox.Visibility = Visibility.Collapsed;
        _titleText.Visibility = Visibility.Visible;

        if (commit)
        {
            _controller.UpdatePaperTitle(_paper, editedTitle);
        }
        else
        {
            RefreshPaperTitle();
        }
    }

    private bool CanBeginTitleEditNow()
    {
        return IsVisible &&
            !_paper.IsCollapsed &&
            !_isApplyingCollapsedState &&
            !_isTransitionVisualsActive &&
            Width > DesiredCapsuleWindowWidth + 8 &&
            Height > PaperLayoutDefaults.CapsuleHeight + 8;
    }

    private void QueueTitleEditAfterWindowIsExpanded()
    {
        if (_pendingTitleEdit)
        {
            return;
        }

        _pendingTitleEdit = true;
        if (_paper.IsCollapsed || !IsVisible)
        {
            ExpandForProgrammaticOpen();
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToRightEdge: true);
        }

        var delay = Math.Max(ExpandAnimationMilliseconds, CollapseResizeMilliseconds) + 30;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pendingTitleEdit = false;
            Dispatcher.BeginInvoke((Action)BeginTitleEdit, System.Windows.Threading.DispatcherPriority.Input);
        };
        timer.Start();
    }

    public void UpdateTextZoom()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        var zoom = CurrentTextZoom();
        if (_noteBox != null)
        {
            var expectedFontSize = Math.Round(NoteTypography.FontSize * zoom, 1);
            if (IsLoaded && Math.Abs(_noteBox.FontSize - expectedFontSize) > 0.001)
            {
                RebuildNoteBodyForMarkdownMode();
            }
            else
            {
                _noteBox.SetTextZoom(zoom);
            }
        }

        if (_textZoomIndicator != null)
        {
            _textZoomIndicator.Text = $"{(int)Math.Round(zoom * 100)}%";
            _textZoomIndicator.Foreground = WeakTextBrush;
            _textZoomIndicator.Opacity = 0.55;
            if (_textZoomIndicator.Parent is UIElement host)
            {
                host.Visibility = Math.Abs(zoom - 1.0) < 0.001 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private double CurrentTextZoom()
    {
        return Math.Clamp(_paper.TextZoom, 0.5, 1.5);
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        var step = e.Delta > 0 ? 0.1 : -0.1;
        _controller.SetPaperTextZoom(_paper, _paper.TextZoom + step);
        e.Handled = true;
    }

    private void OpenMarkdownInDefaultEditor()
    {
        if (_paper.Type != PaperTypes.Note)
        {
            return;
        }

        try
        {
            var path = WriteExternalMarkdownFile();
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Format("OpenMarkdownFailureMessage", CurrentExternalMarkdownExtension(), ex.Message),
                Strings.Get("OpenMarkdownFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void UpdateExternalMarkdownExtension()
    {
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Content = ExternalOpenButtonLabel();
            _openMarkdownButton.ToolTip = OpenMarkdownEditorToolTip();
            _openMarkdownButton.Visibility = _controller.State.ShowTopBarExternalOpenButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void UpdateTopBarNewPaperButtons()
    {
        if (_newTodoButton != null)
        {
            _newTodoButton.Visibility = _controller.State.ShowTopBarNewTodoButton ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_newNoteButton != null)
        {
            _newNoteButton.Visibility = _controller.State.ShowTopBarNewNoteButton ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Visibility = _controller.State.ShowTopBarExternalOpenButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void UpdateTodoLinkFeature()
    {
        if (_linkNoteButton != null)
        {
            _linkNoteButton.Visibility = _controller.State.EnableTodoNoteLinks ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_controller.State.EnableTodoNoteLinks)
        {
            EndNoteLinkMouseGesture(commit: false);
            SetNoteLinkDropTarget(null);
        }

        RefreshTodoRowsForExternalChange();
    }

    private string OpenMarkdownEditorToolTip()
    {
        return Strings.Format("ToolTipOpenMarkdownEditor", CurrentExternalMarkdownExtension());
    }

    private string ExternalOpenButtonLabel()
    {
        var extension = CurrentExternalMarkdownExtension().TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ExternalMarkdownFileExtensions.Default.TrimStart('.');
        }

        return extension.Length > 2
            ? extension[..2].ToUpperInvariant()
            : extension.ToUpperInvariant();
    }

    private string CurrentExternalMarkdownExtension()
    {
        return ExternalMarkdownFileExtensions.Normalize(_controller.State.ExternalMarkdownExtension);
    }

    private string WriteExternalMarkdownFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"paper-{_paper.Id}{CurrentExternalMarkdownExtension()}");
        var text = _noteBox?.Text ?? _paper.Content ?? "";
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private void ConfirmAndDeletePaper()
    {
        if (ShowDeletePaperDialog())
        {
            _controller.DeletePaper(_paper);
        }
    }

    private void DeletePaperFromPaperMenu()
    {
        if (_controller.IsPaperEmpty(_paper))
        {
            _controller.DeletePaper(_paper);
            return;
        }

        ConfirmAndDeletePaper();
    }

    private bool ShowDeletePaperDialog()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = Strings.Get("DeletePaperTitle"),
            Width = 300,
            Height = 178,
            MinWidth = 300,
            MinHeight = 178,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = Topmost
        };

        var root = new Border
        {
            CornerRadius = new CornerRadius(RadiusShell),
            BorderBrush = PaperBorderBrush,
            BorderThickness = new Thickness(1),
            Background = PaperBrush,
            Padding = new Thickness(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Strings.Get("DeletePaperQuestion"),
            Foreground = TextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var message = new TextBlock
        {
            Text = Strings.Get("DeletePaperBody"),
            Foreground = WeakTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var delete = DialogButton(Strings.Get("MenuDelete"), isDanger: true);
        delete.Click += (_, _) => dialog.DialogResult = true;

        buttons.Children.Add(delete);

        var cancel = DialogButton(Strings.Get("CommonCancel"), isDanger: false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => dialog.DialogResult = false;

        buttons.Children.Add(cancel);

        Grid.SetRow(title, 0);
        Grid.SetRow(message, 1);
        Grid.SetRow(buttons, 2);

        layout.Children.Add(title);
        layout.Children.Add(message);
        layout.Children.Add(buttons);

        root.Child = layout;
        dialog.Content = root;

        return dialog.ShowDialog() == true;
    }

    private static Button DialogButton(string text, bool isDanger)
    {
        var background = isDanger
            ? Theme.DangerBrush
            : Theme.Tint(28);

        var foreground = isDanger ? PaperBrush : TextBrush;
        var hover = isDanger
            ? Theme.DangerHoverBrush
            : Theme.Tint(46);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var mouseOver = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, hover));

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));

        template.Triggers.Add(mouseOver);
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }

    private PaperItem AddItemAfter(PaperItem? after, string text, bool pushUndo = true)
    {
        if (pushUndo) PushUndoSnapshot();
        var ordered = OrderedItems().ToList();
        var index = after == null ? ordered.Count : ordered.FindIndex(i => i.Id == after.Id) + 1;
        if (index < 0) index = ordered.Count;

        var newItem = new PaperItem
        {
            Text = text,
            Done = false
        };

        ordered.Insert(index, newItem);
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        return newItem;
    }

    private void RemoveItem(PaperItem item, bool rebuild = true, string? focusItemId = null)
    {
        PushUndoSnapshot();
        var fallbackFocus = focusItemId ?? PreviousItem(item)?.Id ?? NextItem(item)?.Id;
        var itemId = item.Id;

        // 删除动画
        if (_controller.State.EnableAnimations)
        {
            var row = _todoRows.FirstOrDefault(r => (string)r.Tag == itemId);
            if (row != null)
            {
                _paper.Items.RemoveAll(i => i.Id == itemId);

                if (_paper.Items.Count == 0)
                {
                    var replacement = new PaperItem();
                    _paper.Items.Add(replacement);
                    fallbackFocus = replacement.Id;
                }

                NormalizeTodoItems();
                NormalizeOrders();
                _controller.MarkDirty();

                var animationGeneration = _todoRowsGeneration;
                row.IsHitTestVisible = false;
                AnimationHelper.EnsureTransform(row);
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = AnimationHelper.QuickEase
                };

                fadeOut.Completed += (s, e) =>
                {
                    if (rebuild && animationGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(fallbackFocus);
                    }
                };

                row.BeginAnimation(OpacityProperty, fadeOut);
                AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                return;
            }
        }

        // 无动画或找不到行时直接删除
        _paper.Items.RemoveAll(i => i.Id == itemId);

        if (_paper.Items.Count == 0)
        {
            var replacement = new PaperItem();
            _paper.Items.Add(replacement);
            fallbackFocus = replacement.Id;
        }

        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        if (rebuild)
        {
            RebuildTodoRows(fallbackFocus);
        }
    }

    private void ClearDoneItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();
        var completedItems = OrderedItems().Where(i => i.Done).ToList();
        if (completedItems.Count == 0)
        {
            return;
        }

        var completedItemIds = new HashSet<string>(completedItems.Select(i => i.Id), StringComparer.Ordinal);
        var clearDoneGeneration = ++_clearDoneGeneration;

        PushUndoSnapshot();
        var remainingItems = OrderedItems()
            .Where(i => !completedItemIds.Contains(i.Id))
            .ToList();

        if (remainingItems.Count == 0)
        {
            remainingItems.Add(new PaperItem());
        }

        _paper.Items = remainingItems;
        NormalizeTodoItems();
        NormalizeOrders();

        var focus = remainingItems.FirstOrDefault(i => i.Id == focusedId)?.Id
            ?? remainingItems.FirstOrDefault(i => !IsBlank(i))?.Id
            ?? remainingItems.FirstOrDefault()?.Id;

        _controller.MarkDirty();

        // 批量消失动画
        if (_controller.State.EnableAnimations && completedItems.Count > 0)
        {
            var animatedRows = completedItems
                .Take(15)
                .Select(item => _todoRows.FirstOrDefault(r => (string)r.Tag == item.Id))
                .Where(row => row != null)
                .Cast<Border>()
                .ToList();

            if (animatedRows.Count > 0)
            {
                var rowGeneration = _todoRowsGeneration;
                for (int i = 0; i < animatedRows.Count; i++)
                {
                    var row = animatedRows[i];
                    row.IsHitTestVisible = false;
                    var delay = i * 30;
                    void StartRowAnimation()
                    {
                        if (clearDoneGeneration != _clearDoneGeneration ||
                            rowGeneration != _todoRowsGeneration ||
                            !_todoRows.Contains(row))
                        {
                            return;
                        }

                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                        var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(180))
                        {
                            EasingFunction = AnimationHelper.QuickEase
                        };

                        row.BeginAnimation(OpacityProperty, fadeOut);
                        AnimationHelper.GetTranslateTransform(row).BeginAnimation(TranslateTransform.XProperty, slideOut);
                    }

                    if (delay == 0)
                    {
                        StartRowAnimation();
                        continue;
                    }

                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(delay)
                    };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        StartRowAnimation();
                    };
                    timer.Start();
                }

                var finalizeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(((animatedRows.Count - 1) * 30) + 180)
                };
                finalizeTimer.Tick += (_, _) =>
                {
                    finalizeTimer.Stop();
                    if (clearDoneGeneration == _clearDoneGeneration &&
                        rowGeneration == _todoRowsGeneration)
                    {
                        RebuildTodoRows(focus);
                    }
                };
                finalizeTimer.Start();
                return;
            }
        }

        RebuildTodoRows(focus);
    }

    public bool TryHitTodoRow(Point screenPoint, out string? itemId)
    {
        itemId = null;
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || _paper.IsCollapsed || !IsVisible)
        {
            return false;
        }

        foreach (var row in _todoRows)
        {
            if (row.Tag is not string rowItemId || !row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var point = row.PointFromScreen(screenPoint);
            if (point.X < 0 || point.X > row.ActualWidth || point.Y < 0 || point.Y > row.ActualHeight)
            {
                continue;
            }

            itemId = rowItemId;
            return true;
        }

        return false;
    }

    public void SetNoteLinkDropTarget(string? itemId)
    {
        if (_linkedNoteDropRow?.Tag is string currentId &&
            string.Equals(currentId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        ClearNoteLinkDropTargetVisual();

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var row = _todoRows.FirstOrDefault(r =>
            r.Tag is string rowItemId &&
            string.Equals(rowItemId, itemId, StringComparison.Ordinal));
        if (row == null)
        {
            return;
        }

        _linkedNoteDropRow = row;
        row.Background = NoteLinkTargetBgBrush;
        row.BorderBrush = NoteLinkTargetBorderBrush;
        row.BorderThickness = new Thickness(1);
        row.Padding = new Thickness(1, 3, 1, 3);
    }

    public bool LinkNoteToTodo(string itemId, string noteId)
    {
        if (!_controller.State.EnableTodoNoteLinks || _paper.Type != PaperTypes.Todo || !_controller.IsExistingNote(noteId))
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return false;
        }

        if (string.Equals(item.LinkedNoteId, noteId, StringComparison.Ordinal))
        {
            return true;
        }

        var focusedId = CurrentFocusedTodoItemId();
        PushUndoSnapshot();
        item.LinkedNoteId = noteId;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
        return true;
    }

    private void UnlinkNoteFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedNoteId))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.LinkedNoteId = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        _controller.RefreshCapsuleEligibilityForLinkedNotes();
    }

    private void ClearNoteLinkDropTargetVisual()
    {
        var row = _linkedNoteDropRow;
        if (row == null)
        {
            return;
        }

        _linkedNoteDropRow = null;
        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (!Equals(_activeDropRow, row))
        {
            row.Background = row.IsMouseOver ? HoverBrush : Brushes.Transparent;
        }
    }







    private PaperItem? PreviousItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index > 0 ? ordered[index - 1] : null;
    }

    private PaperItem? NextItem(PaperItem item)
    {
        var ordered = OrderedItems().ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        return index >= 0 && index < ordered.Count - 1 ? ordered[index + 1] : null;
    }

    private void BeginTodoMouseDrag()
    {
        if (_todoDrag == null)
        {
            return;
        }

        _todoDrag.IsDragging = true;

        var rowOrigin = _todoDrag.SourceRow.TranslatePoint(new Point(0, 0), this);
        _todoDrag.MouseOffsetInRow = new Point(
            Math.Max(0, _todoDrag.StartPoint.X - rowOrigin.X),
            Math.Max(0, _todoDrag.StartPoint.Y - rowOrigin.Y));

        _todoDrag.SourceRow.Opacity = 0.25;
        _todoDrag.SourceRow.Background = HoverBrush;
        _todoDrag.Handle.Opacity = 0.9;
        Mouse.OverrideCursor = Cursors.SizeAll;

        _todoDrag.Ghost = CreateTodoDragGhost(_todoDrag);
        _dragLayer?.Children.Add(_todoDrag.Ghost);
        UpdateTodoDragGhost(_todoDrag, _todoDrag.StartPoint);

        ShowAppendAreaAsTrashBin(active: true);
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoMouseDrag(commit: _todoDrag.IsDragging);
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(this);

        if (!_todoDrag.IsDragging)
        {
            var movedEnough =
                Math.Abs(current.X - _todoDrag.StartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _todoDrag.StartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

            if (!movedEnough)
            {
                return;
            }

            BeginTodoMouseDrag();
        }

        var panelPoint = _todoPanel != null ? e.GetPosition(_todoPanel) : current;
        UpdateTodoMouseDrag(panelPoint, current);
        e.Handled = true;
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_todoDrag == null)
        {
            return;
        }

        EndTodoMouseDrag(commit: _todoDrag.IsDragging);
        e.Handled = true;
    }

    private void UpdateTodoMouseDrag(Point pointOnPanel, Point pointOnWindow)
    {
        if (_todoDrag == null || _todoPanel == null)
        {
            return;
        }

        UpdateTodoDragGhost(_todoDrag, pointOnWindow);
        ClearActiveDropIndicator();

        bool overTrash = false;
        if (_appendArea != null && _appendArea.IsVisible)
        {
            try
            {
                var transform = this.TransformToVisual(_appendArea);
                Point posInAppend = transform.Transform(pointOnWindow);
                if (posInAppend.X >= 0 && posInAppend.X <= _appendArea.ActualWidth &&
                    posInAppend.Y >= 0 && posInAppend.Y <= _appendArea.ActualHeight)
                {
                    overTrash = true;
                }
            }
            catch
            {
                // Fallback in case layout is not fully updated
            }
        }

        if (overTrash)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = true;
            ShowAppendAreaAsTrashBin(active: true, hovered: true);
            return;
        }

        ShowAppendAreaAsTrashBin(active: true, hovered: false);

        var candidates = _todoRows
            .Where(row => row.Tag is string id && id != _todoDrag.ItemId)
            .ToList();

        if (candidates.Count == 0)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
            return;
        }

        double bestDist = double.MaxValue;
        Border? bestRow = null;
        var bestPlacement = DropPlacement.After;

        foreach (var row in candidates)
        {
            double top = row.TranslatePoint(new Point(0, 0), _todoPanel).Y;
            ConsiderDropBoundary(row, DropPlacement.Before, top);
            ConsiderDropBoundary(row, DropPlacement.After, top + row.ActualHeight);
        }

        if (bestRow == null)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
            return;
        }

        ShowDropIndicator(bestRow, bestPlacement);
        _todoDrag.TargetId = bestRow.Tag as string;
        _todoDrag.TargetPlacement = bestPlacement;
        _todoDrag.DropAtEnd = false;

        void ConsiderDropBoundary(Border row, DropPlacement placement, double y)
        {
            double dist = Math.Abs(pointOnPanel.Y - y);
            if (dist >= bestDist)
            {
                return;
            }

            bestDist = dist;
            bestRow = row;
            bestPlacement = placement;
        }
    }

    private Border CreateTodoDragGhost(TodoDragState state)
    {
        var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
        var text = item?.Text ?? "";
        var done = item?.Done == true;

        var ghost = new Border
        {
            Width = Math.Max(state.SourceRow.ActualWidth, 160),
            MinHeight = Math.Max(state.SourceRow.ActualHeight, 30),
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = PaperBrush,
            BorderBrush = Theme.Tint(150),
            BorderThickness = new Thickness(1),
            Opacity = 0.65,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.24
            }
        };

        var grid = new Grid
        {
            IsHitTestVisible = false
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        var check = new TextBlock
        {
            Text = done ? "☑" : "☐",
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var content = new TextBlock
        {
            Text = text,
            Foreground = done ? BrightWeakTextBrush : TextBrush,
            FontSize = 14,
            Padding = new Thickness(2, 3, 2, 3),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (done)
        {
            content.TextDecorations = TextDecorations.Strikethrough;
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var handle = new TextBlock
        {
            Text = "≡",
            Foreground = WeakTextBrush,
            Opacity = 0.58,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(handle, 2);
        grid.Children.Add(handle);

        ghost.Child = grid;
        return ghost;
    }

    private void CloseTodoDragGhost(TodoDragState state)
    {
        if (state.Ghost == null)
        {
            return;
        }

        _dragLayer?.Children.Remove(state.Ghost);
        state.Ghost = null;
    }

    private static void UpdateTodoDragGhost(TodoDragState state, Point pointOnWindow)
    {
        if (state.Ghost == null)
        {
            return;
        }

        Canvas.SetLeft(state.Ghost, pointOnWindow.X - state.MouseOffsetInRow.X);
        Canvas.SetTop(state.Ghost, pointOnWindow.Y - state.MouseOffsetInRow.Y);
    }

    private void EndTodoMouseDrag(bool commit)
    {
        var state = _todoDrag;
        if (state == null)
        {
            return;
        }

        _todoDrag = null;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Mouse.OverrideCursor = null;

        CloseTodoDragGhost(state);

        state.SourceRow.Opacity = 1.0;
        state.SourceRow.Background = Brushes.Transparent;
        state.Handle.Opacity = 1.0;

        ClearActiveDropIndicator();
        ShowAppendAreaAsTrashBin(active: false);

        if (!commit)
        {
            RebuildTodoRows(state.ItemId);
            return;
        }

        if (state.DropAtEnd)
        {
            var item = _paper.Items.FirstOrDefault(i => i.Id == state.ItemId);
            if (item != null)
            {
                RemoveItem(item, rebuild: true);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetId))
        {
            MoveItem(state.ItemId, state.TargetId, state.TargetPlacement, focusDragged: true);
            return;
        }

        RebuildTodoRows(state.ItemId);
    }

    private void MoveItemBefore(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.Before, focusDragged);
    }

    private void MoveItemAfter(string draggedId, string targetId, bool focusDragged = true)
    {
        MoveItem(draggedId, targetId, DropPlacement.After, focusDragged);
    }

    private void MoveItem(string draggedId, string targetId, DropPlacement placement, bool focusDragged)
    {
        if (draggedId == targetId)
        {
            return;
        }

        var ordered = OrderedItems().ToList();
        var originalOrder = ordered.Select(i => i.Id).ToList();

        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        var target = ordered.FirstOrDefault(i => i.Id == targetId);

        if (dragged == null || target == null)
        {
            return;
        }

        ordered.Remove(dragged);

        var targetIndex = ordered.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (placement == DropPlacement.After)
        {
            targetIndex++;
        }

        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(targetIndex, dragged);

        if (originalOrder.SequenceEqual(ordered.Select(i => i.Id)))
        {
            return;
        }

        PushUndoSnapshot();
        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusDragged ? dragged.Id : null);
    }

    private void MoveItemToEnd(string draggedId, bool focusDragged = true)
    {
        var ordered = OrderedItems().ToList();
        var dragged = ordered.FirstOrDefault(i => i.Id == draggedId);
        if (dragged == null)
        {
            return;
        }

        var oldIndex = ordered.IndexOf(dragged);
        if (oldIndex == ordered.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        ordered.Remove(dragged);
        ordered.Add(dragged);

        _paper.Items = ordered;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        RebuildTodoRows(focusDragged ? dragged.Id : null);
    }

    private IEnumerable<PaperItem> OrderedItems()
    {
        return _paper.Items.OrderBy(i => i.Order).ToList();
    }

    private void NormalizeTodoItems()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var ordered = _paper.Items.ToList();
        if (ordered.Count == 0)
        {
            ordered.Add(new PaperItem());
        }

        _paper.Items = ordered;
    }

    private static bool IsBlank(PaperItem item)
    {
        return string.IsNullOrWhiteSpace(item.Text);
    }

    private string? CurrentFocusedTodoItemId()
    {
        var focused = FocusManager.GetFocusedElement(this);

        if (focused is TodoTextBox box)
        {
            foreach (var pair in _todoEditors)
            {
                if (ReferenceEquals(pair.Value, box))
                {
                    return pair.Key;
                }
            }
        }

        return null;
    }

    private void NormalizeOrders()
    {
        // Preserve the current list order. Sorting here would undo freshly inserted
        // or dragged rows because new items start with Order = 0 until we renumber them.
        for (var i = 0; i < _paper.Items.Count; i++)
        {
            _paper.Items[i].Order = i;
        }
    }

    private void ShowDropIndicator(Border row, DropPlacement placement)
    {
        if (!Equals(_activeDropRow, row))
        {
            ClearActiveDropIndicator();
            _activeDropRow = row;
        }

        if (_dragLayer == null)
        {
            return;
        }

        if (_dropIndicatorLine == null)
        {
            _dropIndicatorLine = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = DropIndicatorBrush,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_dropIndicatorLine, 1001);
            _dragLayer.Children.Add(_dropIndicatorLine);
        }

        _dropIndicatorLine.Background = DropIndicatorBrush;
        var rowOrigin = row.TranslatePoint(new Point(0, 0), _dragLayer);
        var y = placement == DropPlacement.Before
            ? rowOrigin.Y
            : rowOrigin.Y + row.ActualHeight;
        var width = Math.Max(24, row.ActualWidth - 8);

        _dropIndicatorLine.Width = width;
        Canvas.SetLeft(_dropIndicatorLine, rowOrigin.X + 4);
        Canvas.SetTop(_dropIndicatorLine, y - (_dropIndicatorLine.Height / 2));
    }

    private void ClearDropIndicator(Border row)
    {
        if (Equals(_activeDropRow, row))
        {
            _activeDropRow = null;
        }

        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);

        if (_dropIndicatorLine != null)
        {
            _dragLayer?.Children.Remove(_dropIndicatorLine);
            _dropIndicatorLine = null;
        }
    }

    private void ClearActiveDropIndicator()
    {
        if (_activeDropRow != null)
        {
            ClearDropIndicator(_activeDropRow);
            _activeDropRow = null;
        }
    }

    private static void ReplaceSelection(TextBox box, string replacement)
    {
        box.SelectedText = replacement;
        box.SelectionStart = box.SelectionStart + replacement.Length;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        EndNoteLinkMouseGesture(commit: false);
        if (_closeForReal)
        {
            CloseExpandedDeepCapsuleSlotHostForReal();
            return;
        }

        e.Cancel = true;
        _controller.HidePaper(_paper);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_todoDrag != null && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoMouseDrag(commit: false);
        }
    }

    private static DependencyObject? GetSafeParent(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        if (current is FrameworkContentElement fce)
        {
            return fce.Parent;
        }

        if (current is ContentElement ce)
        {
            return ContentOperations.GetParent(ce);
        }

        return null;
    }

    private static Button IconButton(string text, string tooltip)
    {
        return new Button
        {
            Content = text,
            ToolTip = tooltip,
            Width = 28,
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Style = SharedIconButtonStyle
        };
    }

    private static ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu
        {
            Padding = new Thickness(4, 4, 4, 4),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            HasDropShadow = true,
            Template = SharedContextMenuTemplate
        };
        menu.Resources["PaperBrushKey"] = PaperBrush;
        menu.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        menu.Resources["TextBrushKey"] = TextBrush;
        menu.Resources["WeakTextBrushKey"] = WeakTextBrush;
        menu.Resources["HoverBrushKey"] = HoverBrush;
        menu.Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        menu.Background = PaperBrush;
        menu.BorderBrush = PaperBorderBrush;
        menu.Foreground = TextBrush;

        menu.Resources.Add(typeof(MenuItem), SharedCompactMenuItemStyle);
        return menu;
    }

    private static Separator MenuSeparator()
    {
        return new Separator
        {
            Margin = new Thickness(8, 3, 8, 3),
            Opacity = 0.38
        };
    }

    private static MenuItem MenuHeader(string header)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = false,
            Padding = new Thickness(8, 2, 10, 2),
            Background = Brushes.Transparent,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        item.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        return item;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem
        {
            Header = header,
            Padding = new Thickness(8, 4, 10, 4),
            Background = Brushes.Transparent
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextBrushKey");
        item.Click += click;
        return item;
    }

    private static List<PaperItem> CloneItems(List<PaperItem> items)
    {
        return items.Select(i => new PaperItem
        {
            Id = i.Id,
            Text = i.Text,
            Done = i.Done,
            Order = i.Order,
            LinkedNoteId = i.LinkedNoteId
        }).ToList();
    }

    private void PushUndoSnapshot()
    {
        CommitFocusedTextIfNeeded();

        _undoStack.Add(CloneItems(_paper.Items));
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
    }

    private void CommitFocusedTextIfNeeded()
    {
        var focusedId = CurrentFocusedTodoItemId();
        if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
        {
            if (_activeOriginalItemId == focusedId && _activeOriginalText != null && box.Text != _activeOriginalText)
            {
                var item = _paper.Items.FirstOrDefault(i => i.Id == focusedId);
                if (item != null)
                {
                    var oldText = item.Text;
                    item.Text = _activeOriginalText;

                    var oldSnapshot = CloneItems(_paper.Items);
                    _undoStack.Add(oldSnapshot);
                    if (_undoStack.Count > MaxUndoDepth)
                    {
                        _undoStack.RemoveAt(0);
                    }

                    item.Text = oldText;
                    _activeOriginalText = oldText;
                }
            }
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _redoStack.Add(currentItems);

        var previousItems = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        // 找出变化的项ID
        var changedIds = new HashSet<string>();
        foreach (var prevItem in previousItems)
        {
            var currentItem = currentItems.FirstOrDefault(i => i.Id == prevItem.Id);
            if (currentItem != null && (currentItem.Text != prevItem.Text || currentItem.Done != prevItem.Done))
            {
                changedIds.Add(prevItem.Id);
            }
        }
        foreach (var currItem in currentItems)
        {
            if (!previousItems.Any(i => i.Id == currItem.Id))
            {
                changedIds.Add(currItem.Id);
            }
        }

        _paper.Items = previousItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);

        // 闪烁高亮变化的行
        if (_controller.State.EnableAnimations && changedIds.Count > 0)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var id in changedIds.Take(10)) // 最多高亮10个
                {
                    var row = _todoRows.FirstOrDefault(r => (string)r.Tag == id);
                    if (row != null)
                    {
                        AnimationHelper.FlashHighlight(row, Colors.Yellow, 120);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId();

        var currentItems = CloneItems(_paper.Items);
        _undoStack.Add(currentItems);

        var nextItems = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        // 找出变化的项ID
        var changedIds = new HashSet<string>();
        foreach (var nextItem in nextItems)
        {
            var currentItem = currentItems.FirstOrDefault(i => i.Id == nextItem.Id);
            if (currentItem == null || currentItem.Text != nextItem.Text || currentItem.Done != nextItem.Done)
            {
                changedIds.Add(nextItem.Id);
            }
        }
        foreach (var currItem in currentItems)
        {
            if (!nextItems.Any(i => i.Id == currItem.Id))
            {
                changedIds.Add(currItem.Id);
            }
        }

        _paper.Items = nextItems;
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();

        RebuildTodoRows(focusedId);

        // 闪烁高亮变化的行
        if (_controller.State.EnableAnimations && changedIds.Count > 0)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var id in changedIds.Take(10))
                {
                    var row = _todoRows.FirstOrDefault(r => (string)r.Tag == id);
                    if (row != null)
                    {
                        AnimationHelper.FlashHighlight(row, Colors.Yellow, 120);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_paper.Type == PaperTypes.Note)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanUndo)
                    {
                        return;
                    }
                }

                Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                var focusedId = CurrentFocusedTodoItemId();
                if (focusedId != null && _todoEditors.TryGetValue(focusedId, out var box))
                {
                    if (box.CanRedo)
                    {
                        return;
                    }
                }

                Redo();
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            _suppressTodoBackspaceUntilKeyUp = false;
        }
    }

    public static readonly DependencyProperty TransitionProgressProperty =
        DependencyProperty.Register(
            nameof(TransitionProgress),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(0.0, OnTransitionProgressChanged));

    public double TransitionProgress
    {
        get => (double)GetValue(TransitionProgressProperty);
        set => SetValue(TransitionProgressProperty, value);
    }

    private static readonly DependencyProperty DeepCapsuleAnimatedLeftProperty =
        DependencyProperty.Register(
            nameof(DeepCapsuleAnimatedLeft),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(double.NaN, OnDeepCapsuleAnimatedLeftChanged));

    private double DeepCapsuleAnimatedLeft
    {
        get => (double)GetValue(DeepCapsuleAnimatedLeftProperty);
        set => SetValue(DeepCapsuleAnimatedLeftProperty, value);
    }

    private static readonly DependencyProperty DeepCapsuleAnimatedTopProperty =
        DependencyProperty.Register(
            nameof(DeepCapsuleAnimatedTop),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(double.NaN, OnDeepCapsuleAnimatedTopChanged));

    private double DeepCapsuleAnimatedTop
    {
        get => (double)GetValue(DeepCapsuleAnimatedTopProperty);
        set => SetValue(DeepCapsuleAnimatedTopProperty, value);
    }

    private static readonly DependencyProperty DeepCapsuleSlotHorizontalProgressProperty =
        DependencyProperty.Register(
            nameof(DeepCapsuleSlotHorizontalProgress),
            typeof(double),
            typeof(PaperWindow),
            new PropertyMetadata(double.NaN, OnDeepCapsuleSlotHorizontalProgressChanged));

    private double DeepCapsuleSlotHorizontalProgress
    {
        get => (double)GetValue(DeepCapsuleSlotHorizontalProgressProperty);
        set => SetValue(DeepCapsuleSlotHorizontalProgressProperty, value);
    }

    private static void OnTransitionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PaperWindow window)
        {
            window.UpdateTransitionVisuals((double)e.NewValue);
        }
    }

    private static void OnDeepCapsuleAnimatedLeftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PaperWindow window || e.NewValue is not double left || double.IsNaN(left) || double.IsInfinity(left))
        {
            return;
        }

        window.MoveWindowWithoutGeometrySave(() => window.Left = window.RoundToDevicePixelX(left));
    }

    private static void OnDeepCapsuleAnimatedTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PaperWindow window || e.NewValue is not double top || double.IsNaN(top) || double.IsInfinity(top))
        {
            return;
        }

        window.MoveWindowWithoutGeometrySave(() => window.Top = window.RoundToDevicePixelY(top));
    }

    private static void OnDeepCapsuleSlotHorizontalProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PaperWindow window || e.NewValue is not double progress || double.IsNaN(progress) || double.IsInfinity(progress))
        {
            return;
        }

        window.ApplyDeepCapsuleSlotHorizontalProgress(progress);
    }

    private void UpdateTransitionVisuals(double progress)
    {
        if (!_isTransitionVisualsActive)
        {
            return;
        }

        var currentProgress = double.IsNaN(progress) || double.IsInfinity(progress)
            ? 0.0
            : Math.Clamp(progress, 0.0, 1.0);

        var visualWidth = _startTransitionWidth + (_targetTransitionWidth - _startTransitionWidth) * currentProgress;
        var visualHeight = _startTransitionHeight + (_targetTransitionHeight - _startTransitionHeight) * currentProgress;
        var visualChromeWidth = Math.Max(1.0, visualWidth - WindowChromeInset);
        var visualChromeHeight = Math.Max(1.0, visualHeight - WindowChromeInset);
        var baseChromeWidth = Math.Max(1.0, _transitionBaseWidth - WindowChromeInset);
        var baseChromeHeight = Math.Max(1.0, _transitionBaseHeight - WindowChromeInset);

        _paperChrome.HorizontalAlignment = HorizontalAlignment.Left;
        _paperChrome.VerticalAlignment = VerticalAlignment.Top;
        _paperChrome.Width = visualChromeWidth;
        _paperChrome.Height = visualChromeHeight;
        _shellScale.ScaleX = Math.Max(0.01, visualChromeWidth / baseChromeWidth);
        _shellScale.ScaleY = Math.Max(0.01, visualChromeHeight / baseChromeHeight);
        UpdateTransitionCornerRadius(visualChromeWidth, visualChromeHeight, baseChromeWidth, baseChromeHeight);
    }

    private void ResetTransitionVisuals()
    {
        _isTransitionVisualsActive = false;
        _paperChrome.Width = double.NaN;
        _paperChrome.Height = double.NaN;
        _paperChrome.HorizontalAlignment = HorizontalAlignment.Stretch;
        _paperChrome.VerticalAlignment = VerticalAlignment.Stretch;
        _shellScale.ScaleX = 1.0;
        _shellScale.ScaleY = 1.0;
        _paperChrome.CornerRadius = PaperChromeCornerRadiusForState(_paper.IsCollapsed && _controller.State.UseCapsuleMode);
    }

    private void UpdateTransitionCornerRadius(
        double visualChromeWidth,
        double visualChromeHeight,
        double baseChromeWidth,
        double baseChromeHeight)
    {
        var visualChromeMin = Math.Min(visualChromeWidth, visualChromeHeight);
        var expandedChromeMin = Math.Max(1.0, Math.Min(baseChromeWidth, baseChromeHeight));
        var capsuleChromeMin = Math.Max(
            1.0,
            Math.Min(
                PaperLayoutDefaults.CapsuleWidth - WindowChromeInset,
                PaperLayoutDefaults.CapsuleHeight - WindowChromeInset));
        var compactRange = Math.Max(1.0, expandedChromeMin - capsuleChromeMin);
        var compactness = Math.Clamp((expandedChromeMin - visualChromeMin) / compactRange, 0.0, 1.0);
        var compactVisualRadius = Math.Min(CapsuleChromeCornerRadius, visualChromeMin / 2.0);
        var desiredVisualRadius = ExpandedChromeCornerRadius + (compactVisualRadius - ExpandedChromeCornerRadius) * compactness;

        _paperChrome.CornerRadius = new CornerRadius(desiredVisualRadius);
    }

    private static CornerRadius PaperChromeCornerRadiusForState(bool collapsed)
    {
        return new CornerRadius(collapsed ? CapsuleChromeCornerRadius : ExpandedChromeCornerRadius);
    }

    private double CapsuleWindowWidth()
    {
        return CapsuleWindowWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleWindowWidth(bool usesDeepCapsulePresentation)
    {
        var minWidth = usesDeepCapsulePresentation ? PaperLayoutDefaults.CapsuleWidth : CapsuleNormalMinWidth;
        return Math.Max(minWidth, CapsuleShellWidth(usesDeepCapsulePresentation) + WindowChromeInset);
    }

    private double CapsuleShellWidth()
    {
        return CapsuleShellWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleShellWidth(bool usesDeepCapsulePresentation)
    {
        return Math.Ceiling(CapsuleLeftPadding + MeasureCapsuleIconWidth() + CapsuleIconGap + MeasureCapsuleTitleWidth() + CapsuleCloseWidthForPlacement(usesDeepCapsulePresentation) + CapsuleRightPadding);
    }

    private double CapsuleCloseWidthForCurrentPlacement()
    {
        return CapsuleCloseWidthForPlacement(UsesDeepCapsulePresentation);
    }

    private static double CapsuleCloseWidthForPlacement(bool usesDeepCapsulePresentation)
    {
        return usesDeepCapsulePresentation ? CapsuleCloseWidth : CapsuleNormalCloseWidth;
    }

    private bool UsesDeepCapsulePresentation => false;

    // The pill window clamps to a minimum width (CapsuleWidth), so for short titles the pill is
    // wider than the raw content. The shell must always fill the pill interior, otherwise it is
    // left-aligned inside the pill and the close button's rounded right corner floats off the
    // pill's actual curve. Pill interior = window width minus the chrome margin on both sides.
    private double CapsuleShellLayoutWidth()
    {
        return CapsuleShellLayoutWidth(UsesDeepCapsulePresentation);
    }

    private double CapsuleShellLayoutWidth(bool usesDeepCapsulePresentation)
    {
        return Math.Max(CapsuleShellWidth(usesDeepCapsulePresentation), CapsuleWindowWidth(usesDeepCapsulePresentation) - WindowChromeInset);
    }

    private double MeasureCapsuleTitleWidth()
    {
        return MeasureCapsuleTextWidth(_controller.PaperCapsuleTitle(_paper), CapsuleLabelFontSize, FontWeights.Normal);
    }

    // The capsule icon glyph (✓ / ✎) is not a fixed box — its rendered advance width depends
    // on the font and weight. Measure it with the same SemiBold weight it renders at.
    private double MeasureCapsuleIconWidth()
    {
        return MeasureCapsuleTextWidth(_paper.Type == PaperTypes.Note ? "✎" : "✓", CapsuleIconFontSize, FontWeights.SemiBold);
    }

    // Single source of truth for "how wide does this text actually render". Uses the same
    // font family (NoteTypography) and weight the capsule icon/label are bound to, so
    // measurement and rendering never disagree — digits and halfwidth chars get their true
    // advance width.
    private double MeasureCapsuleTextWidth(string text, double fontSize, FontWeight weight)
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
                new Typeface(NoteTypography.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                fontSize,
                WeakTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return text.Length * fontSize;
        }
    }

    private double RoundToDevicePixelX(double value)
    {
        return RoundToDevicePixel(value, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private double RoundToDevicePixelY(double value)
    {
        return RoundToDevicePixel(value, VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    private static double RoundToDevicePixel(double value, double scale)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || scale <= 0)
        {
            return value;
        }

        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    private void SaveGeometryIfAllowed()
    {
        if (_isApplyingCollapsedState || SuppressGeometrySave)
        {
            return;
        }

        _controller.UpdateGeometry(_paper, this);
    }

    private void MoveWindowWithoutGeometrySave(Action move)
    {
        var wasSuppressing = _suppressGeometrySave;
        _suppressGeometrySave = true;
        try
        {
            move();
        }
        finally
        {
            _suppressGeometrySave = wasSuppressing;
        }
    }

    private void ClearDeepCapsulePositionAnimation()
    {
        ClearDeepCapsuleLeftPositionAnimation();
        ClearDeepCapsuleTopPositionAnimation();
    }

    private void ClearDeepCapsuleLeftPositionAnimation()
    {
        BeginAnimation(DeepCapsuleAnimatedLeftProperty, null);
        BeginAnimation(Window.LeftProperty, null);
    }

    private void ClearDeepCapsuleTopPositionAnimation()
    {
        BeginAnimation(DeepCapsuleAnimatedTopProperty, null);
        BeginAnimation(Window.TopProperty, null);
    }

    private void ClearDeepCapsuleSlotHorizontalAnimation()
    {
        BeginAnimation(DeepCapsuleSlotHorizontalProgressProperty, null);
    }

    private bool IsDeepCapsuleSlotHorizontalAnimating => !double.IsNaN(DeepCapsuleSlotHorizontalProgress);

    private static Rect DeepCapsuleWorkArea()
    {
        return DeepCapsuleLayout.WorkArea;
    }

    private void ApplyDeepCapsuleSlotHostViewport(double viewportWidth)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        _deepCapsuleSlotHost.Width = DeepCapsuleSlotViewportWidth(viewportWidth);
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        ApplyDeepCapsuleSlotFixedLayout();
    }

    private void ApplyDeepCapsuleSlotFixedLayout()
    {
        var fullWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;

        if (_deepCapsuleSlotChrome != null)
        {
            _deepCapsuleSlotChrome.Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotChrome.Width = Math.Max(0, fullWidth - WindowChromeMargin);
        }
        if (_deepCapsuleSlotShell != null)
        {
            _deepCapsuleSlotShell.Margin = new Thickness(WindowChromeMargin, WindowChromeMargin, 0, WindowChromeMargin);
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }
        if (_deepCapsuleSlotOutline != null)
        {
            _deepCapsuleSlotOutline.Margin = new Thickness(outlineMargin, outlineMargin, 0, outlineMargin);
            _deepCapsuleSlotOutline.Width = Math.Max(0, fullWidth - outlineMargin);
        }
    }

    private void ApplyDeepCapsuleSlotHorizontalProgress(double progress)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        progress = Math.Clamp(progress, 0.0, 1.0);
        var viewportWidth = Lerp(_deepCapsuleSlotStartViewportWidth, _deepCapsuleSlotTargetViewportWidth, progress);
        var anchorRight = _deepCapsuleSlotTargetLeft + _deepCapsuleSlotTargetViewportWidth;
        var left = anchorRight - viewportWidth;

        _deepCapsuleSlotHost.Left = RoundToDevicePixelX(left);
        _deepCapsuleSlotHost.Width = DeepCapsuleSlotViewportWidth(RoundToDevicePixelX(viewportWidth));
        _deepCapsuleSlotHost.Height = PaperLayoutDefaults.CapsuleHeight;
        _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private double DeepCapsuleSlotShellLayoutWidth()
    {
        var fullWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        return Math.Max(0, Math.Max(
            CapsuleShellWidth(usesDeepCapsulePresentation: true),
            fullWidth - WindowChromeMargin));
    }

    private double DeepCapsuleSlotViewportWidth(double viewportWidth)
    {
        return Math.Clamp(viewportWidth, 1, CapsuleWindowWidth(usesDeepCapsulePresentation: true));
    }

    private double DeepCapsuleTopForIndex(int index)
    {
        return DeepCapsuleLayout.TopForIndex(index, _controller.State.DeepCapsuleStartTopMargin);
    }

    private void MoveDeepCapsuleToCurrentTarget(
        bool animate = false,
        int durationMs = DeepCapsuleLayout.SlotMoveMilliseconds,
        bool keepHiding = false,
        bool forceRestingOffset = false)
    {
        if (!HasDeepCapsuleSlotPlacement || _isCollapseAllRetracted)
        {
            return;
        }

        var area = DeepCapsuleWorkArea();
        var capsuleWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        var deepCapsuleVisibleWidth = DeepCapsuleVisibleWidth();
        var shouldUseActiveOffset = !keepHiding &&
            !forceRestingOffset &&
            (_deepCapsuleVisualState is DeepCapsuleVisualState.Hovered or DeepCapsuleVisualState.Active);
        var visibleWidth = shouldUseActiveOffset
            ? ExpandedDeepCapsuleVisibleWidth()
            : deepCapsuleVisibleWidth;
        var targetLeft = RoundToDevicePixelX(area.Right - visibleWidth);
        var targetTop = RoundToDevicePixelY(DeepCapsuleTopForIndex(_deepCapsuleIndex + _deepCapsuleVisualOffset));

        MoveExpandedDeepCapsuleSlotHost(
            targetLeft,
            targetTop,
            visibleWidth,
            animate,
            durationMs,
            keepHiding);
    }

    private double DeepCapsuleVisibleWidth()
    {
        var capsuleWidth = CapsuleWindowWidth(usesDeepCapsulePresentation: true);
        // Resting edge-attached state is a docked tag, not a cropped full capsule. Keep the
        // close area fully off-screen and size the visible part to the icon + title only.
        return Math.Clamp(
            WindowChromeMargin + CapsuleLeftPadding + MeasureCapsuleIconWidth() + CapsuleIconGap + MeasureCapsuleTitleWidth() + 4,
            34,
            Math.Max(34, capsuleWidth - WindowChromeMargin - 24));
    }

    private double ExpandedDeepCapsuleVisibleWidth()
    {
        return DeepCapsuleLayout.FocusVisibleWidth(
            CapsuleWindowWidth(usesDeepCapsulePresentation: true),
            DeepCapsuleVisibleWidth());
    }

    private bool IsLikelyAtDeepCapsuleEdge(double capsuleWidth)
    {
        if (!_paper.IsCollapsed || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            return false;
        }

        var area = DeepCapsuleWorkArea();
        var minVisibleWidth = Math.Min(DeepCapsuleVisibleWidth(), ExpandedDeepCapsuleVisibleWidth());
        var leftEdgeThreshold = area.Right - capsuleWidth - DeepCapsuleGap;
        var rightEdgeThreshold = area.Right - minVisibleWidth + DeepCapsuleGap;
        var withinVerticalStack = Top >= area.Top + DeepCapsuleTopMargin - DeepCapsuleGap
            && Top <= area.Bottom - PaperLayoutDefaults.CapsuleHeight + DeepCapsuleGap;
        return withinVerticalStack && Left >= leftEdgeThreshold && Left <= rightEdgeThreshold;
    }

    // Shared position animator for every deep-capsule move (slot placement, hover peek,
    // retract, release). A generation token guards completions so a superseded animation's
    // Completed handler never snaps the window to a stale target.
    private void BeginDeepCapsuleMove(double targetLeft, double targetTop, int leftDurationMs, int topDurationMs, bool animate)
    {
        MoveWindowWithoutGeometrySave(() =>
        {
            Width = CapsuleWindowWidth();
            Height = PaperLayoutDefaults.CapsuleHeight;
        });
        UpdateCapsuleClosePlacement();

        var gen = ++_deepCapsuleMoveGeneration;

        if (!animate)
        {
            ClearDeepCapsulePositionAnimation();
            MoveWindowWithoutGeometrySave(() =>
            {
                Left = targetLeft;
                Top = targetTop;
            });
            UpdateCapsuleClosePlacement();
            return;
        }

        var currentLeft = double.IsNaN(Left) || double.IsInfinity(Left) ? targetLeft : RoundToDevicePixelX(Left);
        var currentTop = double.IsNaN(Top) || double.IsInfinity(Top) ? targetTop : RoundToDevicePixelY(Top);
        var animateLeft = Math.Abs(currentLeft - targetLeft) >= 0.5;
        var animateTop = Math.Abs(currentTop - targetTop) >= 0.5;

        if (!animateLeft && !animateTop)
        {
            ClearDeepCapsulePositionAnimation();
            MoveWindowWithoutGeometrySave(() =>
            {
                Left = targetLeft;
                Top = targetTop;
            });
            UpdateCapsuleClosePlacement();
            return;
        }

        var easeOut = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };

        if (animateLeft)
        {
            var leftAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = currentLeft,
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(leftDurationMs),
                EasingFunction = easeOut
            };
            leftAnimation.Completed += (_, _) =>
            {
                if (gen != _deepCapsuleMoveGeneration)
                {
                    return;
                }

                MoveWindowWithoutGeometrySave(() =>
                {
                    ClearDeepCapsuleLeftPositionAnimation();
                    Left = targetLeft;
                });
                UpdateCapsuleClosePlacement();
            };

            BeginAnimation(DeepCapsuleAnimatedLeftProperty, leftAnimation, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleLeftPositionAnimation();
            MoveWindowWithoutGeometrySave(() => Left = targetLeft);
        }

        if (animateTop)
        {
            var topAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = currentTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(topDurationMs),
                EasingFunction = easeOut
            };
            topAnimation.Completed += (_, _) =>
            {
                if (gen != _deepCapsuleMoveGeneration)
                {
                    return;
                }

                MoveWindowWithoutGeometrySave(() =>
                {
                    ClearDeepCapsuleTopPositionAnimation();
                    Top = targetTop;
                });
            };

            BeginAnimation(DeepCapsuleAnimatedTopProperty, topAnimation, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            ClearDeepCapsuleTopPositionAnimation();
            MoveWindowWithoutGeometrySave(() => Top = targetTop);
        }
    }

    private void AnimateWindowOpacity(double to, bool animate)
    {
        if (!animate || Math.Abs(Opacity - to) < 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // Slide this capsule up to the master's slot and fade it out. The window stays shown
    // (so it keeps counting as a deep-capsule member) but, being a per-pixel transparent
    // window at Opacity 0, it is fully click-through and never blocks the master pill.
    public void RetractIntoMaster(double anchorTop, bool animate)
    {
        if (!OccupiesDeepCapsuleSlot || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode || !_paper.IsVisible)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
        _isCollapseAllRetracted = true;
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        RefreshEffectiveTopmost();

        var area = DeepCapsuleWorkArea();
        var currentSlotVisible = _deepCapsuleSlotHost?.IsVisible == true &&
            !double.IsNaN(_deepCapsuleSlotHost.Width) &&
            !double.IsInfinity(_deepCapsuleSlotHost.Width) &&
            _deepCapsuleSlotHost.Width > 0;
        var visibleWidth = currentSlotVisible
            ? DeepCapsuleSlotViewportWidth(_deepCapsuleSlotHost!.Width)
            : DeepCapsuleVisibleWidth();
        var targetLeft = currentSlotVisible &&
            !double.IsNaN(_deepCapsuleSlotHost!.Left) &&
            !double.IsInfinity(_deepCapsuleSlotHost.Left)
                ? RoundToDevicePixelX(_deepCapsuleSlotHost.Left)
                : RoundToDevicePixelX(area.Right - visibleWidth);
        var targetTop = RoundToDevicePixelY(anchorTop);

        MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate);
        if (_deepCapsuleSlotHost != null)
        {
            AnimateSlotHostOpacity(0.0, animate);
        }
        if (_paper.IsCollapsed)
        {
            HideMainWindowForDeepCapsuleRest();
        }
    }

    private void SetDeepCapsuleHover(bool hovering)
    {
        if (IsDeepCapsuleReordering || !HasDeepCapsuleSlotPlacement || !_paper.IsCollapsed || !_controller.State.UseDeepCapsuleMode)
        {
            return;
        }

        if (IsDeepCapsuleSlotActive)
        {
            return;
        }

        SetDeepCapsuleVisualState(hovering ? DeepCapsuleVisualState.Hovered : DeepCapsuleVisualState.Resting);
        MoveDeepCapsuleToCurrentTarget(animate: true);
    }

    public void ApplyDeepCapsulePlacement(int index, bool animate = false, int visualOffset = 0)
    {
        if (!_paper.IsCollapsed || !_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            ClearDeepCapsulePlacement();
            return;
        }

        var keepActiveUntilRetracted = animate &&
            IsDeepCapsuleSlotActive &&
            _deepCapsuleSlotHost?.IsVisible == true;

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
        if (!_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        RefreshCapsuleLabel();
        MoveDeepCapsuleToCurrentTarget(
            animate,
            keepActiveUntilRetracted ? 120 : DeepCapsuleLayout.SlotMoveMilliseconds,
            forceRestingOffset: keepActiveUntilRetracted);
        if (keepActiveUntilRetracted)
        {
            ClearDeepCapsuleSlotActiveAfterMove(120);
        }
        AnimateSlotHostOpacity(1.0, animate);
        if (!_isApplyingCollapsedState)
        {
            HideMainWindowForDeepCapsuleRest();
        }
        RefreshEffectiveTopmost();
    }

    public void ApplyExpandedDeepCapsuleSlotPlacement(int index, bool animate = false, int visualOffset = 0)
    {
        var shouldReserveWhileExpanded = _controller.State.ShowDeepCapsuleWhileExpanded &&
            _controller.CanPaperDisplayAsCapsule(_paper);
        if (_paper.IsCollapsed ||
            !shouldReserveWhileExpanded ||
            !_controller.State.UseCapsuleMode ||
            !_controller.State.UseDeepCapsuleMode ||
            !_paper.IsVisible)
        {
            ClearExpandedDeepCapsuleSlotPlacement();
            return;
        }

        SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
        SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
        _isCollapseAllRetracted = false;
        _deepCapsuleIndex = Math.Max(0, index);
        _deepCapsuleVisualOffset = Math.Max(0, visualOffset);
        RefreshCapsuleLabel();
        UpdateDeepCapsuleSlotHostTheme();

        var area = DeepCapsuleWorkArea();
        var visibleWidth = ExpandedDeepCapsuleVisibleWidth();
        var targetLeft = RoundToDevicePixelX(area.Right - visibleWidth);
        var targetTop = RoundToDevicePixelY(DeepCapsuleTopForIndex(index + visualOffset));
        RefreshDeepCapsuleSlotLabel();

        var firstShow = _deepCapsuleSlotHost?.IsVisible != true;
        if (firstShow)
        {
            MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate: false);
        }
        else
        {
            MoveExpandedDeepCapsuleSlotHost(targetLeft, targetTop, visibleWidth, animate);
        }
        UpdateDeepCapsuleSlotClosePlacement();
        AnimateSlotHostOpacity(1.0, animate);
        RefreshEffectiveTopmost();
        UpdateToolTipSetting();
    }

    public void ClearExpandedDeepCapsuleSlotPlacement(bool animate = false)
    {
        var wasActive = IsDeepCapsuleSlotActive;
        var keepActiveUntilRetracted = animate &&
            wasActive &&
            _paper.IsCollapsed &&
            HasDeepCapsuleSlotPlacement &&
            _deepCapsuleSlotHost?.IsVisible == true;
        _deepCapsuleSlotMoveGeneration++;
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        if (!_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (wasActive && !_isApplyingCollapsedState && !keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (keepActiveUntilRetracted)
        {
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        }
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.Retracting)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: !_paper.IsCollapsed || !HasDeepCapsuleSlotPlacement);

        if (_paper.IsCollapsed && HasDeepCapsuleSlotPlacement)
        {
            MoveDeepCapsuleToCurrentTarget(
                animate,
                keepActiveUntilRetracted ? 120 : DeepCapsuleLayout.SlotMoveMilliseconds,
                forceRestingOffset: keepActiveUntilRetracted);
        }
    }

    private void ClearDeepCapsuleSlotActiveAfterMove(int durationMs)
    {
        var generation = _deepCapsuleSlotMoveGeneration;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(durationMs + 20)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _deepCapsuleSlotMoveGeneration)
            {
                return;
            }

            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
        };
        timer.Start();
    }

    private void HideExpandedDeepCapsuleSlotHost(bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        if (!animate || !_deepCapsuleSlotHost.IsVisible || _deepCapsuleSlotHostRoot == null)
        {
            if (IsDeepCapsuleSlotRetracting)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            _deepCapsuleSlotHostRoot?.BeginAnimation(UIElement.OpacityProperty, null);
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
            }
            ClearDeepCapsuleSlotHorizontalAnimation();
            _deepCapsuleSlotHost.Hide();
            return;
        }

        if (IsDeepCapsuleSlotRetracting)
        {
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.Retracting);
        _deepCapsuleSlotHostRoot.IsHitTestVisible = false;
        var hideGeneration = _deepCapsuleSlotMoveGeneration;
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = _deepCapsuleSlotHostRoot.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (hideGeneration != _deepCapsuleSlotMoveGeneration || _deepCapsuleSlotHost == null)
            {
                return;
            }

            if (_deepCapsuleSlotState == DeepCapsuleSlotState.Retracting)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            _deepCapsuleSlotHostRoot?.BeginAnimation(UIElement.OpacityProperty, null);
            _deepCapsuleSlotHost.Hide();
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
                _deepCapsuleSlotHostRoot.IsHitTestVisible = true;
            }
        };
        _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void RetractAndHideDeepCapsuleSlotHost(bool animate)
    {
        if (_deepCapsuleSlotHost == null)
        {
            return;
        }

        var root = _deepCapsuleSlotHostRoot;
        if (!animate || !_deepCapsuleSlotHost.IsVisible || root == null || _deepCapsuleSlotHost.Opacity < 0.05)
        {
            HideExpandedDeepCapsuleSlotHost(animate: false);
            return;
        }

        SetDeepCapsuleSlotState(DeepCapsuleSlotState.Retracting);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
        UpdateDeepCapsuleSlotHostTheme();
        UpdateDeepCapsuleSlotClosePlacement(updateHostViewport: false);

        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1.0;
        root.IsHitTestVisible = false;

        MoveDeepCapsuleToCurrentTarget(animate: true, durationMs: 120, keepHiding: true);
        var generation = _deepCapsuleSlotMoveGeneration;

        var finishTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(125)
        };
        finishTimer.Tick += (_, _) =>
        {
            finishTimer.Stop();
            BeginDeepCapsuleSlotHideFade(generation);
        };
        finishTimer.Start();
    }

    private void BeginDeepCapsuleSlotHideFade(int generation)
    {
        if (_deepCapsuleSlotHostRoot == null)
        {
            return;
        }

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(45),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (generation != _deepCapsuleSlotMoveGeneration || _deepCapsuleSlotHost == null)
            {
                return;
            }

            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            _isCollapseAllRetracted = false;
            _deepCapsuleVisualOffset = 0;
            _deepCapsuleIndex = -1;
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
            _deepCapsuleSlotHost.Hide();
            if (_deepCapsuleSlotHostRoot != null)
            {
                _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, null);
                _deepCapsuleSlotHostRoot.Opacity = 1.0;
                _deepCapsuleSlotHostRoot.IsHitTestVisible = true;
            }
        };
        _deepCapsuleSlotHostRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    public void ClearDeepCapsulePlacement(bool restoreCollapsedPosition = true, bool animate = false)
    {
        var shouldRetractBeforeHide = animate &&
            _deepCapsuleSlotHost?.IsVisible == true &&
            _deepCapsuleSlotHostRoot != null &&
            HasDeepCapsuleSlotPlacement &&
            !_isCollapseAllRetracted;

        if (shouldRetractBeforeHide)
        {
            ClearDeepCapsulePositionAnimation();
            RetractAndHideDeepCapsuleSlotHost(animate: true);
        }
        else
        {
            ClearDeepCapsulePositionAnimation();
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            _isCollapseAllRetracted = false;
            _deepCapsuleVisualOffset = 0;
            _deepCapsuleIndex = -1;
            UpdateCapsuleClosePlacement();
            HideExpandedDeepCapsuleSlotHost(animate);
        }

        // A capsule may have been faded out while retracted behind the master; never leave
        // a live (expanded or free-floating) window invisible.
        if (Math.Abs(Opacity - 1.0) > 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }
    }

    public void ClearDeepCapsuleSlotReservation(bool animate = false)
    {
        if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
        {
            SetDeepCapsuleSlotState(_paper.IsCollapsed ? DeepCapsuleSlotState.CollapsedDocked : DeepCapsuleSlotState.None);
        }
        ClearExpandedDeepCapsuleSlotPlacement(animate);
    }

    public void UpdateDeepCapsuleMode()
    {
        if (!_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            ClearDeepCapsulePlacement();
        }
        else if (!_paper.IsCollapsed)
        {
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false);
        }
        else
        {
            MoveDeepCapsuleToCurrentTarget();
        }

        RefreshEffectiveTopmost();
    }

    public void UpdateDeepCapsuleExpandedSlotMode()
    {
        if (_paper.IsCollapsed)
        {
            return;
        }

        if (!_paper.IsVisible || !_controller.State.UseCapsuleMode || !_controller.State.UseDeepCapsuleMode)
        {
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            return;
        }

        if (_controller.State.ShowDeepCapsuleWhileExpanded && _controller.CanPaperDisplayAsCapsule(_paper))
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.ExpandedReserved);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Active);
            _isCollapseAllRetracted = false;
            RefreshCapsuleLabel();
            UpdateDeepCapsuleSlotHostTheme();
            UpdateDeepCapsuleSlotClosePlacement();
            return;
        }

        if (!_controller.State.ShowDeepCapsuleWhileExpanded && HoldsDeepCapsuleSlotWhileExpanded)
        {
            SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
            ClearDeepCapsulePlacement(restoreCollapsedPosition: false, animate: _controller.State.EnableAnimations);
        }
    }

    private void StartDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() || _deepCapsuleSlotHost == null)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Reordering);
        SetDeepCapsuleVisualState(DeepCapsuleVisualState.Hovered);
        // currentScreenPos is in physical device pixels (PointToScreen); Top is in DIPs.
        // Convert to DIPs so the capsule tracks the cursor 1:1 at any DPI.
        var dpiScaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        _deepCapsuleDragMouseOffsetY = currentScreenPos.Y / dpiScaleY - _deepCapsuleSlotHost.Top;

        _deepCapsuleSlotHost.BeginAnimation(Window.LeftProperty, null);
        _deepCapsuleSlotHost.BeginAnimation(Window.TopProperty, null);

        var area = DeepCapsuleWorkArea();
        var visibleWidth = ExpandedDeepCapsuleVisibleWidth();
        _deepCapsuleDragLeft = RoundToDevicePixelX(area.Right - visibleWidth);

        _deepCapsuleSlotHost.Left = _deepCapsuleDragLeft;
        ApplyDeepCapsuleSlotHostViewport(visibleWidth);
        _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;

        Mouse.OverrideCursor = Cursors.SizeNS;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(Point currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        var area = DeepCapsuleWorkArea();
        var minTop = area.Top + DeepCapsuleTopMargin;
        var maxTop = Math.Max(minTop, area.Bottom - PaperLayoutDefaults.CapsuleHeight - DeepCapsuleTopMargin);
        // currentScreenPos is in physical device pixels; convert to DIPs to match Top/offset.
        var dpiScaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var targetTop = Math.Clamp(currentScreenPos.Y / dpiScaleY - _deepCapsuleDragMouseOffsetY, minTop, maxTop);

        if (_deepCapsuleSlotHost != null)
        {
            _deepCapsuleSlotHost.Left = _deepCapsuleDragLeft;
            _deepCapsuleSlotHost.Top = RoundToDevicePixelY(targetTop);
            _deepCapsuleSlotLeft = _deepCapsuleSlotHost.Left;
            _deepCapsuleSlotTop = _deepCapsuleSlotHost.Top;
        }
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
        Mouse.OverrideCursor = null;
        SetDeepCapsuleVisualState(
            _deepCapsuleSlotShell?.IsMouseOver == true
                ? DeepCapsuleVisualState.Hovered
                : DeepCapsuleVisualState.Resting);

        if (commit)
        {
            _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
            return;
        }

        MoveDeepCapsuleToCurrentTarget();
    }

    private bool CanReorderDeepCapsuleSlot()
    {
        return HasDeepCapsuleSlotPlacement &&
            _deepCapsuleSlotHost?.IsVisible == true &&
            (_paper.IsCollapsed || (_controller.State.ShowDeepCapsuleWhileExpanded && IsDeepCapsuleSlotActive));
    }

    private int DeepCapsuleDropIndexForCurrentPosition()
    {
        var count = _controller.VisibleDeepCapsuleCount();
        if (count <= 1)
        {
            return 0;
        }

        var centerY = (_deepCapsuleSlotHost?.Top ?? _deepCapsuleSlotTop) + (PaperLayoutDefaults.CapsuleHeight / 2);
        var area = DeepCapsuleWorkArea();
        // Real capsules start at slot _deepCapsuleVisualOffset when the master capsule occupies slot 0.
        var firstCenterY = DeepCapsuleTopForIndex(_deepCapsuleVisualOffset) + (PaperLayoutDefaults.CapsuleHeight / 2);
        var slotHeight = PaperLayoutDefaults.CapsuleHeight + DeepCapsuleGap;
        var originalIndex = Math.Clamp(_deepCapsuleIndex, 0, count - 1);
        var rawIndex = (centerY - firstCenterY) / slotHeight;
        var index = rawIndex >= originalIndex
            ? (int)Math.Floor(rawIndex)
            : (int)Math.Ceiling(rawIndex);
        return Math.Clamp(index, 0, count - 1);
    }

    private void AlignExpandedToRightEdge(double targetWidth, double targetHeight, double requiredRightInset = 0)
    {
        var area = DeepCapsuleWorkArea();
        var width = Math.Max(targetWidth, PaperLayoutDefaults.MinWidth);
        var height = Math.Max(targetHeight, PaperLayoutDefaults.MinHeight);
        var rightInset = Math.Min(
            Math.Max(
                Math.Max(DeepCapsuleExpandedRightInset, requiredRightInset),
                _controller.VisibleDeepCapsuleRestingWidth() + DeepCapsuleGap),
            Math.Max(0, area.Width - width));
        var targetTop = Math.Clamp(Top, area.Top + DeepCapsuleTopMargin, Math.Max(area.Top + DeepCapsuleTopMargin, area.Bottom - height - DeepCapsuleTopMargin));

        Left = RoundToDevicePixelX(area.Right - width - rightInset);
        Top = RoundToDevicePixelY(targetTop);
    }

    private void RegisterNameSafe(string name, object scopedElement)
    {
        try
        {
            UnregisterName(name);
        }
        catch
        {
            // Name may not exist yet.
        }

        try
        {
            RegisterName(name, scopedElement);
        }
        catch
        {
            // Duplicate names are non-fatal for this small UI.
        }
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject target)
    {
        while (current != null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
            current = GetSafeParent(current);
        }
        return false;
    }

    private static bool IsScrollBarInteractionSource(DependencyObject? current, DependencyObject scope)
    {
        while (current != null)
        {
            if (current is ScrollBar or Thumb or Track or RepeatButton)
            {
                return true;
            }

            if (ReferenceEquals(current, scope))
            {
                return false;
            }

            current = GetSafeParent(current);
        }

        return false;
    }

    private bool CanDisplayAsCapsule()
    {
        return _controller.CanPaperDisplayAsCapsule(_paper);
    }

    private void RefreshCloseButton()
    {
        if (_closeButton == null)
        {
            return;
        }

        if (CanDisplayAsCapsule())
        {
            _closeButton.Content = "─";
            _closeButton.ToolTip = Strings.Get("ToolTipCollapseToCapsule");
            _closeButton.Cursor = Cursors.Hand;
        }
        else
        {
            _closeButton.Content = "×";
            _closeButton.ToolTip = Strings.Get("ToolTipHideThisPaper");
            _closeButton.Cursor = Cursors.Hand;
        }
    }

    public void UpdateCapsuleMode()
    {
        RefreshCloseButton();
        if (!CanDisplayAsCapsule() && _paper.IsCollapsed)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                ClearDeepCapsulePlacement();
            }
            SetCollapsedState(false, animate: true);
        }
        else
        {
            RefreshEffectiveTopmost();
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        UpdateTextZoom();
    }

    public void RefreshCapsuleEligibility()
    {
        RefreshCloseButton();
        if (!CanDisplayAsCapsule() && _paper.IsCollapsed)
        {
            if (HasDeepCapsuleSlotPlacement)
            {
                ClearDeepCapsulePlacement();
            }

            SetCollapsedState(false, animate: true);
            return;
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
        if (_capsuleLeftArea != null)
        {
            _capsuleLeftArea.ContextMenu = BuildPaperContextMenu();
        }
    }

    private void RefreshCapsuleLabel()
    {
        if (_capsuleLabelText == null)
        {
            return;
        }

        _capsuleLabelText.Text = _controller.PaperCapsuleTitle(_paper);
        _capsuleLabelText.ToolTip = _controller.PaperTitleText(_paper);
        if (_capsuleIconText != null)
        {
            _capsuleIconText.Foreground = BrightWeakTextBrush;
        }
        if (_capsuleShell != null)
        {
            _capsuleShell.Width = CapsuleShellLayoutWidth();
        }
        UpdateCapsuleClosePlacement();
        RefreshDeepCapsuleSlotLabel();
        UpdateDeepCapsuleSlotClosePlacement();
    }

    private void RefreshDeepCapsuleSlotLabel()
    {
        if (_deepCapsuleSlotLabelText == null)
        {
            return;
        }

        _deepCapsuleSlotLabelText.Text = _controller.PaperCapsuleTitle(_paper);
        _deepCapsuleSlotLabelText.ToolTip = _controller.PaperTitleText(_paper);
        if (_deepCapsuleSlotShell != null && !IsDeepCapsuleSlotHorizontalAnimating)
        {
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }
    }

    private void UpdateCapsuleClosePlacement()
    {
        var usesDeepCapsulePresentation = UsesDeepCapsulePresentation;
        if (_capsuleCloseArea != null)
        {
            _capsuleCloseArea.Width = CapsuleCloseWidthForCurrentPlacement();
            _capsuleCloseArea.Margin = usesDeepCapsulePresentation
                ? new Thickness(0, 0, 2, 0)
                : new Thickness(0);
        }

        if (_capsuleCloseGlyphOffset != null)
        {
            _capsuleCloseGlyphOffset.X = usesDeepCapsulePresentation
                ? CapsuleCloseGlyphDeepOffset
                : CapsuleCloseGlyphNormalOffset;
        }

        if (_capsuleShell != null)
        {
            _capsuleShell.Width = CapsuleShellLayoutWidth();
        }

        UpdateDeepCapsuleSlotClosePlacement();
    }

    private void UpdateDeepCapsuleSlotClosePlacement(bool updateHostViewport = true)
    {
        var usesActivePresentation = _deepCapsuleVisualState is DeepCapsuleVisualState.Active or DeepCapsuleVisualState.Hovered;
        if (_deepCapsuleSlotCloseArea != null)
        {
            _deepCapsuleSlotCloseArea.Width = CapsuleCloseWidth;
            _deepCapsuleSlotCloseArea.Margin = usesActivePresentation
                ? new Thickness(0, 0, 2, 0)
                : new Thickness(0);
        }

        if (_deepCapsuleSlotCloseGlyphOffset != null)
        {
            _deepCapsuleSlotCloseGlyphOffset.X = CapsuleCloseGlyphDeepOffset;
        }

        if (_deepCapsuleSlotShell != null && !IsDeepCapsuleSlotHorizontalAnimating)
        {
            _deepCapsuleSlotShell.Width = DeepCapsuleSlotShellLayoutWidth();
        }

        if (updateHostViewport && _deepCapsuleSlotHost != null && HasDeepCapsuleSlotPlacement)
        {
            ApplyDeepCapsuleSlotHostViewport(_deepCapsuleSlotHost.Width);
        }
    }

    private Point CapsulePointerScreenPosition(MouseEventArgs e)
    {
        if (_capsuleShell != null && PresentationSource.FromVisual(_capsuleShell) != null)
        {
            return _capsuleShell.PointToScreen(e.GetPosition(_capsuleShell));
        }

        return PointToScreen(e.GetPosition(this));
    }

    private void BuildCapsuleShell()
    {
        _capsuleShell = new Grid
        {
            Width = CapsuleShellLayoutWidth(),
            Height = 30,
            Background = Brushes.Transparent
        };
        _capsuleShell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _capsuleShell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftArea = new Border
        {
            Background = Brushes.Transparent,
            // Concentric with the capsule pill's left end.
            CornerRadius = new CornerRadius(CapsuleInnerCornerRadius, 0, 0, CapsuleInnerCornerRadius),
            Cursor = Cursors.Hand
        };

        var leftStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // Hug the left edge (with a small inset) instead of centering, so the icon
            // doesn't float in the middle of the left area with dead space beside it.
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(CapsuleLeftPadding, 0, 0, 0)
        };

        var iconText = new TextBlock
        {
            Text = _paper.Type == PaperTypes.Note ? "✎" : "✓",
            Foreground = BrightWeakTextBrush,
            // Explicit font so the rendered glyph matches what MeasureCapsuleTextWidth measures.
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleIconFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleIconText = iconText;
        leftStack.Children.Add(iconText);

        _capsuleLabelText = new TextBlock
        {
            Foreground = WeakTextBrush,
            // Explicit font so the rendered title matches the measured width (the window
            // default is Segoe UI, which has different digit/halfwidth metrics).
            FontFamily = NoteTypography.FontFamily,
            FontSize = CapsuleLabelFontSize,
            Margin = new Thickness(CapsuleIconGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RefreshCapsuleLabel();
        leftStack.Children.Add(_capsuleLabelText);

        leftArea.Child = leftStack;

        leftArea.MouseEnter += (_, _) => leftArea.Background = HoverBrush;
        leftArea.MouseLeave += (_, _) => leftArea.Background = Brushes.Transparent;

        leftArea.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _mouseDownScreenPos = CapsulePointerScreenPosition(e);
            _isMaybeDragging = true;
            leftArea.CaptureMouse();
            e.Handled = true;
        };

        leftArea.PreviewMouseMove += (s, e) =>
        {
            if (!_isMaybeDragging) return;

            Point currentScreenPos = CapsulePointerScreenPosition(e);
            double deltaX = Math.Abs(currentScreenPos.X - _mouseDownScreenPos.X);
            double deltaY = Math.Abs(currentScreenPos.Y - _mouseDownScreenPos.Y);

            if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
                deltaY >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isMaybeDragging = false;

                leftArea.ReleaseMouseCapture();
                leftArea.Background = Brushes.Transparent;
                leftArea.Cursor = Cursors.SizeAll;

                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore if mouse state changed unexpectedly
                }
                finally
                {
                    leftArea.Cursor = Cursors.Hand;
                }

                e.Handled = true;
            }
        };

        leftArea.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_isMaybeDragging)
            {
                _isMaybeDragging = false;
                leftArea.ReleaseMouseCapture();

                SetCollapsedState(false);
                e.Handled = true;
            }
        };

        leftArea.LostMouseCapture += (s, e) =>
        {
            _isMaybeDragging = false;
        };

        leftArea.ContextMenu = BuildPaperContextMenu();
        _capsuleLeftArea = leftArea;

        Grid.SetColumn(leftArea, 0);
        _capsuleShell.Children.Add(leftArea);

        var closeGlyphOffset = new TranslateTransform(0, 0);
        var closeGlyph = new TextBlock
        {
            Text = "×",
            Foreground = WeakTextBrush,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = closeGlyphOffset
        };
        _capsuleCloseGlyph = closeGlyph;
        _capsuleCloseGlyphOffset = closeGlyphOffset;

        var capsuleClose = new Border
        {
            Width = CapsuleCloseWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            // Concentric with the capsule pill's right edge.
            CornerRadius = new CornerRadius(0, CapsuleInnerCornerRadius, CapsuleInnerCornerRadius, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Get("ToolTipHideThisPaper"),
            Child = closeGlyph
        };
        _capsuleCloseArea = capsuleClose;
        UpdateCapsuleClosePlacement();
        capsuleClose.MouseEnter += (_, _) =>
        {
            leftArea.Background = Brushes.Transparent;
            capsuleClose.Background = HoverBrush;
            closeGlyph.Foreground = TextBrush;
        };
        capsuleClose.MouseLeave += (_, _) =>
        {
            capsuleClose.Background = Brushes.Transparent;
            closeGlyph.Foreground = WeakTextBrush;
            capsuleClose.Opacity = 1.0;
        };
        capsuleClose.MouseLeftButtonDown += (_, e) =>
        {
            capsuleClose.Opacity = 0.72;
            e.Handled = true;
        };
        capsuleClose.MouseLeftButtonUp += (_, e) =>
        {
            capsuleClose.Opacity = 1.0;
            _controller.HidePaper(_paper);
            e.Handled = true;
        };

        Grid.SetColumn(capsuleClose, 1);
        _capsuleShell.Children.Add(capsuleClose);
    }

    public void SetCollapsedState(bool collapsed, bool animate = true, bool saveGeometry = true, bool alignExpandedToRight = false)
    {
        animate = animate && _controller.State.EnableAnimations;

        if (collapsed && !CanDisplayAsCapsule())
        {
            if (_paper.IsCollapsed)
            {
                SetCollapsedState(false, animate, saveGeometry, alignExpandedToRight);
            }
            else
            {
                RefreshCloseButton();
                _paperChrome.ContextMenu = BuildPaperContextMenu();
            }
            return;
        }

        if (_paper.IsCollapsed == collapsed)
        {
            return;
        }

        if (_isApplyingCollapsedState)
        {
            // Capture current animated values to prevent snapping
            double currentWidth = Width;
            double currentHeight = Height;
            double currentShellOpacity = _shell.Opacity;
            double currentCapsuleOpacity = _capsuleShell.Opacity;

            // Set them as local values
            Width = currentWidth;
            Height = currentHeight;
            _shell.Opacity = currentShellOpacity;
            _capsuleShell.Opacity = currentCapsuleOpacity;

            // Clear ongoing animations safely
            BeginAnimation(TransitionProgressProperty, null);
            _shell.BeginAnimation(UIElement.OpacityProperty, null);
            _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
            ResetTransitionVisuals();

            _shell.Width = double.NaN;
            _shell.Height = double.NaN;
            _isApplyingCollapsedState = false;
        }

        _isApplyingCollapsedState = true;
        var transitionGeneration = ++_collapseTransitionGeneration;

        var capsuleWidth = CapsuleWindowWidth();
        double targetWidth = collapsed ? capsuleWidth : _paper.Width;
        double targetHeight = collapsed ? PaperLayoutDefaults.CapsuleHeight : _paper.Height;
        double finalTargetWidth = RoundToDevicePixelX(targetWidth);
        double finalTargetHeight = RoundToDevicePixelY(targetHeight);
        var usesDeepCapsuleMode = _paper.IsVisible && _controller.State.UseCapsuleMode && _controller.State.UseDeepCapsuleMode;
        var arrangeDeepCapsulesAfterCollapse = collapsed && usesDeepCapsuleMode;

        var wasDeepCapsulePlaced = HasDeepCapsuleSlotPlacement;
        var expandingFromDeepCapsuleEdge = !collapsed && usesDeepCapsuleMode && wasDeepCapsulePlaced;
        var arrangeDeepCapsulesAfterExpand = expandingFromDeepCapsuleEdge;
        var keepDeepCapsuleSlotReservation = !collapsed
            && expandingFromDeepCapsuleEdge
            && usesDeepCapsuleMode
            && _controller.State.ShowDeepCapsuleWhileExpanded;
        var returningToHiddenDeepCapsuleSlot = collapsed
            && usesDeepCapsuleMode
            && ExpandedFromDeepCapsuleEdge
            && !_controller.State.ShowDeepCapsuleWhileExpanded;

        _paper.IsCollapsed = collapsed;
        if (!collapsed)
        {
            if (_controller.State.ShowDeepCapsuleWhileExpanded &&
                _controller.CanPaperDisplayAsCapsule(_paper) &&
                (expandingFromDeepCapsuleEdge || _controller.State.UseDeepCapsuleMode))
            {
                SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.EdgeSlot);
            }
            SetDeepCapsuleSlotState(keepDeepCapsuleSlotReservation ? DeepCapsuleSlotState.ExpandedReserved : DeepCapsuleSlotState.None);
            SetDeepCapsuleVisualState(keepDeepCapsuleSlotReservation ? DeepCapsuleVisualState.Active : DeepCapsuleVisualState.Resting);
            if (alignExpandedToRight || expandingFromDeepCapsuleEdge)
            {
                var requiredRightInset = keepDeepCapsuleSlotReservation
                    ? ExpandedDeepCapsuleVisibleWidth() + DeepCapsuleGap
                    : 0;
                MoveWindowWithoutGeometrySave(() => AlignExpandedToRightEdge(finalTargetWidth, finalTargetHeight, requiredRightInset));
            }
            if (arrangeDeepCapsulesAfterExpand)
            {
                _controller.ArrangeDeepCapsules(animate: true);
            }
        }
        else
        {
            if (_deepCapsuleSlotState == DeepCapsuleSlotState.ExpandedReserved)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.None);
            }
            if (usesDeepCapsuleMode && !wasDeepCapsulePlaced && !returningToHiddenDeepCapsuleSlot)
            {
                SetDeepCapsuleSlotState(DeepCapsuleSlotState.CollapsedDocked);
                SetDeepCapsuleVisualState(DeepCapsuleVisualState.Resting);
                _controller.ArrangeDeepCapsules(animate: false);
            }
        }

        RefreshEffectiveTopmost();
        _controller.MarkDirty();

        if (collapsed)
        {
            RefreshCapsuleLabel();
            _capsuleShell.Visibility = Visibility.Visible;

            if (_paperChrome.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            {
                shadow.BlurRadius = 8;
                shadow.Opacity = 0.08;
            }
        }
        else
        {
            _shell.Visibility = Visibility.Visible;

            if (_paper.Type == PaperTypes.Todo)
            {
                RebuildTodoRows();
            }

            if (_paperChrome.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            {
                shadow.BlurRadius = 14;
                shadow.Opacity = 0.18;
            }
        }

        if (animate)
        {
            var expandedWidth = collapsed ? RoundToDevicePixelX(Width) : finalTargetWidth;
            var expandedHeight = collapsed ? RoundToDevicePixelY(Height) : finalTargetHeight;
            _transitionBaseWidth = expandedWidth;
            _transitionBaseHeight = expandedHeight;
            _startTransitionWidth = collapsed ? expandedWidth : capsuleWidth;
            _startTransitionHeight = collapsed ? expandedHeight : PaperLayoutDefaults.CapsuleHeight;
            _targetTransitionWidth = collapsed ? finalTargetWidth : expandedWidth;
            _targetTransitionHeight = collapsed ? finalTargetHeight : expandedHeight;
            _isTransitionVisualsActive = true;

            // Prevent shell content reflow/wrapping by locking its size to the expanded dimensions
            _shell.Width = Math.Max(0, expandedWidth - WindowChromeInset);
            _shell.Height = Math.Max(0, expandedHeight - WindowChromeInset);

            TransitionProgress = 0.0;
            UpdateTransitionVisuals(0.0);
            if (!collapsed)
            {
                Width = expandedWidth;
                Height = expandedHeight;
            }

            var easeOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

            var progressAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(collapsed ? CollapseResizeMilliseconds : ExpandAnimationMilliseconds),
                BeginTime = collapsed ? TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds) : TimeSpan.Zero,
                EasingFunction = easeOut
            };

            if (collapsed)
            {
                _shell.Opacity = 1.0;
                _capsuleShell.Opacity = 0.0;

                var fadeOutShell = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds),
                    EasingFunction = easeOut
                };
                _shell.BeginAnimation(UIElement.OpacityProperty, fadeOutShell);

                var fadeInCapsule = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(CollapseResizeMilliseconds),
                    BeginTime = TimeSpan.FromMilliseconds(CollapseShellFadeMilliseconds),
                    EasingFunction = easeOut
                };
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeInCapsule);
            }
            else
            {
                _shell.Opacity = 0.0;

                _capsuleShell.Opacity = 1.0;

                var fadeOutCapsule = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(80),
                    EasingFunction = easeOut
                };
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeOutCapsule);

                var fadeInShell = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(140),
                    BeginTime = TimeSpan.FromMilliseconds(80),
                    EasingFunction = easeOut
                };
                _shell.BeginAnimation(UIElement.OpacityProperty, fadeInShell);
            }

            progressAnim.Completed += (s, e) =>
            {
                if (transitionGeneration != _collapseTransitionGeneration)
                {
                    return;
                }

                // 1. Set local values before clearing animations to prevent snapping/flicker
                TransitionProgress = 1.0;
                UpdateTransitionVisuals(1.0);

                if (collapsed)
                {
                    _shell.Opacity = 0.0;
                    _capsuleShell.Opacity = 1.0;
                    _shell.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _capsuleShell.Opacity = 0.0;
                    _capsuleShell.Visibility = Visibility.Collapsed;
                    _shell.Opacity = 1.0;
                }

                if (collapsed)
                {
                    MinWidth = capsuleWidth;
                    MinHeight = PaperLayoutDefaults.CapsuleHeight;
                    ResizeMode = ResizeMode.NoResize;
                }
                else
                {
                    MinWidth = PaperLayoutDefaults.MinWidth;
                    MinHeight = PaperLayoutDefaults.MinHeight;
                    ResizeMode = ResizeMode.CanResizeWithGrip;
                }

                Width = finalTargetWidth;
                Height = finalTargetHeight;
                // Re-measure at the final window size before removing the visual scale.
                UpdateLayout();

                // 2. Clear animations
                BeginAnimation(TransitionProgressProperty, null);
                _shell.BeginAnimation(UIElement.OpacityProperty, null);
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
                ResetTransitionVisuals();

                // 3. Unlock shell layout
                _shell.Width = double.NaN;
                _shell.Height = double.NaN;

                _isApplyingCollapsedState = false;
                if (saveGeometry)
                {
                    _controller.UpdateGeometry(_paper, this);
                }
                if (arrangeDeepCapsulesAfterCollapse)
                {
                    _controller.ArrangeDeepCapsules(animate: true);
                    SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.Normal);
                    HideMainWindowForDeepCapsuleRest();
                }
            };

            BeginAnimation(TransitionProgressProperty, progressAnim);
        }
        else
        {
            BeginAnimation(TransitionProgressProperty, null);
            _shell.BeginAnimation(UIElement.OpacityProperty, null);
            _capsuleShell.BeginAnimation(UIElement.OpacityProperty, null);
            ResetTransitionVisuals();

            TransitionProgress = 0.0;

            if (collapsed)
            {
                _shell.Visibility = Visibility.Collapsed;
                _shell.Opacity = 0;
                _capsuleShell.Visibility = Visibility.Visible;
                _capsuleShell.Opacity = 1;

                MinWidth = capsuleWidth;
                MinHeight = PaperLayoutDefaults.CapsuleHeight;
                ResizeMode = ResizeMode.NoResize;
            }
            else
            {
                _shell.Visibility = Visibility.Visible;
                _shell.Opacity = 1;
                _capsuleShell.Visibility = Visibility.Collapsed;
                _capsuleShell.Opacity = 0;

                MinWidth = PaperLayoutDefaults.MinWidth;
                MinHeight = PaperLayoutDefaults.MinHeight;
                ResizeMode = ResizeMode.CanResizeWithGrip;
            }

            _shell.Width = double.NaN;
            _shell.Height = double.NaN;

            Width = finalTargetWidth;
            Height = finalTargetHeight;

            _isApplyingCollapsedState = false;
            if (saveGeometry)
            {
                _controller.UpdateGeometry(_paper, this);
            }
            if (arrangeDeepCapsulesAfterCollapse)
            {
                _controller.ArrangeDeepCapsules(animate: true);
                SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin.Normal);
                HideMainWindowForDeepCapsuleRest();
            }
        }

        _paperChrome.ContextMenu = BuildPaperContextMenu();
    }

    private static void ApplyNoActivateStyle(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExNoActivate);
    }

    private static void ApplyTopmostZOrder(Window window, bool topmost, IntPtr insertAfter)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
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

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookStruct
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
