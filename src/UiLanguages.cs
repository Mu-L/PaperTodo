using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PaperTodo;

public static class UiLanguages
{
    public const string System = "system";
    public const string ChineseSimplified = "zh-CN";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";
    public const string Korean = "ko-KR";

#if PAPERTODO_DEFAULT_ENGLISH
    public const string Default = English;
#else
    public const string Default = System;
#endif

    // Capture the real process/system cultures before PaperTodo applies any preference.
    // Language switching is restart-based: resolve one effective culture for this process
    // and keep resource lookup independent from async/Dispatcher ExecutionContext culture flow.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly string StartupPreference = LoadPersistedPreferenceCore();

    // Intentionally fixed for the process lifetime; a settings change takes effect after restart.
    public static CultureInfo EffectiveCulture { get; } =
        ResolveCulture(StartupPreference, SystemCulture);

    public static CultureInfo EffectiveUiCulture { get; } =
        ResolveCulture(StartupPreference, SystemUiCulture);

    public static string Normalize(string? language)
    {
        return language is ChineseSimplified or English or Japanese or Korean
            ? language
            : System;
    }

    public static string LoadPersistedPreference() => StartupPreference;

    private static string LoadPersistedPreferenceCore()
    {
        foreach (var fileName in new[] { "data.json", "data.backup.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("uiLanguage", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return Normalize(value.GetString());
                }

                // A valid primary state without this newer property is authoritative:
                // use the build/system default rather than a stale backup preference.
                return Default;
            }
            catch
            {
                // Language preference is non-critical. Normal state loading still owns
                // corruption/recovery reporting; startup localization simply falls back.
            }
        }

        return Default;
    }

    public static bool TryGetCulture(string? language, out CultureInfo culture)
    {
        var normalized = Normalize(language);
        if (normalized == System)
        {
            culture = null!;
            return false;
        }

        culture = CultureInfo.GetCultureInfo(normalized);
        return true;
    }

    private static CultureInfo ResolveCulture(string? language, CultureInfo systemCulture)
    {
        var normalized = Normalize(language);
        return normalized == System
            ? systemCulture
            : CultureInfo.GetCultureInfo(normalized);
    }
}
