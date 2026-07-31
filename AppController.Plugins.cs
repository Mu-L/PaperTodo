using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        root.Children.Add(new TextBlock
        {
            Text = Strings.Format(
                "PluginsCurrentProtocolFormat",
                PaperBodyPluginRegistry.SupportedPluginApiVersion),
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = Strings.Get("PluginsIntro"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            LineHeight = AppTypography.Scale(19),
            Margin = new Thickness(0, 5, 0, 0)
        });

        var toolbar = new Grid
        {
            Margin = new Thickness(0, 12, 0, 10)
        };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var path = new TextBlock
        {
            Text = _paperBodyPlugins.PluginRoot,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(11),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _paperBodyPlugins.PluginRoot
        };
        var openFolder = PluginPageButton(Strings.Get("PluginsOpenFolder"));
        openFolder.Margin = new Thickness(8, 0, 0, 0);
        openFolder.Click += (_, _) => OpenPluginFolder();
        var reload = PluginPageButton(Strings.Get("PluginsReload"));
        reload.Margin = new Thickness(8, 0, 0, 0);
        reload.Click += (_, _) => ReloadPaperBodyPlugins();
        Grid.SetColumn(path, 0);
        Grid.SetColumn(openFolder, 1);
        Grid.SetColumn(reload, 2);
        toolbar.Children.Add(path);
        toolbar.Children.Add(openFolder);
        toolbar.Children.Add(reload);
        root.Children.Add(toolbar);

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
            MinWidth = 76,
            Padding = new Thickness(11, 5, 11, 5),
            Style = BuildDialogButtonStyle(),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
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
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = descriptor.DisplayName,
            Foreground = TrayTextBrush,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(11.5),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 8, 0)
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = descriptor.Id,
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            Margin = new Thickness(0, 4, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = descriptor.SourcePath
        });

        var badge = new Border
        {
            Background = TrayHoverBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = Strings.Format(
                    "PluginsDescriptorVersionFormat",
                    PluginKindText(descriptor.Kind),
                    descriptor.Version,
                    descriptor.ApiVersion,
                    descriptor.StateVersion),
                Foreground = TrayTextBrush,
                FontSize = AppTypography.Scale(10.5),
                FontWeight = FontWeights.Medium
            }
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(badge, 1);
        content.Children.Add(text);
        content.Children.Add(badge);
        card.Child = content;
        return card;
    }

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
                    new TextBlock
                    {
                        Text = Path.GetFileName(issue.SourcePath),
                        Foreground = Theme.DangerBrush,
                        FontSize = AppTypography.Scale(12),
                        FontWeight = FontWeights.SemiBold
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
        _paperBodyPlugins.Dispose();
    }
}
