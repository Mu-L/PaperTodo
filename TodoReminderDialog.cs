using System.Globalization;
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
        bool animate,
        out DateTimeOffset reminderAt)
    {
        reminderAt = default;
        DateTimeOffset? result = null;
        var initial = RoundUpToFiveMinutes(
            initialValue.ToLocalTime());
        var selectedDate = initial.Date;
        var selectedHour = initial.Hour;
        var selectedMinute = initial.Minute;

        var dialog = new Window
        {
            Owner = owner,
            Title = Strings.Get("TodoReminderCustomTitle"),
            Width = 440,
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
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 5,
                Opacity = Theme.IsDark ? 0.38 : 0.22
            }
        };
        var rootLayout = new Grid();
        rootLayout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        rootLayout.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader(dialog);
        Grid.SetRow(header, 0);
        rootLayout.Children.Add(header);

        var content = new StackPanel
        {
            Margin = new Thickness(18, 16, 18, 18)
        };

        var dateInput = CreateDateInput(initial.Date);
        var dateSummary = new TextBlock
        {
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var previousDay = CreateSquareButton("‹", 32);
        var nextDay = CreateSquareButton("›", 32);
        var dateNavigation = new Grid();
        dateNavigation.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        dateNavigation.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        dateNavigation.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(previousDay, 0);
        Grid.SetColumn(dateInput, 1);
        Grid.SetColumn(nextDay, 2);
        dateNavigation.Children.Add(previousDay);
        dateNavigation.Children.Add(dateInput);
        dateNavigation.Children.Add(nextDay);

        var today = CreateChipButton(
            Strings.Get("TodoReminderToday"));
        var tomorrow = CreateChipButton(
            Strings.Get("TodoReminderTomorrow"));
        var quickDates = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        quickDates.Children.Add(today);
        tomorrow.Margin = new Thickness(8, 0, 0, 0);
        quickDates.Children.Add(tomorrow);

        var dateBody = new StackPanel();
        dateBody.Children.Add(dateNavigation);
        dateBody.Children.Add(dateSummary);
        dateBody.Children.Add(quickDates);
        content.Children.Add(BuildFieldCard(
            Strings.Get("TodoReminderDate"),
            "▦",
            dateBody));

        var selectionText = new TextBlock
        {
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var selectionIcon = new TextBlock
        {
            Text = "●",
            Foreground = Theme.ActiveBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(8),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };
        var selectionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        selectionRow.Children.Add(selectionIcon);
        selectionRow.Children.Add(selectionText);
        var selection = new Border
        {
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 34 : 22)),
            BorderBrush = Theme.Tint(64),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 12, 0, 0),
            Child = selectionRow
        };

        var validationText = new TextBlock
        {
            Foreground = Theme.DangerBrush,
            FontSize = AppTypography.Scale(11.5),
            TextWrapping = TextWrapping.Wrap
        };
        var validation = new Border
        {
            Background = Theme.Danger(
                (byte)(Theme.IsDark ? 30 : 18)),
            BorderBrush = Theme.Danger(70),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0),
            Child = validationText
        };

        var hourValue = CreateTimeValue();
        var minuteValue = CreateTimeValue();
        var hourStepper = CreateTimeStepper(
            hourValue,
            () => AdjustMinutes(-60),
            () => AdjustMinutes(60));
        var minuteStepper = CreateTimeStepper(
            minuteValue,
            () => AdjustMinutes(-5),
            () => AdjustMinutes(5));
        hourStepper.PreviewMouseWheel += (_, e) =>
        {
            AdjustMinutes(e.Delta > 0 ? 60 : -60);
            e.Handled = true;
        };
        minuteStepper.PreviewMouseWheel += (_, e) =>
        {
            AdjustMinutes(e.Delta > 0 ? 5 : -5);
            e.Handled = true;
        };

        var timeRow = new Grid();
        timeRow.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        timeRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        timeRow.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        var separator = new TextBlock
        {
            Text = ":",
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(22),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 1)
        };
        Grid.SetColumn(hourStepper, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(minuteStepper, 2);
        timeRow.Children.Add(hourStepper);
        timeRow.Children.Add(separator);
        timeRow.Children.Add(minuteStepper);
        var timeCard = BuildFieldCard(
            Strings.Get("TodoReminderTime"),
            "◷",
            timeRow);
        timeCard.Margin = new Thickness(0, 12, 0, 0);
        content.Children.Add(timeCard);
        content.Children.Add(selection);
        content.Children.Add(validation);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = CreateButton(
            Strings.Get("CommonCancel"),
            primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = CreateButton(
            Strings.Get("CommonOk"),
            primary: true);
        confirm.IsDefault = true;
        confirm.Margin = new Thickness(8, 0, 0, 0);
        confirm.Click += (_, _) =>
        {
            if (!TryReadDate(out var date))
            {
                ShowValidation(
                    Strings.Get("TodoReminderInvalidDate"));
                return;
            }

            selectedDate = date;
            var local = DateTime.SpecifyKind(
                selectedDate.Date
                    .AddHours(selectedHour)
                    .AddMinutes(selectedMinute),
                DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                ShowValidation(
                    Strings.Get(
                        "TodoReminderInvalidLocalTime"));
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
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);

        Grid.SetRow(content, 1);
        rootLayout.Children.Add(content);
        root.Child = rootLayout;
        dialog.Content = root;

        previousDay.Click += (_, _) =>
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date.Date.AddDays(-1);
            }
            else
            {
                selectedDate = selectedDate.AddDays(-1);
            }
            RefreshSelection();
        };
        nextDay.Click += (_, _) =>
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date.Date.AddDays(1);
            }
            else
            {
                selectedDate = selectedDate.AddDays(1);
            }
            RefreshSelection();
        };
        today.Click += (_, _) =>
        {
            selectedDate = DateTime.Today;
            RefreshSelection();
        };
        tomorrow.Click += (_, _) =>
        {
            selectedDate = DateTime.Today.AddDays(1);
            RefreshSelection();
        };
        dateInput.LostKeyboardFocus += (_, _) =>
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date;
                RefreshSelection();
            }
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
            }
        };

        if (animate)
        {
            root.Opacity = 0;
            AnimationHelper.EnsureTransform(root);
            var scale = AnimationHelper.GetScaleTransform(root);
            var translate =
                AnimationHelper.GetTranslateTransform(root);
            scale.ScaleX = 0.97;
            scale.ScaleY = 0.97;
            translate.Y = 8;
            dialog.ContentRendered += (_, _) =>
            {
                AnimationHelper.FadeTo(
                    root,
                    1,
                    duration: 120);
                AnimationHelper.ScaleTo(
                    root,
                    1,
                    duration: 150,
                    easing: AnimationHelper.QuickEase);
                AnimationHelper.TranslateTo(
                    root,
                    0,
                    0,
                    duration: 150,
                    easing: AnimationHelper.QuickEase);
            };
        }

        RefreshSelection();
        if (dialog.ShowDialog() == true && result.HasValue)
        {
            reminderAt = result.Value;
            return true;
        }

        return false;

        void AdjustMinutes(int delta)
        {
            if (TryReadDate(out var date))
            {
                selectedDate = date;
            }

            var adjusted = selectedDate.Date
                .AddHours(selectedHour)
                .AddMinutes(selectedMinute + delta);
            selectedDate = adjusted.Date;
            selectedHour = adjusted.Hour;
            selectedMinute = adjusted.Minute;
            RefreshSelection();
        }

        bool TryReadDate(out DateTime date)
        {
            var text = dateInput.Text.Trim();
            if (DateTime.TryParseExact(
                    text,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var exact))
            {
                date = exact.Date;
                return true;
            }

            if (DateTime.TryParse(
                    text,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var localized))
            {
                date = localized.Date;
                return true;
            }

            date = default;
            return false;
        }

        void RefreshSelection()
        {
            dateInput.Text = selectedDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            dateSummary.Text = selectedDate.ToString(
                "dddd",
                CultureInfo.CurrentUICulture);
            hourValue.Text = selectedHour.ToString(
                "00",
                CultureInfo.InvariantCulture);
            minuteValue.Text = selectedMinute.ToString(
                "00",
                CultureInfo.InvariantCulture);
            selectionText.Text = Strings.Format(
                "TodoReminderSelectionFormat",
                selectedDate.ToString(
                    "D",
                    CultureInfo.CurrentUICulture),
                selectedHour,
                selectedMinute);
            ApplyChipState(
                today,
                selectedDate == DateTime.Today);
            ApplyChipState(
                tomorrow,
                selectedDate ==
                    DateTime.Today.AddDays(1));
            validation.Visibility = Visibility.Collapsed;
        }

        void ShowValidation(string message)
        {
            validationText.Text = message;
            validation.Visibility = Visibility.Visible;
            if (animate)
            {
                AnimationHelper.QuickBounce(
                    validation,
                    scale: 1.012,
                    duration: 70);
            }
        }
    }

    private static Border BuildHeader(Window dialog)
    {
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 48 : 34)),
            Child = new TextBlock
            {
                Text = "◷",
                Foreground = Theme.ActiveBrush,
                FontFamily = AppTypography.SymbolFontFamily,
                FontSize = AppTypography.Scale(17),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var titles = new StackPanel
        {
            Margin = new Thickness(11, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titles.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoReminderCustomTitle"),
            Foreground = Theme.TextBrush,
            FontSize = AppTypography.Scale(15),
            FontWeight = FontWeights.SemiBold
        });
        titles.Children.Add(new TextBlock
        {
            Text = Strings.Get("TodoReminderCustomHint"),
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var close = CreateSquareButton("×", 28);
        close.FontSize = AppTypography.Scale(16);
        close.IsCancel = true;
        close.Click += (_, _) => dialog.DialogResult = false;

        var row = new Grid();
        row.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        row.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(close, 2);
        row.Children.Add(icon);
        row.Children.Add(titles);
        row.Children.Add(close);

        var header = new Border
        {
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 20 : 13)),
            BorderBrush = Theme.Tint(36),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(16, 16, 0, 0),
            Padding = new Thickness(18, 14, 12, 14),
            Cursor = Cursors.SizeAll,
            Child = row
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left ||
                IsButtonSource(e.OriginalSource as DependencyObject))
            {
                return;
            }

            try
            {
                dialog.DragMove();
            }
            catch (InvalidOperationException)
            {
                // Mouse state may change while Windows begins the native move.
            }
        };
        return header;
    }

    private static bool IsButtonSource(
        DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase)
            {
                return true;
            }
            source = source switch
            {
                FrameworkElement element => element.Parent,
                FrameworkContentElement content => content.Parent,
                _ => null
            };
        }
        return false;
    }

    private static Border BuildFieldCard(
        string label,
        string icon,
        UIElement body)
    {
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(1, 0, 0, 10)
        };
        heading.Children.Add(new TextBlock
        {
            Text = icon,
            Foreground = Theme.ActiveBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(12),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        heading.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var layout = new StackPanel();
        layout.Children.Add(heading);
        layout.Children.Add(body);
        return new Border
        {
            Background = Theme.Tint(
                (byte)(Theme.IsDark ? 17 : 9)),
            BorderBrush = Theme.Tint(38),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 13, 13),
            Child = layout
        };
    }

    private static TextBox CreateDateInput(DateTime value)
    {
        var input = new TextBox
        {
            Text = value.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            Height = 32,
            Margin = new Thickness(8, 0, 8, 0),
            Padding = new Thickness(8, 4, 8, 4),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.PaperBrush,
            Foreground = Theme.TextBrush,
            BorderBrush = Theme.Tint(64),
            BorderThickness = new Thickness(1),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12.5),
            FontWeight = FontWeights.SemiBold,
            MaxLength = 10,
            ToolTip = "yyyy-MM-dd"
        };
        return input;
    }

    private static TextBlock CreateTimeValue() => new()
    {
        Foreground = Theme.TextBrush,
        FontFamily = AppTypography.UiFontFamily,
        FontSize = AppTypography.Scale(20),
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Border CreateTimeStepper(
        TextBlock value,
        Action decrease,
        Action increase)
    {
        var minus = CreateSquareButton("−", 30);
        var plus = CreateSquareButton("+", 30);
        minus.Click += (_, _) => decrease();
        plus.Click += (_, _) => increase();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(minus, 0);
        Grid.SetColumn(value, 1);
        Grid.SetColumn(plus, 2);
        grid.Children.Add(minus);
        grid.Children.Add(value);
        grid.Children.Add(plus);
        return new Border
        {
            MinWidth = 142,
            Background = Theme.PaperBrush,
            BorderBrush = Theme.Tint(58),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            Child = grid
        };
    }

    private static Button CreateSquareButton(
        string text,
        double size)
    {
        var button = CreateButton(text, primary: false);
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.Padding = new Thickness(0);
        button.FontSize = AppTypography.Scale(15);
        return button;
    }

    private static Button CreateChipButton(string text)
    {
        var button = CreateButton(text, primary: false);
        button.MinWidth = 76;
        button.Padding = new Thickness(12, 5, 12, 5);
        button.FontSize = AppTypography.Scale(11.5);
        return button;
    }

    private static void ApplyChipState(
        Button button,
        bool active)
    {
        button.BorderBrush =
            active ? Theme.ActiveBrush : Brushes.Transparent;
        button.BorderThickness =
            active ? new Thickness(1) : new Thickness(0);
        button.Foreground =
            active ? Theme.ActiveBrush : Theme.TextBrush;
        button.FontWeight =
            active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static Button CreateButton(
        string text,
        bool primary)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(
            Control.PaddingProperty,
            new Thickness(16, 7, 16, 7)));
        style.Setters.Add(new Setter(
            Control.BorderThicknessProperty,
            new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.BorderBrushProperty,
            Brushes.Transparent));
        style.Setters.Add(new Setter(
            Control.BackgroundProperty,
            primary ? Theme.ActiveBrush : Theme.Tint(24)));
        style.Setters.Add(new Setter(
            Control.ForegroundProperty,
            primary ? Theme.PaperBrush : Theme.TextBrush));
        style.Setters.Add(new Setter(
            Control.FontSizeProperty,
            AppTypography.Scale(13)));
        style.Setters.Add(new Setter(
            Control.CursorProperty,
            Cursors.Hand));
        style.Setters.Add(new Setter(
            FrameworkElement.MinWidthProperty,
            72.0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(8));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(
                Control.BackgroundProperty));
        border.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(
                Control.BorderBrushProperty));
        border.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(
                Control.BorderThicknessProperty));
        border.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(
                Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(
            typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(
                ContentControl.ContentProperty));
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
            primary
                ? Theme.CheckBoxActiveHoverBrush
                : Theme.Tint(44)));
        template.Triggers.Add(hover);

        var pressed = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(
            UIElement.OpacityProperty,
            0.78));
        template.Triggers.Add(pressed);
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            template));

        return new Button
        {
            Content = text,
            Style = style
        };
    }

    private static DateTimeOffset RoundUpToFiveMinutes(
        DateTimeOffset value)
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
            value.Ticks %
                TimeSpan.TicksPerMillisecond == 0;
        return alreadyOnBoundary
            ? withoutSeconds
            : withoutSeconds.AddMinutes(
                remainder == 0 ? 5 : 5 - remainder);
    }
}
