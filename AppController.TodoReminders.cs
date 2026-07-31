using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private static readonly TimeSpan TodoReminderMaximumTimerInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TodoReminderMinimumTimerInterval =
        TimeSpan.FromMilliseconds(250);

    internal void NotifyTodoReminderChanged(bool saveImmediately)
    {
        if (saveImmediately)
        {
            SaveNow();
        }
        else
        {
            MarkDirty();
        }

        RefreshTodoReminderSchedule();
    }

    internal void NotifyTodoReminderCollectionChanged()
    {
        if (State.ExperimentalTodoReminders)
        {
            RefreshTodoReminderSchedule();
        }
    }

    internal void RefreshTodoReminderFeature()
    {
        foreach (var window in _windows.Values)
        {
            window.UpdateTodoReminderFeature();
        }

        RefreshTodoReminderSchedule();
    }

    private void RefreshTodoReminderSchedule()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                (Action)RefreshTodoReminderSchedule,
                DispatcherPriority.Background);
            return;
        }

        StopTodoReminderTimer();
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var nextReminder = State.Papers
            .Where(paper => paper.Type == PaperTypes.Todo)
            .SelectMany(paper => paper.Items)
            .Where(item => !item.Done && item.ReminderAt.HasValue)
            .Select(item => item.ReminderAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        if (nextReminder == DateTimeOffset.MaxValue)
        {
            return;
        }

        var delay = nextReminder - DateTimeOffset.Now;
        var interval = delay <= TodoReminderMinimumTimerInterval
            ? TodoReminderMinimumTimerInterval
            : delay >= TodoReminderMaximumTimerInterval
                ? TodoReminderMaximumTimerInterval
                : delay;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval
        };
        timer.Tick += OnTodoReminderTimerTick;
        _todoReminderTimer = timer;
        timer.Start();
    }

    private void OnTodoReminderTimerTick(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _todoReminderTimer))
        {
            return;
        }

        StopTodoReminderTimer();
        var now = DateTimeOffset.Now;
        foreach (var window in _windows.Values)
        {
            window.RefreshTodoReminderCountdowns(now);
        }
        ProcessDueTodoReminders(now);
    }

    private void ProcessDueTodoReminders(DateTimeOffset now)
    {
        if (IsExiting || !State.ExperimentalTodoReminders)
        {
            return;
        }

        var due = State.Papers
            .Where(paper => paper.Type == PaperTypes.Todo)
            .SelectMany(paper => paper.Items
                .Where(item =>
                    !item.Done &&
                    item.ReminderAt is { } reminderAt &&
                    reminderAt <= now)
                .Select(item => (Paper: paper, Item: item)))
            .ToList();
        if (due.Count == 0)
        {
            RefreshTodoReminderSchedule();
            return;
        }

        foreach (var (_, item) in due)
        {
            item.ReminderAt = null;
        }

        foreach (var paperId in due.Select(entry => entry.Paper.Id).Distinct(StringComparer.Ordinal))
        {
            if (_windows.TryGetValue(paperId, out var window))
            {
                window.RefreshTodoReminderAfterTrigger();
            }
        }

        SaveNow();
        try
        {
            ShowTodoReminderBalloon(due);
        }
        catch
        {
            // A stale shell notification area must not break future reminder scheduling.
        }
        RefreshTodoReminderSchedule();
    }

    private void ShowTodoReminderBalloon(
        IReadOnlyList<(PaperData Paper, PaperItem Item)> due)
    {
        if (_trayIcon == null || due.Count == 0)
        {
            return;
        }

        var first = due[0];
        var firstText = CompactTodoReminderText(first.Item.Text);
        var message = due.Count == 1
            ? Strings.Format(
                "TodoReminderBalloonSingleFormat",
                PaperTitleText(first.Paper),
                firstText)
            : Strings.Format(
                "TodoReminderBalloonMultipleFormat",
                due.Count,
                firstText);
        _trayIcon.ShowBalloonTip(
            Strings.Get("TodoReminderBalloonTitle"),
            message,
            BalloonIcon.Info);
    }

    private static string CompactTodoReminderText(string? text)
    {
        var compact = string.Join(
            " ",
            (text ?? "")
                .Split(
                    new[] { '\r', '\n', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        if (string.IsNullOrWhiteSpace(compact))
        {
            return Strings.Get("TodoReminderUnnamed");
        }

        const int maximumLength = 90;
        return compact.Length <= maximumLength
            ? compact
            : compact[..(maximumLength - 1)] + "…";
    }

    private void StopTodoReminderTimer()
    {
        if (_todoReminderTimer == null)
        {
            return;
        }

        _todoReminderTimer.Stop();
        _todoReminderTimer.Tick -= OnTodoReminderTimerTick;
        _todoReminderTimer = null;
    }
}
