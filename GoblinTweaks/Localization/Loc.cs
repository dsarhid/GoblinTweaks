using System.Reflection;
using System.Text.Json;

namespace GoblinTweaks.Localization;

/// <summary>
/// Plugin localization. Every language file is loaded at startup (they are tiny), so texts in all
/// languages are available at any time, e.g. for searching. Missing keys fall back to English.
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "en";

    /// <summary>Languages offered in the settings window. Add a JSON file and an entry here to add one.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"),
        ("es", "Español"),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = [];
    private static Dictionary<string, string> _current = [];

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static event Action? LanguageChanged;

    public static void Initialize(string language)
    {
        foreach (var (code, _) in Languages)
            Texts[code] = Load(code);

        SetLanguage(language);
    }

    public static void SetLanguage(string language)
    {
        if (!Texts.TryGetValue(language, out var texts))
        {
            language = DefaultLanguage;
            texts = Texts.GetValueOrDefault(DefaultLanguage) ?? [];
        }

        CurrentLanguage = language;
        _current = texts;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key, string? defaultText = null)
    {
        if (_current.TryGetValue(key, out var text))
            return text;

        if (Texts.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out text))
            return text;

        return defaultText ?? key;
    }

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>The text of <paramref name="key"/> in every language that defines it.</summary>
    public static IEnumerable<string> GetInAllLanguages(string key)
    {
        foreach (var texts in Texts.Values)
        {
            if (texts.TryGetValue(key, out var text))
                yield return text;
        }
    }

    private static Dictionary<string, string> Load(string languageCode)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GoblinTweaks.Localization.{languageCode}.json");
            return stream == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not load language {lang}", languageCode);
            return [];
        }
    }
}
