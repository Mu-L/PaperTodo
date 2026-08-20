using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly PaperBodyPluginRegistry _paperBodyPlugins;

    internal PaperBodyPluginRegistry PaperBodyPlugins => _paperBodyPlugins;

    private UIElement BuildPluginsSettingsPage()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(2, 4, 4, 0)
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Strings.Get("PluginsPageTitle"),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var openFolder = PluginPageButton(Strings.Get("PluginsOpenFolder"));
        openFolder.Margin = new Thickness(8, 0, 0, 0);
        openFolder.ToolTip = _paperBodyPlugins.PluginRoot;
        openFolder.Click += (_, _) => OpenPluginFolder();
        var reload = PluginPageButton(Strings.Get("PluginsReload"));
        reload.Margin = new Thickness(8, 0, 0, 0);
        reload.Click += (_, _) => ReloadPaperBodyPlugins();
        Grid.SetColumn(title, 0);
        Grid.SetColumn(openFolder, 1);
        Grid.SetColumn(reload, 2);
        header.Children.Add(title);
        header.Children.Add(openFolder);
        header.Children.Add(reload);
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = Strings.Get("PluginsIntro"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11.5),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });


        var descriptors = _paperBodyPlugins.Descriptors;
        root.Children.Add(SettingsSectionLabel(
            Strings.Format("PluginsLoadedCountFormat", descriptors.Count)));
        foreach (var descriptor in descriptors)
        {
            root.Children.Add(BuildPluginDescriptorCard(descriptor));
        }

        if (_paperBodyPlugins.Issues.Count > 0)
        {
            root.Children.Add(SettingsSectionLabel(Strings.Get("PluginsLoadProblems")));
            foreach (var issue in _paperBodyPlugins.Issues)
            {
                root.Children.Add(BuildPluginIssueCard(issue));
            }
        }

        return root;
    }

    private Button PluginPageButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 72,
            Padding = new Thickness(10, 4, 10, 4),
            Style = BuildDialogButtonStyle(),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11.5),
            Focusable = false
        };
    }

    private UIElement BuildPluginDescriptorCard(PaperBodyPluginDescriptor descriptor)
    {
        var card = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Padding = new Thickness(11, 8, 11, 9),
            Margin = new Thickness(0, 5, 0, 3)
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var settings = descriptor.Manifest?.Settings ?? [];
        PaperBodyPluginDataReadIssue? dataIssue = null;
        if (descriptor.Kind != PaperBodyPluginKind.BuiltIn &&
            _paperBodyPlugins.DataStore.TryGetReadIssue(
                descriptor.Id,
                out var detectedDataIssue))
        {
            dataIssue = detectedDataIssue;
        }
        if (settings.Length > 0)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Min(255, SettingsWindowWidth() * 0.34))
            });
        }

        var text = new StackPanel();
        var status = PluginStatusFor(descriptor, dataIssue != null);
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var statusDot = CreatePluginStatusDot(status);
        _pluginStatusRefreshers[descriptor.Id] = () =>
            ApplyPluginStatusDot(
                statusDot,
                PluginStatusFor(descriptor, dataIssue != null));
        Grid.SetColumn(statusDot, 0);
        titleRow.Children.Add(statusDot);

        var titleFlow = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Strings.Format(
                "PluginsProtocolTooltipFormat",
                descriptor.ApiVersion)
        };
        titleFlow.Inlines.Add(new System.Windows.Documents.Run(descriptor.DisplayName)
        {
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold
        });
        titleFlow.Inlines.Add(new System.Windows.Documents.Run(
            $" — {PluginKindText(descriptor.Kind)} · v{PluginVersionText(descriptor.Version)} · s{descriptor.ApiVersion}")
        {
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            FontWeight = FontWeights.Medium
        });
        Grid.SetColumn(titleFlow, 1);
        titleRow.Children.Add(titleFlow);
        text.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(11.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, settings.Length > 0 ? 14 : 0, 0)
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = descriptor.Id,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            Margin = new Thickness(0, 4, settings.Length > 0 ? 14 : 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = descriptor.SourcePath
        });
        if (dataIssue != null)
        {
            text.Children.Add(new TextBlock
            {
                Text = Strings.Get(
                    dataIssue.UsingEmptyState
                        ? "PluginsDataRecoveryPending"
                        : "PluginsDataRecoveryActive"),
                Foreground = Theme.DangerBrush,
                FontSize = AppTypography.Scale(10.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, settings.Length > 0 ? 14 : 0, 0),
                ToolTip = string.IsNullOrWhiteSpace(dataIssue.Details)
                    ? dataIssue.ActivePath
                    : $"{dataIssue.ActivePath}{Environment.NewLine}{dataIssue.Details}"
            });
        }
        Grid.SetColumn(text, 0);
        content.Children.Add(text);

        if (settings.Length > 0)
        {
            var settingsPanel = BuildPluginSettingsPanel(descriptor, settings);
            Grid.SetColumn(settingsPanel, 1);
            content.Children.Add(settingsPanel);
        }

        card.Child = content;
        return card;
    }

    private FrameworkElement BuildPluginSettingsPanel(
        PaperBodyPluginDescriptor descriptor,
        IReadOnlyList<PaperBodyPluginSettingManifest> settings)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0)
        };
        var quick = settings.Where(item => item.Quick).Take(3).ToArray();
        var remaining = settings.Where(item => !item.Quick).ToArray();
        if (remaining.Length == 0)
        {
            foreach (var setting in quick)
            {
                root.Children.Add(BuildPluginSettingControl(descriptor, setting));
            }
            return root;
        }

        var more = new StackPanel
        {
            Visibility = Visibility.Collapsed
        };
        foreach (var setting in remaining)
        {
            more.Children.Add(BuildPluginSettingControl(descriptor, setting));
        }

        var toggle = PluginPageButton(Strings.Get("PluginsMoreSettings"));
        toggle.MinWidth = 0;
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) =>
        {
            var expand = more.Visibility != Visibility.Visible;
            more.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = Strings.Get(
                expand ? "PluginsLessSettings" : "PluginsMoreSettings");
        };

        if (quick.Length == 0)
        {
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(toggle);
        }
        else
        {
            foreach (var setting in quick[..^1])
            {
                root.Children.Add(BuildPluginSettingControl(descriptor, setting));
            }

            var tail = new Grid();
            tail.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            tail.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            var finalQuick = BuildPluginSettingControl(descriptor, quick[^1]);
            finalQuick.Margin = new Thickness(0, 4, 8, 0);
            toggle.Margin = new Thickness(8, 4, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(finalQuick, 0);
            Grid.SetColumn(toggle, 1);
            tail.Children.Add(finalQuick);
            tail.Children.Add(toggle);
            root.Children.Add(tail);
        }

        root.Children.Add(more);
        return root;
    }

    private FrameworkElement BuildPluginSettingControl(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        if (setting.Type == "boolean")
        {
            var value = _paperBodyPlugins.DataStore
                .GetSettingValue(descriptor, setting)
                .GetBoolean();
            var toggle = SettingsToggle(
                setting.Name,
                value,
                () => CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(
                        !_paperBodyPlugins.DataStore
                            .GetSettingValue(descriptor, setting)
                            .GetBoolean())));
            toggle.Margin = new Thickness(0, 4, 0, 0);
            toggle.ToolTip = PluginSettingToolTip(setting);
            return toggle;
        }

        var row = new Grid
        {
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = PluginSettingToolTip(setting)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = setting.Name,
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(11.5),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        });

        FrameworkElement editor = setting.Type switch
        {
            "string" => BuildPluginStringSetting(descriptor, setting),
            "number" => BuildPluginNumberSetting(descriptor, setting),
            "select" => BuildPluginSelectSetting(descriptor, setting),
            _ => new TextBlock()
        };
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private FrameworkElement BuildPluginStringSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var editor = new TextBox
        {
            Text = current.ValueKind == JsonValueKind.String
                ? current.GetString() ?? ""
                : "",
            Width = 125,
            MinHeight = 27,
            MaxLength = setting.MaxLength ?? 0,
            Style = BuildSettingsTextBoxStyle(),
            FontSize = AppTypography.Scale(11.5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(setting.Placeholder))
        {
            editor.ToolTip = setting.Placeholder;
        }
        editor.TextChanged += (_, _) => CommitPluginSetting(
            descriptor,
            setting,
            JsonSerializer.SerializeToElement(editor.Text));
        return editor;
    }

    private FrameworkElement BuildPluginNumberSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        var editor = new TextBox
        {
            Text = current.TryGetDouble(out var number)
                ? number.ToString("G", CultureInfo.CurrentCulture)
                : "0",
            Width = string.IsNullOrWhiteSpace(setting.Suffix) ? 112 : 86,
            MinHeight = 27,
            Style = BuildSettingsTextBoxStyle(),
            FontSize = AppTypography.Scale(11.5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        editor.TextChanged += (_, _) =>
        {
            if (TryParsePluginNumber(editor.Text, out var parsed))
            {
                CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(parsed));
            }
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (!TryParsePluginNumber(editor.Text, out var parsed))
            {
                var stored = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
                editor.Text = stored.GetDouble().ToString("G", CultureInfo.CurrentCulture);
                return;
            }

            var normalized = CommitPluginSetting(
                descriptor,
                setting,
                JsonSerializer.SerializeToElement(parsed));
            editor.Text = normalized.GetDouble().ToString("G", CultureInfo.CurrentCulture);
        };
        panel.Children.Add(editor);
        if (!string.IsNullOrWhiteSpace(setting.Suffix))
        {
            panel.Children.Add(new TextBlock
            {
                Text = setting.Suffix,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(10.5),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return panel;
    }

    private FrameworkElement BuildPluginSelectSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        var current = _paperBodyPlugins.DataStore.GetSettingValue(descriptor, setting);
        var selected = current.ValueKind == JsonValueKind.String
            ? current.GetString() ?? ""
            : "";

        string SelectedName() =>
            setting.Options.FirstOrDefault(option =>
                string.Equals(option.Value, selected, StringComparison.Ordinal))?.Name
            ?? selected;

        var valueText = new TextBlock
        {
            Text = SelectedName(),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(11.5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 1 L 4 5 L 8 1"),
            Stroke = TrayWeakTextBrush,
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 8,
            Height = 6,
            Margin = new Thickness(10, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            SnapsToDevicePixels = true
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(valueText);
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);

        var editor = new Border
        {
            Width = 132,
            MinHeight = 28,
            Padding = new Thickness(9, 2, 8, 2),
            CornerRadius = new CornerRadius(6),
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = true,
            SnapsToDevicePixels = true,
            Child = content
        };

        // Keep the settings selector on the existing ContextMenu lifecycle. A standalone Popup
        // previously caused the settings window to terminate when its first select was opened.
        var menu = CreateTrayMenu();
        menu.MinWidth = 132;
        menu.MaxWidth = 320;
        menu.MaxHeight = 236;
        menu.PlacementTarget = editor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 4;
        var optionRows = new List<(string Value, string Name, MenuItem Row)>();

        void RefreshOptionRows()
        {
            foreach (var (value, name, row) in optionRows)
            {
                var active = string.Equals(value, selected, StringComparison.Ordinal);
                row.Header = active ? $"✓  {name}" : $"   {name}";
                row.Foreground = active ? Theme.ActiveBrush : TrayTextBrush;
                row.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                row.Background = active
                    ? Theme.Tint((byte)(Theme.IsDark ? 45 : 28))
                    : Brushes.Transparent;
            }
        }

        foreach (var option in setting.Options)
        {
            var optionRow = new MenuItem
            {
                Header = option.Name,
                Style = SharedTrayMenuItemStyle,
                ToolTip = option.Name
            };
            optionRow.Click += (_, _) =>
            {
                var normalized = CommitPluginSetting(
                    descriptor,
                    setting,
                    JsonSerializer.SerializeToElement(option.Value));
                selected = normalized.ValueKind == JsonValueKind.String
                    ? normalized.GetString() ?? option.Value
                    : option.Value;
                valueText.Text = SelectedName();
                RefreshOptionRows();
                menu.IsOpen = false;
                editor.Focus();
            };
            optionRows.Add((option.Value, option.Name, optionRow));
            menu.Items.Add(optionRow);
        }

        RefreshOptionRows();

        void SetOpenVisual(bool open)
        {
            arrow.RenderTransform = open
                ? new RotateTransform(180)
                : Transform.Identity;
            editor.Background = open ? TrayHoverBrush : Brushes.Transparent;
            editor.BorderBrush = open ? Theme.ActiveBrush : TrayBorderBrush;
        }

        void OpenPopup()
        {
            RefreshOptionRows();
            SetOpenVisual(true);
            menu.PlacementTarget = editor;
            menu.IsOpen = true;
        }

        void SelectByOffset(int offset)
        {
            if (setting.Options.Length == 0)
            {
                return;
            }
            var index = Array.FindIndex(
                setting.Options,
                option => string.Equals(
                    option.Value,
                    selected,
                    StringComparison.Ordinal));
            index = index < 0 ? 0 : index;
            index = (index + offset + setting.Options.Length) %
                setting.Options.Length;
            var option = setting.Options[index];
            var normalized = CommitPluginSetting(
                descriptor,
                setting,
                JsonSerializer.SerializeToElement(option.Value));
            selected = normalized.ValueKind == JsonValueKind.String
                ? normalized.GetString() ?? option.Value
                : option.Value;
            valueText.Text = SelectedName();
            RefreshOptionRows();
        }

        editor.MouseEnter += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                editor.Background = TrayHoverBrush;
            }
        };
        editor.MouseLeave += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                editor.Background = Brushes.Transparent;
            }
        };
        editor.MouseLeftButtonDown += (_, e) =>
        {
            if (menu.IsOpen)
            {
                menu.IsOpen = false;
            }
            else
            {
                OpenPopup();
            }
            e.Handled = true;
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down)
            {
                SelectByOffset(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                SelectByOffset(-1);
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            {
                if (menu.IsOpen) menu.IsOpen = false;
                else OpenPopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && menu.IsOpen)
            {
                menu.IsOpen = false;
                e.Handled = true;
            }
        };
        menu.Closed += (_, _) => SetOpenVisual(false);
        return editor;
    }

    private JsonElement CommitPluginSetting(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting,
        JsonElement value)
    {
        var normalized = _paperBodyPlugins.DataStore.SetSettingValue(
            descriptor,
            setting,
            value);
        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);
        foreach (var window in _windows.Values.ToList())
        {
            window.NotifyPaperBodyPluginSettingsChanged(descriptor.Id, settingsJson);
        }
        return normalized;
    }

    private static bool TryParsePluginNumber(string text, out double value)
    {
        var parsed = double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value) ||
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        return parsed && double.IsFinite(value);
    }

    private static object? PluginSettingToolTip(PaperBodyPluginSettingManifest setting) =>
        string.IsNullOrWhiteSpace(setting.Description)
            ? null
            : setting.Description;

    private UIElement BuildPluginIssueCard(PaperBodyPluginLoadIssue issue)
    {
        var label = issue.RestartRequired
            ? $"{issue.Message} · {Strings.Get("PluginsRestartRequired")}"
            : issue.Message;
        return new Border
        {
            BorderBrush = Theme.Danger(72),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Danger((byte)(Theme.IsDark ? 20 : 12)),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 5, 0, 3),
            Child = new StackPanel
            {
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreatePluginStatusDot(PluginPageStatus.Issue),
                            new TextBlock
                            {
                                Text = Path.GetFileName(issue.SourcePath),
                                Foreground = Theme.DangerBrush,
                                FontSize = AppTypography.Scale(12),
                                FontWeight = FontWeights.SemiBold
                            }
                        }
                    },
                    new TextBlock
                    {
                        Text = label,
                        Foreground = TrayWeakTextBrush,
                        FontSize = AppTypography.Scale(11.5),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0),
                        ToolTip = issue.SourcePath
                    }
                }
            }
        };
    }

    private static string PluginKindText(PaperBodyPluginKind kind) => kind switch
    {
        PaperBodyPluginKind.Native => Strings.Get("PluginsNative"),
        PaperBodyPluginKind.Web => Strings.Get("PluginsWeb"),
        _ => Strings.Get("PluginsBuiltIn")
    };

    private static string PluginVersionText(Version version)
    {
        if (version.Revision > 0)
        {
            return version.ToString(4);
        }
        if (version.Build > 0)
        {
            return version.ToString(3);
        }
        return version.ToString(2);
    }

    private void OpenPluginFolder()
    {
        try
        {
            Directory.CreateDirectory(_paperBodyPlugins.PluginRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paperBodyPlugins.PluginRoot,
                UseShellExecute = true
            });
        }
        catch
        {
            // Settings remains usable if Explorer cannot open the directory.
        }
    }

    private void ReloadPaperBodyPlugins()
    {
        _paperBodyPlugins.Reload();
        var changedProviderIds = _paperBodyPlugins.LastChangedProviderIds;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshPaperBodyProviderAvailability(changedProviderIds);
        }
        RefreshTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void DisposePaperBodyPlugins()
    {
        DisposePaperPluginHostRuntime();
        _paperBodyPlugins.Dispose();
    }
}
