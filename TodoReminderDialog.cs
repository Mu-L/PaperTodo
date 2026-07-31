using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal static class TodoReminderDialog
{
    public static bool TryShow(
        Window owner,
        DateTimeOffset initialValue,
        bool animate,
        out DateTimeOffset reminderAt)
    {
        reminderAt = default;
        DateTimeOffset? result = null;
        var initial = RoundUpToMinute(
            initialValue.ToLocalTime());
        var selectedDate = initial.Date;
        var selectedTime = initial.TimeOfDay;

        var dialog = new Window
        {
            Owner = owner,
            Title = Strings.Get("TodoReminderCustomTitle"),
            Width = 356,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = owner.Topmost,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            Language = AppTypography.Language,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        AppTypography.ApplyTextRendering(dialog);

        var root = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(16, 14, 16, 15),
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Opacity = Theme.IsDark ? 0.34 : 0.2
            }
        };
        var content = new StackPanel();

        var close = TodoReminderDialogControls.Button(
            "×",
            compact: true);
        close.IsCancel = true;
        close.Click += (_, _) => dialog.DialogResult = false;
        var title = new TextBlock
        {
            Text = Strings.Get("TodoReminderCustomTitle"),
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(14),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid
        {
            Cursor = Cursors.SizeAll
        };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left ||
                close.IsMouseOver)
            {
                return;
            }

            try
            {
                dialog.DragMove();
            }
            catch (InvalidOperationException)
            {
                // Windows may release the pointer while native moving starts.
            }
        };
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoReminderCustomHint"),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(10.8),
            Margin = new Thickness(0, 3, 0, 12)
        });

        var dateInput = CreateInput(
            selectedDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            maximumLength: 10);
        var previousDay = TodoReminderDialogControls.Button(
            "−",
            compact: true);
        var nextDay = TodoReminderDialogControls.Button(
            "+",
            compact: true);
        content.Children.Add(BuildAdjustableField(
            Strings.Get("TodoReminderDate"),
            dateInput,
            previousDay,
            nextDay));

        var timeInput = CreateInput(
            selectedTime.ToString(
                @"hh\:mm",
                CultureInfo.InvariantCulture),
            maximumLength: 5);
        var previousMinute = TodoReminderDialogControls.Button(
            "−",
            compact: true);
        var nextMinute = TodoReminderDialogControls.Button(
            "+",
            compact: true);
        var timeField = BuildAdjustableField(
            Strings.Get("TodoReminderTime"),
            timeInput,
            previousMinute,
            nextMinute);
        timeField.Margin = new Thickness(0, 9, 0, 0);
        content.Children.Add(timeField);

        var validation = new TextBlock
        {
            Foreground = Theme.DangerBrush,
            FontSize = AppTypography.Scale(10.8),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 9, 2, 0)
        };
        content.Children.Add(validation);

        var cancel = TodoReminderDialogControls.Button(
            Strings.Get("CommonCancel"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = TodoReminderDialogControls.Button(
            Strings.Get("CommonOk"),
            primary: true);
        confirm.IsDefault = true;
        confirm.Margin = new Thickness(8, 0, 0, 0);
        confirm.Click += (_, _) =>
        {
            if (!TryReadDate(out selectedDate))
            {
                ShowValidation(
                    Strings.Get("TodoReminderInvalidDate"));
                return;
            }
            if (!TryReadTime(out selectedTime))
            {
                ShowValidation(
                    Strings.Get("TodoReminderInvalidTime"));
                return;
            }

            var local = DateTime.SpecifyKind(
                selectedDate.Date.Add(selectedTime),
                DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                ShowValidation(
                    Strings.Get("TodoReminderInvalidLocalTime"));
                return;
            }

            var candidate = new DateTimeOffset(
                local,
                TimeZoneInfo.Local.GetUtcOffset(local));
            if (candidate <= DateTimeOffset.Now)
            {
                ShowValidation(
                    Strings.Get("TodoReminderFutureRequired"));
                return;
            }

            result = candidate;
            dialog.DialogResult = true;
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        content.Children.Add(actions);

        previousDay.Click += (_, _) => AdjustDays(-1);
        nextDay.Click += (_, _) => AdjustDays(1);
        previousMinute.Click += (_, _) => AdjustMinutes(-1);
        nextMinute.Click += (_, _) => AdjustMinutes(1);
        dateInput.PreviewMouseWheel += (_, e) =>
        {
            AdjustDays(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        timeInput.PreviewMouseWheel += (_, e) =>
        {
            AdjustMinutes(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        dateInput.PreviewKeyDown += (_, e) =>
            HandleAdjustmentKey(e, AdjustDays);
        timeInput.PreviewKeyDown += (_, e) =>
            HandleAdjustmentKey(e, AdjustMinutes);

        root.Child = content;
        dialog.Content = root;
        if (animate)
        {
            root.Opacity = 0;
            dialog.ContentRendered += (_, _) =>
                AnimationHelper.FadeIn(root, duration: 110);
        }

        if (dialog.ShowDialog() == true && result.HasValue)
        {
            reminderAt = result.Value;
            return true;
        }
        return false;

        void AdjustDays(int days)
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date;
            }
            selectedDate = selectedDate.AddDays(days);
            dateInput.Text = selectedDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            validation.Visibility = Visibility.Collapsed;
        }

        void AdjustMinutes(int minutes)
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date;
            }
            if (TryReadTime(out var time))
            {
                selectedTime = time;
            }

            var adjusted = selectedDate.Date
                .Add(selectedTime)
                .AddMinutes(minutes);
            selectedDate = adjusted.Date;
            selectedTime = adjusted.TimeOfDay;
            dateInput.Text = selectedDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            timeInput.Text = selectedTime.ToString(
                @"hh\:mm",
                CultureInfo.InvariantCulture);
            validation.Visibility = Visibility.Collapsed;
        }

        bool TryReadDate(out DateTime value)
        {
            return DateTime.TryParseExact(
                    dateInput.Text.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out value) ||
                DateTime.TryParse(
                    dateInput.Text.Trim(),
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out value);
        }

        bool TryReadTime(out TimeSpan value)
        {
            var parsed = DateTime.TryParseExact(
                timeInput.Text.Trim(),
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var time);
            value = parsed ? time.TimeOfDay : default;
            return parsed;
        }

        void ShowValidation(string message)
        {
            validation.Text = message;
            validation.Visibility = Visibility.Visible;
        }
    }

    private static Border BuildAdjustableField(
        string label,
        TextBox input,
        Button decrease,
        Button increase)
    {
        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        controls.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        controls.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        input.Margin = new Thickness(7, 0, 7, 0);
        Grid.SetColumn(input, 1);
        Grid.SetColumn(increase, 2);
        controls.Children.Add(decrease);
        controls.Children.Add(input);
        controls.Children.Add(increase);

        var layout = new StackPanel();
        layout.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(10.8),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 5)
        });
        layout.Children.Add(controls);
        return new Border
        {
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 18 : 10)),
            BorderBrush = Theme.Tint(38),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9),
            Child = layout
        };
    }

    private static TextBox CreateInput(
        string text,
        int maximumLength) =>
        new()
        {
            Text = text,
            MaxLength = maximumLength,
            Height = 28,
            Padding = new Thickness(7, 3, 7, 3),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.PaperBrush,
            Foreground = Theme.TextBrush,
            BorderBrush = Theme.Tint(58),
            BorderThickness = new Thickness(1),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold
        };

    private static void HandleAdjustmentKey(
        KeyEventArgs e,
        Action<int> adjust)
    {
        if (e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        adjust(e.Key == Key.Up ? 1 : -1);
        e.Handled = true;
    }

    private static DateTimeOffset RoundUpToMinute(
        DateTimeOffset value)
    {
        var minute = new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            0,
            value.Offset);
        return minute == value ? minute : minute.AddMinutes(1);
    }
}
