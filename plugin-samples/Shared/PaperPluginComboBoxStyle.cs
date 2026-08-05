using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.Controls;

/// <summary>
/// PaperTodo-style ComboBox skin for trusted native sample plugins.
/// The host does not inject WPF styles into plugin visual trees, so native plugins must opt in.
/// </summary>
internal static class PaperPluginComboBoxStyle
{
    public static void Apply(
        ComboBox comboBox,
        PaperBodyTheme theme,
        double fontSize)
    {
        ArgumentNullException.ThrowIfNull(comboBox);
        ArgumentNullException.ThrowIfNull(theme);

        comboBox.IsDropDownOpen = false;

        var text = Brush(theme.TextColor, "#202020");
        var weak = Brush(theme.WeakTextColor, "#707070");
        var border = Brush(theme.BorderColor, "#807050");
        var paper = Brush(
            theme.PaperColor,
            theme.IsDark ? "#FF25221E" : "#FFFFF8E6");
        var surface = Brush(
            theme.IsDark ? "#18FFFFFF" : "#0C000000",
            "#0C000000");
        var hover = Brush(
            AddAlpha(theme.AccentColor, theme.IsDark ? (byte)42 : (byte)24),
            "#18B07A31");
        var selected = Brush(
            AddAlpha(theme.AccentColor, theme.IsDark ? (byte)62 : (byte)34),
            "#22B07A31");
        var accent = Brush(theme.AccentColor, "#B07A31");

        comboBox.Foreground = text;
        comboBox.Background = surface;
        comboBox.BorderBrush = border;
        comboBox.BorderThickness = new Thickness(1);
        comboBox.Padding = new Thickness(9, 3, 7, 3);
        comboBox.FontFamily = new FontFamily(theme.FontFamily);
        comboBox.FontSize = fontSize;
        comboBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        comboBox.Style = BuildComboBoxStyle(
            paper,
            surface,
            hover,
            text,
            weak,
            border,
            accent);
        comboBox.ItemContainerStyle = BuildItemStyle(
            hover,
            selected,
            text,
            accent);
    }

    private static Style BuildComboBoxStyle(
        Brush paper,
        Brush surface,
        Brush hover,
        Brush text,
        Brush weak,
        Brush border,
        Brush accent)
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 30.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            BuildComboBoxTemplate(
                paper,
                surface,
                hover,
                text,
                weak,
                border,
                accent)));
        return style;
    }

    private static ControlTemplate BuildComboBoxTemplate(
        Brush paper,
        Brush surface,
        Brush hover,
        Brush text,
        Brush weak,
        Brush border,
        Brush accent)
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chrome.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(DockPanel));
        content.SetValue(DockPanel.LastChildFillProperty, true);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.Name = "Arrow";
        arrow.SetValue(TextBlock.TextProperty, "⌄");
        arrow.SetValue(TextBlock.ForegroundProperty, weak);
        arrow.SetValue(TextBlock.FontSizeProperty, 11.0);
        arrow.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 0, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(DockPanel.DockProperty, Dock.Right);
        content.AppendChild(arrow);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.Name = "ContentSite";
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateSelectorProperty,
            new TemplateBindingExtension(ItemsControl.ItemTemplateSelectorProperty));
        presenter.SetValue(
            ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        presenter.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        presenter.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Left);
        presenter.SetValue(
            UIElement.IsHitTestVisibleProperty,
            false);
        content.AppendChild(presenter);

        chrome.AppendChild(content);
        root.AppendChild(chrome);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "DropDownToggle";
        toggle.SetValue(Control.FocusableProperty, false);
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(
            Control.TemplateProperty,
            TransparentToggleTemplate());
        toggle.SetBinding(
            ToggleButton.IsCheckedProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
        root.AppendChild(toggle);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetBinding(
            Popup.IsOpenProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
            });
        popup.SetBinding(
            Popup.PlacementTargetProperty,
            new Binding
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
            });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, paper);
        popupBorder.SetValue(Border.BorderBrushProperty, border);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        popupBorder.SetValue(
            FrameworkElement.MarginProperty,
            new Thickness(0, 3, 0, 0));
        popupBorder.SetBinding(
            FrameworkElement.MinWidthProperty,
            new Binding(nameof(FrameworkElement.ActualWidth))
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
            });
        popupBorder.SetValue(
            UIElement.EffectProperty,
            new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.22
            });

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto);
        scroll.SetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(FrameworkElement.MaxHeightProperty, 260.0);

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroll.AppendChild(items);
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(ComboBox))
        {
            VisualTree = root
        };

        var hoverTrigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            hover,
            "Chrome"));
        template.Triggers.Add(hoverTrigger);

        var focusTrigger = new Trigger
        {
            Property = UIElement.IsKeyboardFocusWithinProperty,
            Value = true
        };
        focusTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            hover,
            "Chrome"));
        focusTrigger.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            accent,
            "Chrome"));
        template.Triggers.Add(focusTrigger);

        var openTrigger = new Trigger
        {
            Property = ComboBox.IsDropDownOpenProperty,
            Value = true
        };
        openTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            hover,
            "Chrome"));
        openTrigger.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            accent,
            "Chrome"));
        openTrigger.Setters.Add(new Setter(
            TextBlock.TextProperty,
            "⌃",
            "Arrow"));
        template.Triggers.Add(openTrigger);

        var disabledTrigger = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabledTrigger.Setters.Add(new Setter(
            UIElement.OpacityProperty,
            0.45));
        template.Triggers.Add(disabledTrigger);

        return template;
    }

    private static ControlTemplate TransparentToggleTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(ToggleButton))
        {
            VisualTree = border
        };
    }

    private static Style BuildItemStyle(
        Brush hover,
        Brush selected,
        Brush text,
        Brush accent)
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, text));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 28.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            BuildItemTemplate(hover, selected, accent)));
        return style;
    }

    private static ControlTemplate BuildItemTemplate(
        Brush hover,
        Brush selected,
        Brush accent)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        presenter.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Stretch);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ComboBoxItem))
        {
            VisualTree = border
        };

        var highlighted = new Trigger
        {
            Property = ComboBoxItem.IsHighlightedProperty,
            Value = true
        };
        highlighted.Setters.Add(new Setter(
            Border.BackgroundProperty,
            hover,
            "ItemChrome"));
        template.Triggers.Add(highlighted);

        var isSelected = new Trigger
        {
            Property = ComboBoxItem.IsSelectedProperty,
            Value = true
        };
        isSelected.Setters.Add(new Setter(
            Border.BackgroundProperty,
            selected,
            "ItemChrome"));
        isSelected.Setters.Add(new Setter(
            Control.ForegroundProperty,
            accent));
        isSelected.Setters.Add(new Setter(
            Control.FontWeightProperty,
            FontWeights.SemiBold));
        template.Triggers.Add(isSelected);

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        return template;
    }

    private static SolidColorBrush Brush(string value, string fallback)
    {
        try
        {
            return new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value)!);
        }
        catch
        {
            return new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(fallback)!);
        }
    }

    private static string AddAlpha(string value, byte alpha)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value)!;
            return $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch
        {
            return "#18B07A31";
        }
    }
}
