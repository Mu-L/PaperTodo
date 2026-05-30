using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaperTodo;

public static class Theme
{
    private static readonly Brush LightPaperBrush = FrozenBrush(Color.FromRgb(255, 248, 230));
    private static readonly Brush DarkPaperBrush = FrozenBrush(Color.FromRgb(32, 30, 27));

    private static readonly Brush LightPaperBorderBrush = FrozenBrush(Color.FromRgb(222, 202, 160));
    private static readonly Brush DarkPaperBorderBrush = FrozenBrush(Color.FromRgb(74, 67, 59));

    private static readonly Brush LightTextBrush = FrozenBrush(Color.FromRgb(54, 43, 31));
    private static readonly Brush DarkTextBrush = FrozenBrush(Color.FromRgb(230, 223, 211));

    private static readonly Brush LightWeakTextBrush = FrozenBrush(Color.FromRgb(132, 112, 86));
    private static readonly Brush DarkWeakTextBrush = FrozenBrush(Color.FromRgb(140, 131, 117));

    private static readonly Brush LightHoverBrush = FrozenBrush(Color.FromArgb(32, 120, 92, 48));
    private static readonly Brush DarkHoverBrush = FrozenBrush(Color.FromArgb(48, 230, 223, 211));

    private static readonly Brush LightActiveBrush = FrozenBrush(Color.FromRgb(140, 115, 80));
    private static readonly Brush DarkActiveBrush = FrozenBrush(Color.FromRgb(166, 140, 104));

    private static readonly Brush LightCodeBrush = FrozenBrush(Color.FromRgb(246, 235, 206));
    private static readonly Brush DarkCodeBrush = FrozenBrush(Color.FromRgb(44, 41, 37));

    private static readonly Brush LightQuoteBorderBrush = FrozenBrush(Color.FromRgb(210, 188, 144));
    private static readonly Brush DarkQuoteBorderBrush = FrozenBrush(Color.FromRgb(92, 84, 73));

    private static readonly Brush LightLinkBrush = FrozenBrush(Color.FromRgb(98, 88, 190));
    private static readonly Brush DarkLinkBrush = FrozenBrush(Color.FromRgb(135, 128, 218));

    public static bool IsDark
    {
        get
        {
            var theme = AppController.Current?.State?.Theme;
            if (theme == "system")
            {
                return IsSystemDark();
            }
            return theme == "dark";
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value is int i)
                {
                    return i == 0;
                }
            }
        }
        catch
        {
            // Fallback to light
        }
        return false;
    }

    public static Brush PaperBrush => IsDark ? DarkPaperBrush : LightPaperBrush;
    public static Brush PaperBorderBrush => IsDark ? DarkPaperBorderBrush : LightPaperBorderBrush;
    public static Brush TextBrush => IsDark ? DarkTextBrush : LightTextBrush;
    public static Brush WeakTextBrush => IsDark ? DarkWeakTextBrush : LightWeakTextBrush;
    public static Brush HoverBrush => IsDark ? DarkHoverBrush : LightHoverBrush;
    public static Brush ActiveBrush => IsDark ? DarkActiveBrush : LightActiveBrush;
    public static Brush CodeBrush => IsDark ? DarkCodeBrush : LightCodeBrush;
    public static Brush QuoteBorderBrush => IsDark ? DarkQuoteBorderBrush : LightQuoteBorderBrush;
    public static Brush LinkBrush => IsDark ? DarkLinkBrush : LightLinkBrush;

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
