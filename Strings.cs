using System.Globalization;
using System.Resources;
using System.Collections.Generic;

namespace PaperTodo;

public static class Strings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);

    private static readonly IReadOnlyDictionary<string, string[]> Supplemental =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LabsAdvancedShortcuts"] = ["高级快捷键", "Advanced shortcuts", "高度なショートカット", "고급 바로 가기"]
        };

    public static string Get(string key)
    {
        var resource = Manager.GetString(key, CultureInfo.CurrentUICulture);
        if (resource != null)
        {
            return resource;
        }

        if (!Supplemental.TryGetValue(key, out var values))
        {
            return key;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "en" => values[1],
            "ja" => values[2],
            "ko" => values[3],
            _ => values[0]
        };
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
