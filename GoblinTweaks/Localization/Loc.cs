using System.Reflection;
using System.Text.Json;

namespace GoblinTweaks.Localization;

/// <summary>
/// Minimal localization: one flat JSON file per language (embedded), following Dalamud's UI language.
/// Missing keys fall back to English, then to the provided default or the key itself.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _fallback = [];
    private static Dictionary<string, string> _current = [];

    public static event Action? LanguageChanged;

    public static void Initialize()
    {
        _fallback = Load("en");
        SetLanguage(Svc.PluginInterface.UiLanguage);
        Svc.PluginInterface.LanguageChanged += SetLanguage;
    }

    public static void Dispose() => Svc.PluginInterface.LanguageChanged -= SetLanguage;

    public static string Get(string key, string? defaultText = null)
        => _current.TryGetValue(key, out var text) || _fallback.TryGetValue(key, out text)
            ? text
            : defaultText ?? key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    private static void SetLanguage(string languageCode)
    {
        _current = languageCode == "en" ? _fallback : Load(languageCode);
        LanguageChanged?.Invoke();
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
