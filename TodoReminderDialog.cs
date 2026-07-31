using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

internal static class TodoReminderDialog
{
    public static bool TryShow(
        Window owner,
        DateTimeOffset initialValue,
        out DateTimeOffset reminderAt)
    {
        reminderAt = default;
        DateTimeOffset? result = null;
        var initial = RoundUpToFiveMinutes(initialValue.ToLocalTime());

        var dialog = new Window
        {
            Owner = owner,
            Title = Strings.Get("TodoReminderCustomTitle"),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
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
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };
        var layout = new StackPanel();

        var title = new TextBlock
        {
            Text = Strings.Get("TodoReminderCustomTitle"),
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(16),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
            Cursor = Cursors.SizeAll
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { dialog.DragMove(); } catch { }
            }
        };
        layout.Children.Add(title);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dateColumn = new StackPanel();
        dateColumn.Children.Add(FieldLabel(Strings.Get("TodoReminderDate")));
        var datePicker = new DatePicker
        {
            SelectedDate = initial.Date,
            DisplayDate = initial.Date,
            DisplayDateStart = DateTime.Today,
            SelectedDateFormat = DatePickerFormat.Short,
            Height = 30,
            Padding = new Thickness(5, 1, 5, 1),
            Background = Theme.PaperBrush,
            Foreground = Theme.TextBrush,
            BorderBrush = Theme.PaperBorderBrush
        };
        dateColumn.Children.Add(datePicker);
        Grid.SetColumn(dateColumn, 0);
        fields.Children.Add(dateColumn);

        var timeColumn = new StackPanel();
        timeColumn.Children.Add(FieldLabel(Strings.Get("TodoReminderTime")));
        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        var hour = TimeComboBox(
            Enumerable.Range(0, 24).Select(value => value.ToString("00")).ToArray(),
            initial.Hour);
        var minute = TimeComboBox(
            Enumerable.Range(0, 12).Select(value => (value * 5).ToString("00")).ToArray(),
            initial.Minute / 5);
        timeRow.Children.Add(hour);
        timeRow.Children.Add(new TextBlock
        {
            Text = ":",
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(14),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        });
        timeRow.Children.Add(minute);
        timeColumn.Children.Add(timeRow);
        Grid.SetColumn(timeColumn, 2);
        fields.Children.Add(timeColumn);
        layout.Children.Add(fields);

        var validation = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            FontSize = AppTypography.Scale(11.5),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 9, 0, 0)
        };
        layout.Children.Add(validation);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = CreateButton(Strings.Get("CommonCancel"), active: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = CreateButton(Strings.Get("CommonOk"), active: true);
        confirm.IsDefault = true;
        confirm.Margin = new Thickness(8, 0, 0, 0);
        confirm.Click += (_, _) =>
        {
            if (datePicker.SelectedDate is not { } selectedDate ||
                hour.SelectedIndex < 0 ||
                minute.SelectedIndex < 0)
            {
                ShowValidation(Strings.Get("TodoReminderFutureRequired"));
                return;
            }

            var local = DateTime.SpecifyKind(
                selectedDate.Date
                    .AddHours(hour.SelectedIndex)
                    .AddMinutes(minute.SelectedIndex * 5),
                DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                ShowValidation(Strings.Get("TodoReminderInvalidLocalTime"));
                return;
            }

            var candidate = new DateTimeOffset(
                local,
                TimeZoneInfo.Local.GetUtcOffset(local));
            if (candidate <= DateTimeOffset.Now)
            {
                ShowValidation(Strings.Get("TodoReminderFutureRequired"));
                return;
            }

            result = candidate;
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        layout.Children.Add(buttons);

        void ShowValidation(string message)
        {
            validation.Text = message;
            validation.Visibility = Visibility.Visible;
        }

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
            }
        };

        root.Child = layout;
        dialog.Content = root;
        if (dialog.ShowDialog() == true && result.HasValue)
        {
            reminderAt = result.Value;
            return true;
        }

        return false;
    }

    private static DateTimeOffset RoundUpToFiveMinutes(DateTimeOffset value)
    {
        var withoutSeconds = new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            0,
            value.Offset);
        var remainder = withoutSeconds.Minute % 5;
        var alreadyOnBoundary =
            remainder == 0 &&
            value.Second == 0 &&
            value.Millisecond == 0 &&
            value.Ticks % TimeSpan.TicksPerMillisecond == 0;
        return alreadyOnBoundary
            ? withoutSeconds
            : withoutSeconds.AddMinutes(
                remainder == 0 ? 5 : 5 - remainder);
    }

    private static TextBlock FieldLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            Margin = new Thickness(0, 0, 0, 5)
        };
    }

    private static ComboBox TimeComboBox(
        IReadOnlyList<string> values,
        int selectedIndex)
    {
        return new ComboBox
        {
            ItemsSource = values,
            SelectedIndex = Math.Clamp(selectedIndex, 0, values.Count - 1),
            Width = 54,
            Height = 30,
            Padding = new Thickness(6, 1, 3, 1),
            Background = Theme.PaperBrush,
            Foreground = Theme.TextBrush,
            BorderBrush = Theme.PaperBorderBrush
        };
    }

    private static Button CreateButton(string text, bool active)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            active ? Theme.ActiveBrush : Theme.Tint(28)));
        style.Setters.Add(new Setter(
            Control.ForegroundProperty,
            active ? Theme.PaperBrush : Theme.TextBrush));
        style.Setters.Add(new Setter(Control.FontSizeProperty, AppTypography.Scale(13)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        presenter.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(
            Control.BackgroundProperty,
            active ? Theme.CheckBoxActiveHoverBrush : Theme.Tint(46)));
        template.Triggers.Add(hover);

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }
}
