using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void UpdateTodoReminderFeature()
    {
        if (_paper.Type == PaperTypes.Todo)
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }
    }

    internal void RefreshTodoReminderAfterTrigger()
    {
        if (_paper.Type == PaperTypes.Todo && _todoDrag == null)
        {
            RebuildTodoRows(CurrentFocusedTodoItemId());
        }
    }

    private Border BuildTodoReminderButton(
        PaperItem item,
        TodoVisualMetrics metrics)
    {
        var hasReminder = item.ReminderAt.HasValue;
        var glyph = new TextBlock
        {
            Text = "\uE823",
            Foreground = hasReminder ? Theme.ActiveBrush : WeakTextBrush,
            Opacity = hasReminder ? 1.0 : 0.44,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = Math.Max(
                AppTypography.Scale(10.5),
                metrics.TextFontSize - AppTypography.Scale(1.5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Border
        {
            Width = Math.Max(
                AppTypography.Scale(17),
                metrics.CheckColumnWidth - AppTypography.Scale(6)),
            MinHeight = metrics.RowMinHeight,
            Margin = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(RadiusSmall),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = glyph,
            Visibility = item.Done ? Visibility.Hidden : Visibility.Visible,
            ToolTip = TodoReminderToolTip(item)
        };

        button.MouseEnter += (_, _) =>
        {
            button.Background = HoverBrush;
            glyph.Opacity = 1.0;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            glyph.Opacity = item.ReminderAt.HasValue ? 1.0 : 0.44;
        };
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            glyph.Opacity = 0.66;
            e.Handled = true;
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            glyph.Opacity = 1.0;
            OpenTodoReminderMenu(button, item.Id);
            e.Handled = true;
        };
        return button;
    }

    private string TodoReminderToolTip(PaperItem item)
    {
        return item.ReminderAt is { } reminderAt
            ? Strings.Format(
                "TodoReminderSetForFormat",
                reminderAt.ToLocalTime().ToString(
                    "g",
                    CultureInfo.CurrentCulture))
            : Strings.Get("TodoReminderSet");
    }

    private void OpenTodoReminderMenu(
        FrameworkElement placementTarget,
        string itemId)
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            return;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null || item.Done)
        {
            return;
        }

        var menu = CreateContextMenu();
        menu.Items.Add(MenuHeader(Strings.Get("TodoReminderMenuHeader")));
        var now = DateTimeOffset.Now;
        var defaultMinutes =
            ExperimentalTodoReminderOptions.NormalizeQuickMinutes(
                _controller.State.ExperimentalTodoReminderQuickMinutes);
        var minutePresets = new[]
        {
            defaultMinutes,
            10,
            30,
            60
        }.Distinct().ToArray();
        foreach (var minutes in minutePresets)
        {
            var label = Strings.Format("TodoReminderInMinutesFormat", minutes);
            if (minutes == defaultMinutes)
            {
                label += Strings.Get("TodoReminderDefaultSuffix");
            }

            var reminderAt = now.AddMinutes(minutes);
            menu.Items.Add(MenuItem(
                label,
                (_, _) => QueueTodoReminderChange(itemId, reminderAt)));
        }

        menu.Items.Add(MenuSeparator());
        var todayEvening = LocalReminderTime(now.LocalDateTime.Date, 18, 0);
        if (todayEvening is { } evening && evening > now)
        {
            menu.Items.Add(MenuItem(
                Strings.Format(
                    "TodoReminderPresetAtFormat",
                    evening.ToLocalTime().ToString(
                        "ddd HH:mm",
                        CultureInfo.CurrentCulture)),
                (_, _) => QueueTodoReminderChange(itemId, evening)));
        }

        var tomorrowMorning = LocalReminderTime(
            now.LocalDateTime.Date.AddDays(1),
            9,
            0);
        if (tomorrowMorning is { } morning)
        {
            menu.Items.Add(MenuItem(
                Strings.Format(
                    "TodoReminderPresetAtFormat",
                    morning.ToLocalTime().ToString(
                        "ddd HH:mm",
                        CultureInfo.CurrentCulture)),
                (_, _) => QueueTodoReminderChange(itemId, morning)));
        }

        var customInitial = item.ReminderAt is { } existing && existing > now
            ? existing
            : now.AddMinutes(defaultMinutes);
        menu.Items.Add(MenuItem(
            Strings.Get("TodoReminderCustom"),
            (_, _) => Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (IsClosed ||
                        !TodoReminderDialog.TryShow(
                            this,
                            customInitial,
                            out var customReminder))
                    {
                        return;
                    }

                    SetTodoItemReminder(itemId, customReminder);
                }),
                DispatcherPriority.Background)));

        if (item.ReminderAt.HasValue)
        {
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuItem(
                Strings.Get("TodoReminderClear"),
                (_, _) => QueueTodoReminderChange(itemId, null)));
        }

        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void QueueTodoReminderChange(
        string itemId,
        DateTimeOffset? reminderAt)
    {
        _ = Dispatcher.BeginInvoke(
            (Action)(() => SetTodoItemReminder(itemId, reminderAt)),
            DispatcherPriority.Background);
    }

    private void SetTodoItemReminder(
        string itemId,
        DateTimeOffset? reminderAt)
    {
        if (!_controller.State.ExperimentalTodoReminders)
        {
            return;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null ||
            item.Done ||
            (reminderAt.HasValue && reminderAt.Value <= DateTimeOffset.Now) ||
            item.ReminderAt == reminderAt)
        {
            return;
        }

        PushUndoSnapshot();
        item.ReminderAt = reminderAt;
        _controller.NotifyTodoReminderChanged(saveImmediately: true);
        RebuildTodoRows(item.Id);
    }

    private static DateTimeOffset? LocalReminderTime(
        DateTime date,
        int hour,
        int minute)
    {
        var local = DateTime.SpecifyKind(
            date.Date.AddHours(hour).AddMinutes(minute),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
        {
            return null;
        }

        return new DateTimeOffset(
            local,
            TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
