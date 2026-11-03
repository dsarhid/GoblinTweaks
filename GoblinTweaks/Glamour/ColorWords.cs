using System.Globalization;
using System.Text;

namespace GoblinTweaks.Glamour;

/// <summary>
/// Plain color words, both ways: a short English name for a color (so the AI knows what a piece looks like without
/// seeing it), and the colors a request names, in English or Spanish ("algo oscuro con detalles rojos").
/// </summary>
public static class ColorWords
{
    // Below this chroma a color reads as black, gray or white.
    private const float NeutralChroma = 0.04f;

    /// <summary>"black", "dark red", "light blue", "brown"...</summary>
    public static string Name(Rgb color)
    {
        var lightness = ColorMath.Lightness(color);
        if (ColorMath.Chroma(color) < NeutralChroma)
            return lightness switch
            {
                < 0.3f  => "black",
                < 0.5f  => "dark gray",
                < 0.75f => "gray",
                < 0.9f  => "light gray",
                _       => "white",
            };

        // OKLCh hues: red ~29°, orange ~55°, yellow ~110°, green ~142°, cyan ~195°, blue ~264°, purple ~300°.
        var hue = ColorMath.Hue(color);
        var name = hue switch
        {
            < 15f or >= 345f => "pink",
            < 45f            => "red",
            < 80f            => "orange",
            < 120f           => "yellow",
            < 175f           => "green",
            < 230f           => "teal",
            < 285f           => "blue",
            < 320f           => "purple",
            _                => "pink",
        };
        if (name is "orange" or "red" && lightness < 0.5f && ColorMath.Chroma(color) < 0.12f) return "brown";
        if (name == "pink" && lightness < 0.5f) name = "red";
        return lightness switch
        {
            < 0.42f => $"dark {name}",
            > 0.82f => $"light {name}",
            _       => name,
        };
    }

    private static readonly Dictionary<string, Rgb> Words = Build(
        (new Rgb(28, 28, 32),    ["dark", "black", "oscuro", "oscura", "negro", "negra", "sombrio", "sombria"]),
        (new Rgb(236, 236, 236), ["white", "blanco", "blanca"]),
        (new Rgb(128, 128, 128), ["gray", "grey", "gris"]),
        (new Rgb(165, 28, 28),   ["red", "crimson", "rojo", "roja", "carmesi", "escarlata"]),
        (new Rgb(40, 70, 170),   ["blue", "azul"]),
        (new Rgb(40, 125, 60),   ["green", "verde"]),
        (new Rgb(225, 195, 45),  ["yellow", "amarillo", "amarilla"]),
        (new Rgb(200, 160, 60),  ["gold", "golden", "dorado", "dorada", "oro"]),
        (new Rgb(190, 192, 200), ["silver", "plateado", "plateada", "plata"]),
        (new Rgb(110, 50, 150),  ["purple", "violet", "morado", "morada", "violeta", "lila"]),
        (new Rgb(220, 120, 160), ["pink", "rosa", "rosado", "rosada"]),
        (new Rgb(220, 120, 40),  ["orange", "naranja"]),
        (new Rgb(105, 68, 40),   ["brown", "marron", "cafe"]));

    /// <summary>The colors <paramref name="request"/> names, once each, in the order they appear.</summary>
    public static IReadOnlyList<Rgb> InRequest(string request)
    {
        var found = new List<Rgb>();
        foreach (var word in Plain(request).Split(c => !char.IsLetter(c)))
        {
            if (word.Length == 0) continue;
            if (Lookup(word) is { } color && !found.Contains(color)) found.Add(color);
        }
        return found;
    }

    /// <summary>
    /// The words of <paramref name="request"/> that name a color, lowercase, without accents or plurals, once each, in
    /// the order they appear ("Negro, azules" → "negro", "azul"): to show the player what was understood.
    /// </summary>
    public static IReadOnlyList<string> WordsInRequest(string request)
    {
        var found = new List<string>();
        foreach (var word in Plain(request).Split(c => !char.IsLetter(c)))
        {
            if (word.Length == 0) continue;
            if (Key(word) is { } key && !found.Contains(key)) found.Add(key);
        }
        return found;
    }

    private static Rgb? Lookup(string word) => Key(word) is { } key ? Words[key] : null;

    // "rojos" -> "rojo", "azules" -> "azul".
    private static string? Key(string word)
    {
        if (Words.ContainsKey(word)) return word;
        if (word.EndsWith('s') && Words.ContainsKey(word[..^1])) return word[..^1];
        if (word.EndsWith("es", StringComparison.Ordinal) && Words.ContainsKey(word[..^2])) return word[..^2];
        return null;
    }

    /// <summary>Lowercase, without accents ("marrón" -> "marron").</summary>
    internal static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(c);
        return plain.ToString();
    }

    private static Dictionary<string, Rgb> Build(params (Rgb Color, string[] Words)[] groups)
    {
        var words = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        foreach (var (color, list) in groups)
            foreach (var word in list)
                words[word] = color;
        return words;
    }
}

file static class SplitExtensions
{
    public static string[] Split(this string text, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !isSeparator(text[i])) continue;
            parts.Add(text[start..i]);
            start = i + 1;
        }
        return [.. parts];
    }
}
