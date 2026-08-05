using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private sealed class TodoSweepSelectionState
    {
        public TodoSweepSelectionState(
            string anchorItemId,
            Border anchorRow,
            TodoTextBox? sourceTextBox)
        {
            AnchorItemId = anchorItemId;
            AnchorRow = anchorRow;
            SourceTextBox = sourceTextBox;
        }

        public string AnchorItemId { get; }
        public Border AnchorRow { get; }
        public TodoTextBox? SourceTextBox { get; }
        public bool IsPromoted { get; set; }
    }

    private readonly HashSet<string> _selectedTodoItemIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _todoGroupDragItemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _todoGroupDragRestingOpacities = new(StringComparer.Ordinal);
    private TodoSweepSelectionState? _todoSweepSelection;
    private bool _todoSelectionInputHooksInstalled;

    private static Brush TodoSelectionBrush =>
        Theme.Tint((byte)(Theme.IsDark ? 62 : 42));

    private bool IsTodoGroupDrag => _todoGroupDragItemIds.Count > 1;

    private void EnsureTodoSelectionInputHooks()
    {
        if (_todoSelectionInputHooksInstalled)
        {
            return;
        }

        _todoSelectionInputHooksInstalled = true;
        PreviewMouseLeftButtonDown += OnTodoSelectionWindowPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnTodoSweepPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnTodoSweepPreviewMouseLeftButtonUp;
        LostMouseCapture += OnTodoSweepLostMouseCapture;
    }

    private void OnTodoSelectionWindowPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_paper.Type != PaperTypes.Todo ||
            e.ChangedButton != MouseButton.Left ||
            _todoSweepSelection != null)
        {
            return;
        }

        if (FindTodoRowAncestor(e.OriginalSource as DependencyObject) == null)
        {
            ClearTodoSelection();
        }
    }

    private void ConfigureTodoMultiSelection(
        Border row,
        PaperItem item,
        CheckBox check,
        TodoTextBox text)
    {
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || _todoDrag != null)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (IsDescendantOf(source, check) &&
                _selectedTodoItemIds.Count > 1 &&
                _selectedTodoItemIds.Contains(item.Id))
            {
                var selected = SelectedTodoItems();
                ApplyDoneToSelectedTodos(!selected.All(candidate => candidate.Done));
                e.Handled = true;
                return;
            }

            if (IsDescendantOf(source, check) ||
                IsDescendantOfCursor(source, Cursors.SizeAll) ||
                IsDescendantOfCursor(source, Cursors.Hand))
            {
                return;
            }

            // Do not consume the press. TodoTextBox keeps normal character selection until the
            // held pointer actually enters a different todo row; only then do we promote the
            // gesture to whole-item sweep selection.
            ArmTodoSweepSelection(
                item.Id,
                row,
                IsDescendantOf(source, text) ? text : null);

            if (!_selectedTodoItemIds.Contains(item.Id))
            {
                ClearTodoSelection();
            }
        };

        UpdateTodoRowBackground(row);
    }

    private static bool IsDescendantOfType<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool IsDescendantOfCursor(DependencyObject? source, Cursor cursor)
    {
        var current = source;
        while (current != null)
        {
            if (current is FrameworkElement element &&
                ReferenceEquals(element.Cursor, cursor))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private Border? FindTodoRowAncestor(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Border row && _todoRows.Contains(row))
            {
                return row;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ArmTodoSweepSelection(
        string itemId,
        Border row,
        TodoTextBox? sourceTextBox)
    {
        _todoSweepSelection = new TodoSweepSelectionState(
            itemId,
            row,
            sourceTextBox);
    }

    private void PromoteTodoSweepSelection(
        TodoSweepSelectionState state,
        string targetItemId)
    {
        state.IsPromoted = true;
        if (state.SourceTextBox != null)
        {
            state.SourceTextBox.Select(state.SourceTextBox.CaretIndex, 0);
        }
        Keyboard.ClearFocus();
        _selectedTodoItemIds.Clear();
        _selectedTodoItemIds.Add(state.AnchorItemId);
        SelectTodoRange(state.AnchorItemId, targetItemId);
        CaptureMouse();
    }

    private void OnTodoSweepPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var state = _todoSweepSelection;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTodoSweepSelection();
            return;
        }

        var point = e.GetPosition(this);
        var targetRow = FindTodoRowAtPoint(point);
        if (!state.IsPromoted)
        {
            if (targetRow == null ||
                ReferenceEquals(targetRow, state.AnchorRow) ||
                targetRow.Tag is not string firstTargetItemId)
            {
                // The original TextBox still owns this gesture and may select characters.
                return;
            }

            PromoteTodoSweepSelection(state, firstTargetItemId);
        }

        AutoScrollTodoSelection(point);
        targetRow = FindTodoRowAtPoint(point);
        if (targetRow?.Tag is string targetItemId)
        {
            SelectTodoRange(state.AnchorItemId, targetItemId);
        }

        e.Handled = true;
    }

    private void OnTodoSweepPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_todoSweepSelection == null)
        {
            return;
        }

        var promoted = _todoSweepSelection.IsPromoted;
        var clearExistingSelection =
            !promoted && _selectedTodoItemIds.Count > 0;
        EndTodoSweepSelection();
        if (clearExistingSelection)
        {
            ClearTodoSelection();
        }
        e.Handled = promoted;
    }

    private void EndTodoSweepSelection()
        => CancelTodoSweepSelection(clearSelection: false);

    private void CancelTodoSweepSelection(bool clearSelection)
    {
        var hadSweep = _todoSweepSelection != null;
        _todoSweepSelection = null;
        if (clearSelection)
        {
            _selectedTodoItemIds.Clear();
        }
        if (hadSweep && IsMouseCaptured && _todoDrag == null)
        {
            ReleaseMouseCapture();
        }
        ApplyTodoSelectionVisuals();
    }

    private void OnTodoSweepLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_todoSweepSelection?.IsPromoted != true)
        {
            // A TextBox may capture/release the mouse while the gesture is only armed.
            return;
        }

        _todoSweepSelection = null;
        ApplyTodoSelectionVisuals();
    }

    private void AutoScrollTodoSelection(Point pointOnWindow)
    {
        var scrollViewer = FindVisualAncestor<ScrollViewer>(_todoPanel);
        if (scrollViewer == null || scrollViewer.ActualHeight <= 0)
        {
            return;
        }

        var point = TranslatePoint(pointOnWindow, scrollViewer);
        var edge = Math.Min(AppTypography.Scale(28), scrollViewer.ActualHeight / 4);
        if (point.Y < edge)
        {
            scrollViewer.LineUp();
        }
        else if (point.Y > scrollViewer.ActualHeight - edge)
        {
            scrollViewer.LineDown();
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Border? FindTodoRowAtPoint(Point pointOnWindow)
    {
        foreach (var row in _todoRows)
        {
            if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0)
            {
                continue;
            }

            var origin = row.TranslatePoint(new Point(0, 0), this);
            if (pointOnWindow.X >= origin.X &&
                pointOnWindow.X <= origin.X + row.ActualWidth &&
                pointOnWindow.Y >= origin.Y &&
                pointOnWindow.Y <= origin.Y + row.ActualHeight)
            {
                return row;
            }
        }
        return null;
    }

    private void SelectTodoRange(string anchorItemId, string targetItemId)
    {
        var orderedIds = OrderedItems().Select(item => item.Id).ToList();
        var anchorIndex = orderedIds.IndexOf(anchorItemId);
        var targetIndex = orderedIds.IndexOf(targetItemId);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        _selectedTodoItemIds.Clear();
        for (var index = start; index <= end; index++)
        {
            _selectedTodoItemIds.Add(orderedIds[index]);
        }
        ApplyTodoSelectionVisuals();
    }

    private void PruneTodoSelection()
    {
        var validIds = _paper.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _selectedTodoItemIds.RemoveWhere(itemId => !validIds.Contains(itemId));
        _todoGroupDragItemIds.RemoveWhere(itemId => !validIds.Contains(itemId));
    }

    private void ClearTodoSelection()
    {
        if (_selectedTodoItemIds.Count == 0)
        {
            return;
        }

        _selectedTodoItemIds.Clear();
        ApplyTodoSelectionVisuals();
    }

    private void ApplyTodoSelectionVisuals()
    {
        foreach (var row in _todoRows)
        {
            if (!ReferenceEquals(row, _activeDropRow) &&
                !ReferenceEquals(row, _linkedNoteDropRow))
            {
                UpdateTodoRowBackground(row);
            }
        }
    }

    private void UpdateTodoRowBackground(Border row)
    {
        var selected = row.Tag is string itemId &&
            _selectedTodoItemIds.Contains(itemId);
        row.Background = selected
            ? TodoSelectionBrush
            : row.IsMouseOver ? HoverBrush : Brushes.Transparent;
    }

    private List<PaperItem> SelectedTodoItems()
    {
        return OrderedItems()
            .Where(item => _selectedTodoItemIds.Contains(item.Id))
            .ToList();
    }

    private bool TryCopySelectedTodoItems()
    {
        if (_selectedTodoItemIds.Count == 0)
        {
            return false;
        }

        if (FocusManager.GetFocusedElement(this) is TodoTextBox box &&
            box.SelectionLength > 0)
        {
            return false;
        }

        var text = string.Join(
            Environment.NewLine,
            SelectedTodoItems().Select(item => item.Text));
        return ClipboardHelper.TrySetText(text);
    }

    private bool TryClearTodoSelectionFromEscape()
    {
        if (_todoSweepSelection == null && _selectedTodoItemIds.Count == 0)
        {
            return false;
        }

        CancelTodoSweepSelection(clearSelection: true);
        return true;
    }

    private void ApplyDoneToSelectedTodos(bool done)
    {
        var selected = SelectedTodoItems();
        if (selected.Count == 0 || selected.All(item => item.Done == done))
        {
            return;
        }

        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();

        foreach (var item in selected)
        {
            item.Done = done;
            if (done)
            {
                item.ReminderAt = null;
            }
        }

        if (done && _controller.State.AutoClearCompletedTodos)
        {
            var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _paper.Items.RemoveAll(item => selectedIds.Contains(item.Id));
            if (_paper.Items.Count == 0)
            {
                _paper.Items.Add(new PaperItem());
            }
            _selectedTodoItemIds.Clear();
        }

        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        RebuildTodoRows();
        RefreshCapsuleEligibilityForLinkedNoteChanges(previousItems);
    }

    private void DeleteSelectedTodoItems()
    {
        var selected = SelectedTodoItems();
        if (selected.Count == 0)
        {
            return;
        }

        var previousItems = CloneItems(_paper.Items);
        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        PushUndoSnapshot();
        _paper.Items.RemoveAll(item => selectedIds.Contains(item.Id));
        if (_paper.Items.Count == 0)
        {
            _paper.Items.Add(new PaperItem());
        }

        _selectedTodoItemIds.Clear();
        NormalizeTodoItems();
        NormalizeOrders();
        _controller.MarkDirty();
        _controller.NotifyTodoReminderCollectionChanged();
        RebuildTodoRows();
        RefreshCapsuleEligibilityForLinkedNoteChanges(previousItems);
    }

    private bool TryCreateTodoSelectionContextMenu(
        PaperItem item,
        Border row,
        out ContextMenu menu)
    {
        menu = null!;
        if (_selectedTodoItemIds.Count <= 1 ||
            !_selectedTodoItemIds.Contains(item.Id))
        {
            return false;
        }

        var selected = SelectedTodoItems();
        menu = CreateContextMenu();
        menu.Items.Add(MenuHeader(Strings.Format(
            "MenuSelectedTodoCount",
            selected.Count)));
        menu.Items.Add(MenuItem(
            Strings.Get("MenuCopySelectedTodos"),
            (_, _) => TryCopySelectedTodoItems()));
        menu.Items.Add(MenuItem(
            selected.All(candidate => candidate.Done)
                ? Strings.Get("MenuUncompleteSelectedTodos")
                : Strings.Get("MenuCompleteSelectedTodos"),
            (_, _) => ApplyDoneToSelectedTodos(
                !selected.All(candidate => candidate.Done))));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuItem(
            Strings.Format("MenuDeleteSelectedTodos", selected.Count),
            (_, _) => DeleteSelectedTodoItems()));
        menu.Opened += (_, _) => row.Background = TodoSelectionBrush;
        menu.Closed += (_, _) => UpdateTodoRowBackground(row);
        return true;
    }

    private void PrepareTodoSelectionForContextMenu(string itemId)
    {
        if (_selectedTodoItemIds.Count > 0 &&
            !_selectedTodoItemIds.Contains(itemId))
        {
            ClearTodoSelection();
        }
    }

    private void PrepareTodoDragSelection(string itemId)
    {
        _todoGroupDragItemIds.Clear();
        if (_selectedTodoItemIds.Count > 1 &&
            _selectedTodoItemIds.Contains(itemId))
        {
            foreach (var selectedId in _selectedTodoItemIds)
            {
                _todoGroupDragItemIds.Add(selectedId);
            }
            return;
        }

        ClearTodoSelection();
        _todoGroupDragItemIds.Add(itemId);
    }

    private void BeginTodoGroupDragVisuals(string sourceItemId)
    {
        _todoGroupDragRestingOpacities.Clear();
        if (!IsTodoGroupDrag)
        {
            return;
        }

        foreach (var row in _todoRows)
        {
            if (row.Tag is not string itemId ||
                !_todoGroupDragItemIds.Contains(itemId) ||
                string.Equals(itemId, sourceItemId, StringComparison.Ordinal))
            {
                continue;
            }

            var opacity = (double)row.GetAnimationBaseValue(OpacityProperty);
            _todoGroupDragRestingOpacities[itemId] = opacity;
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 0.25;
        }
    }

    private void EndTodoGroupDragVisuals()
    {
        foreach (var (itemId, opacity) in _todoGroupDragRestingOpacities)
        {
            var row = _todoRows.FirstOrDefault(candidate =>
                candidate.Tag is string candidateId &&
                string.Equals(candidateId, itemId, StringComparison.Ordinal));
            if (row == null)
            {
                continue;
            }

            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = opacity;
            UpdateTodoRowBackground(row);
        }
        _todoGroupDragRestingOpacities.Clear();
    }

    private bool RestrictTodoGroupDragToTrash()
    {
        if (!IsTodoGroupDrag)
        {
            return false;
        }

        if (_todoDrag != null)
        {
            _todoDrag.TargetId = null;
            _todoDrag.DropAtEnd = false;
        }
        return true;
    }

    private string TodoDragGhostText(string fallback)
    {
        return IsTodoGroupDrag
            ? Strings.Format("TodoDragSelectedCount", _todoGroupDragItemIds.Count)
            : fallback;
    }

    private bool DeleteTodoGroupDragItems()
    {
        if (!IsTodoGroupDrag)
        {
            return false;
        }

        DeleteSelectedTodoItems();
        ClearTodoDragGroupState();
        return true;
    }

    private void ClearTodoDragGroupState()
    {
        _todoGroupDragItemIds.Clear();
        _todoGroupDragRestingOpacities.Clear();
    }

    private void ConfigureTodoPathDrop(Border row, PaperItem item)
    {
        row.AddHandler(
            DragDrop.PreviewDragEnterEvent,
            new DragEventHandler((_, e) => UpdateTodoPathDropEffect(row, e)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDragOverEvent,
            new DragEventHandler((_, e) => UpdateTodoPathDropEffect(row, e)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDragLeaveEvent,
            new DragEventHandler((_, _) => ResetTodoPathDropVisual(row)),
            handledEventsToo: true);
        row.AddHandler(
            DragDrop.PreviewDropEvent,
            new DragEventHandler((_, e) =>
            {
                try
                {
                    var paths = GetTodoFileDropPaths(e.Data);
                    if (paths.Length != 1)
                    {
                        MessageBox.Show(
                            this,
                            Strings.Get("LinkedPathSingleDropMessage"),
                            Strings.Get("LinkedPathDropFailureTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var path = Path.GetFullPath(paths[0]);
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        MessageBox.Show(
                            this,
                            Strings.Format("LinkedPathMissingMessage", path),
                            Strings.Get("LinkedPathOpenFailureTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    LinkPathToTodo(item, path);
                    e.Effects = DragDropEffects.Link;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        Strings.Format("LinkedPathDropFailureMessage", ex.Message),
                        Strings.Get("LinkedPathDropFailureTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                finally
                {
                    ResetTodoPathDropVisual(row);
                    e.Handled = true;
                }
            }),
            handledEventsToo: true);
    }

    private void UpdateTodoPathDropEffect(Border row, DragEventArgs e)
    {
        var paths = GetTodoFileDropPaths(e.Data);
        if (paths.Length == 1)
        {
            e.Effects = DragDropEffects.Link;
            row.Background = NoteLinkTargetBgBrush;
            row.BorderBrush = NoteLinkTargetBorderBrush;
            row.BorderThickness = new Thickness(1);
            row.Padding = new Thickness(1, 3, 1, 3);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private static string[] GetTodoFileDropPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }
        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    }

    private void ResetTodoPathDropVisual(Border row)
    {
        if (ReferenceEquals(row, _linkedNoteDropRow) ||
            ReferenceEquals(row, _activeDropRow))
        {
            return;
        }

        row.BorderThickness = new Thickness(0, 2, 0, 2);
        row.BorderBrush = Brushes.Transparent;
        row.Padding = new Thickness(2);
        UpdateTodoRowBackground(row);
    }

    private void LinkPathToTodo(PaperItem item, string path)
    {
        if (string.Equals(item.LinkedPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(item.LinkedNoteId))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var previousItems = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.LinkedPath = path;
        item.LinkedNoteId = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
        RefreshCapsuleEligibilityForLinkedNoteChanges(previousItems);
    }

    private void UnlinkPathFromTodoItem(PaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            return;
        }

        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        PushUndoSnapshot();
        item.LinkedPath = null;
        _controller.MarkDirty();
        RebuildTodoRows(focusedId);
    }

    private Border BuildTodoPathLinkButton(
        PaperItem item,
        TodoTextBox text,
        TodoVisualMetrics metrics)
    {
        var path = item.LinkedPath ?? "";
        var showName = _controller.State.ShowLinkedNoteName;
        var allowLongName =
            showName && _controller.State.AllowLongLinkedNoteTitles;
        var label = PathDisplayName(path);

        string LinkedPathButtonLabel(bool isTodoMultiline) =>
            TodoLinkedPathLabel(path, label, allowLongName, isTodoMultiline);

        double LegacyLinkedPathButtonWidth(bool isTodoMultiline) =>
            isTodoMultiline
                ? Math.Max(44, metrics.CheckColumnWidth * 2)
                : Math.Max(50, metrics.CheckColumnWidth * 2.2);

        double LinkedPathButtonWidth(bool isTodoMultiline, string value)
        {
            var legacyWidth = LegacyLinkedPathButtonWidth(isTodoMultiline);
            if (!allowLongName)
            {
                return legacyWidth;
            }

            var measuredWidth = MeasureCapsuleTextWidth(
                value,
                metrics.LinkedNoteNameFontSize,
                FontWeights.SemiBold,
                AppTypography.UiFontFamily) + 10;
            return Math.Max(legacyWidth, Math.Ceiling(measuredWidth));
        }

        var linkedPathButtonText = LinkedPathButtonLabel(isTodoMultiline: false);
        var multilineLinkedPathButtonText = LinkedPathButtonLabel(isTodoMultiline: true);
        var width = showName
            ? Math.Max(
                LinkedPathButtonWidth(false, linkedPathButtonText),
                LinkedPathButtonWidth(true, multilineLinkedPathButtonText))
            : Math.Max(23, metrics.CheckColumnWidth);
        var glyph = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        var button = new Border
        {
            Width = width,
            MinWidth = Math.Max(23, metrics.CheckColumnWidth),
            MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
            Margin = new Thickness(1, 0, 0, 0),
            Padding = showName ? new Thickness(3, 1, 3, 1) : new Thickness(0),
            CornerRadius = new CornerRadius(RadiusControl),
            Cursor = Cursors.Hand,
            Child = glyph
        };

        void RefreshPresentation(bool hovered)
        {
            var valid = File.Exists(path) || Directory.Exists(path);
            var isDirectory = valid && Directory.Exists(path);
            glyph.Text = valid
                ? showName
                    ? (text.LineCount > 1
                        ? multilineLinkedPathButtonText
                        : linkedPathButtonText)
                    : isDirectory ? "\uE8B7" : "\uE7C3"
                : "!";
            glyph.FontFamily = showName || !valid
                ? AppTypography.UiFontFamily
                : new FontFamily("Segoe MDL2 Assets");
            glyph.FontSize = showName
                ? metrics.LinkedNoteNameFontSize
                : valid ? metrics.LinkedNoteIconFontSize : metrics.LinkedNoteIconFontSize + 1;
            glyph.Foreground = valid
                ? hovered ? TextBrush : WeakTextBrush
                : TrashTextBrush;
            glyph.Opacity = valid ? hovered ? 1.0 : 0.72 : 1.0;
            button.Background = valid
                ? hovered ? LinkedNoteLightBgBrush : LinkedNoteNormalBgBrush
                : hovered ? TrashHoverBgBrush : TrashBgBrush;
            button.ToolTip = valid
                ? Strings.Format("ToolTipOpenLinkedPath", path)
                : Strings.Format("ToolTipLinkedPathMissing", path);
        }

        var linkedPathNameLayoutQueued = false;
        void QueueLinkedPathNameLayoutUpdate()
        {
            if (!showName || linkedPathNameLayoutQueued)
            {
                return;
            }

            linkedPathNameLayoutQueued = true;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    linkedPathNameLayoutQueued = false;
                    RefreshPresentation(hovered: button.IsMouseOver);
                    glyph.TextWrapping = text.LineCount > 1
                        ? TextWrapping.Wrap
                        : TextWrapping.NoWrap;
                    glyph.MaxWidth = Math.Max(1, width - 6);
                }),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        if (showName)
        {
            text.SizeChanged += (_, _) => QueueLinkedPathNameLayoutUpdate();
            text.TextChanged += (_, _) => QueueLinkedPathNameLayoutUpdate();
        }

        RefreshPresentation(hovered: false);
        QueueLinkedPathNameLayoutUpdate();
        button.MouseEnter += (_, _) => RefreshPresentation(hovered: true);
        button.MouseLeave += (_, _) =>
        {
            RefreshPresentation(hovered: false);
            button.Opacity = 1.0;
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            button.Opacity = 0.72;
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            button.Opacity = 1.0;
            OpenTodoLinkedPath(item);
            RefreshPresentation(hovered: button.IsMouseOver);
            e.Handled = true;
        };
        return button;
    }

    private string TodoLinkedPathLabel(
        string path,
        string fileName,
        bool allowLongName,
        bool isTodoMultiline)
    {
        if (allowLongName)
        {
            var limit = isTodoMultiline ? 20 : 10;
            return CompactLinkedNoteTitleByDisplayWidth(
                fileName,
                limit,
                limit);
        }

        if (_controller.State.ShowLinkedPathExtensionOnly &&
            !Directory.Exists(path))
        {
            try
            {
                var extension = Path.GetExtension(fileName);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return extension;
                }
            }
            catch
            {
            }
        }

        return isTodoMultiline
            ? CompactLinkedNoteTitle(fileName, 6, 5)
            : CompactLinkedNoteTitle(fileName, 3, 3);
    }

    private static string PathDisplayName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private void OpenTodoLinkedPath(PaperItem item)
    {
        var path = item.LinkedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathMissingMessage", path),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RebuildTodoRows(CurrentFocusedTodoItemId() ?? item.Id);
            return;
        }

        OpenShellPath(path);
    }

    private void OpenTodoLinkedPathLocation(PaperItem item)
    {
        var path = item.LinkedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string? location;
        try
        {
            location = Directory.Exists(path)
                ? Directory.GetParent(path)?.FullName ?? path
                : Path.GetDirectoryName(path);
        }
        catch
        {
            location = null;
        }

        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathMissingMessage", path),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OpenShellPath(location);
    }

    private void OpenShellPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Strings.Format("LinkedPathOpenFailureMessage", ex.Message),
                Strings.Get("LinkedPathOpenFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
