using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.SampleClock;

public sealed class SampleClockPlugin : IPaperBodyPlugin
{
    public string Id => "sample.clock.native";
    public string DisplayName => "原生时钟示例";
    public string Description => "用于验证 PaperTodo 原生 DLL 正文插件加载。";
    public Version Version => new(1, 0, 0);
    public int StateVersion => 1;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;

    public IPaperBodySession Create(PaperBodyContext context)
        => new ClockSession(context);

    private sealed class ClockSession : IPaperBodySession
    {
        private readonly PaperBodyContext _context;
        private readonly TextBlock _time;
        private readonly TextBlock _date;
        private readonly DispatcherTimer _timer;

        public ClockSession(PaperBodyContext context)
        {
            _context = context;
            _time = new TextBlock
            {
                FontSize = 42,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _date = new TextBlock
            {
                FontSize = 13,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(_time);
            panel.Children.Add(_date);
            View = new Grid
            {
                Background = Brushes.Transparent,
                Children = { panel }
            };
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (_, _) => Refresh();
            ApplyTheme(context.Theme);
            Refresh();
            _timer.Start();
        }

        public FrameworkElement View { get; }

        private void Refresh()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("HH:mm:ss");
            _date.Text = now.ToString("yyyy-MM-dd dddd");
            _context.SetCapsuleText(now.ToString("HH:mm"));
        }

        private void ApplyTheme(PaperBodyTheme theme)
        {
            _time.Foreground = Brush(theme.TextColor);
            _date.Foreground = Brush(theme.WeakTextColor);
            _time.FontFamily = new FontFamily(theme.FontFamily);
            _date.FontFamily = new FontFamily(theme.FontFamily);
        }

        private static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);
        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);
        public void OnVisibilityChanged(bool visible)
        {
            if (visible) _timer.Start(); else _timer.Stop();
        }
        public void Dispose() => _timer.Stop();
    }
}
