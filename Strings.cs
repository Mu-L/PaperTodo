using System.Globalization;
using System.Resources;

namespace PaperTodo;

public static class Strings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
    {
        return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
