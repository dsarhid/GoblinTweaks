using System.Text.Json;

namespace GoblinTweaks.Glamour;

/// <summary>
/// A style theme ("pirate", "samurai"...). <c>Keywords</c> and <c>Avoid</c> are English words matched against
/// English item names. <c>Key</c> is also the localization key <c>Tweaks.GoblinGlamour.Theme.&lt;Key&gt;</c>.
/// </summary>
public sealed record Theme(string Key, string[] Keywords, string[] Avoid);

public static class Themes
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads <c>themes.json</c>: an array of <c>{ "key", "keywords": [...], "avoid": [...] }</c>.</summary>
    public static IReadOnlyList<Theme> Parse(string json)
        => (JsonSerializer.Deserialize<List<ThemeJson>>(json, JsonOptions) ?? [])
           .Where(theme => !string.IsNullOrWhiteSpace(theme.Key))
           .Select(theme => new Theme(theme.Key!, theme.Keywords ?? [], theme.Avoid ?? []))
           .ToList();

    /// <summary>1 when a keyword of the theme is a whole word (or words) of the name, 0 otherwise or when an avoid word is.</summary>
    public static float Match(Theme theme, string nameEn)
    {
        var words = Words(nameEn);
        if (theme.Avoid.Any(avoid => Contains(words, avoid)))
            return 0f;
        return theme.Keywords.Any(keyword => Contains(words, keyword)) ? 1f : 0f;
    }

    /// <summary>1 when any of the given words is a whole word of the name (case-insensitive), 0 otherwise.</summary>
    public static float MatchWords(IReadOnlyList<string> words, string nameEn)
    {
        if (words.Count == 0)
            return 0f;
        var nameWords = Words(nameEn);
        return words.Any(word => Contains(nameWords, word)) ? 1f : 0f;
    }

    /// <summary>The words of a text, lowercase, split on anything that is not a letter or digit ("Corsair's" → "corsair", "s").</summary>
    internal static string[] Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
                start = i;
            else if (!inWord && start >= 0)
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }
        return [.. words];
    }

    /// <summary>Whether the phrase (one or more words) appears as consecutive whole words.</summary>
    internal static bool Contains(string[] words, string phrase)
    {
        var parts = Words(phrase);
        if (parts.Length == 0)
            return false;
        for (var i = 0; i + parts.Length <= words.Length; i++)
        {
            var all = true;
            for (var j = 0; j < parts.Length && all; j++)
                all = words[i + j] == parts[j];
            if (all)
                return true;
        }
        return false;
    }

    private sealed class ThemeJson
    {
        public string?   Key      { get; set; }
        public string[]? Keywords { get; set; }
        public string[]? Avoid    { get; set; }
    }
}
