using System.Text;
using System.Text.Json;

namespace GoblinTweaks.Glamour;

/// <summary>What kind of piece an item is, read from its English name ("Darkvein Cape" → Cape). One bit each.</summary>
[Flags]
public enum ShapeTag : uint
{
    None       = 0,
    Hat        = 1u << 0,
    Hood       = 1u << 1,
    Helmet     = 1u << 2,
    Mask       = 1u << 3,
    Circlet    = 1u << 4,
    Cape       = 1u << 5,
    Coat       = 1u << 6,
    Jacket     = 1u << 7,
    Vest       = 1u << 8,
    Robe       = 1u << 9,
    Dress      = 1u << 10,
    Kimono     = 1u << 11,
    Swimsuit   = 1u << 12,
    Shirt      = 1u << 13,
    HeavyArmor = 1u << 14,
    Pants      = 1u << 15,
    Shorts     = 1u << 16,
    Skirt      = 1u << 17,
    Tights     = 1u << 18,
    Boots      = 1u << 19,
    Sandals    = 1u << 20,
    Shoes      = 1u << 21,
    Gloves     = 1u << 22,
    Gauntlets  = 1u << 23,
    Bracers    = 1u << 24,
}

/// <summary>
/// A shape with the whole words of item names that mean it (<c>Names</c>, English) and the words a player may write
/// for it (<c>Words</c>, English and Spanish, lowercase, without accents; phrases allowed: "traje de bano").
/// </summary>
public sealed record ShapeWords(ShapeTag Shape, string[] Names, string[] Words);

/// <summary>Two shapes that look wrong together (heavy armor and swimwear), and how much that costs an outfit.</summary>
public sealed record ShapeClash(ShapeTag A, ShapeTag B, float Penalty);

public sealed record ShapeTable(IReadOnlyList<ShapeWords> Shapes, IReadOnlyList<ShapeClash> Clashes);

/// <summary>
/// Shapes of items, from their names: no pictures, no model. Names say a lot ("Cape", "Robe", "Sabatons"); an item
/// whose name says nothing has no shape, and is never penalized for it.
/// </summary>
public static class ShapeTags
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads <c>shapes.json</c>: <c>{ "shapes": [{ "shape", "names", "words" }], "clashes": [{ "a", "b", "penalty" }] }</c>.</summary>
    public static ShapeTable Parse(string json)
    {
        var file = JsonSerializer.Deserialize<FileJson>(json, JsonOptions) ?? new FileJson();
        var shapes = (file.Shapes ?? [])
            .Where(entry => Enum.TryParse<ShapeTag>(entry.Shape, out var tag) && tag != ShapeTag.None)
            .Select(entry => new ShapeWords(Enum.Parse<ShapeTag>(entry.Shape!), entry.Names ?? [],
                                            [.. (entry.Words ?? []).Select(ColorWords.Plain)]))
            .ToList();
        var clashes = (file.Clashes ?? [])
            .Where(entry => Enum.TryParse<ShapeTag>(entry.A, out _) && Enum.TryParse<ShapeTag>(entry.B, out _))
            .Select(entry => new ShapeClash(Enum.Parse<ShapeTag>(entry.A!), Enum.Parse<ShapeTag>(entry.B!), Math.Clamp(entry.Penalty, 0f, 1f)))
            .ToList();
        return new ShapeTable(shapes, clashes);
    }

    /// <summary>The shapes an English item name says, by whole words ("Escape Boots" is boots, not a cape).</summary>
    public static ShapeTag FromName(string nameEn, ShapeTable table)
    {
        var words = Themes.Words(nameEn);
        var shapes = ShapeTag.None;
        foreach (var entry in table.Shapes)
            if (entry.Names.Any(name => Themes.Contains(words, name)))
                shapes |= entry.Shape;
        return shapes;
    }

    /// <summary>
    /// The shapes a player's request asks for, in English or Spanish, any case, with or without accents, singular or
    /// plural ("capas" → Cape, "traje de baño" → Swimsuit).
    /// </summary>
    public static ShapeTag FromRequest(string request, ShapeTable table)
    {
        var words = Themes.Words(ColorWords.Plain(request));
        var singular = words.Select(Singular).ToArray();
        var shapes = ShapeTag.None;
        foreach (var entry in table.Shapes)
            if (entry.Words.Any(phrase => Themes.Contains(words, phrase) || Themes.Contains(singular, phrase)))
                shapes |= entry.Shape;
        return shapes;
    }

    /// <summary>The highest penalty of a clash between the shapes of two pieces, in either order (0 = none).</summary>
    public static float Clash(ShapeTag a, ShapeTag b, IReadOnlyList<ShapeClash> clashes)
    {
        if (a == ShapeTag.None || b == ShapeTag.None)
            return 0f;
        var penalty = 0f;
        foreach (var clash in clashes)
            if (((a & clash.A) != 0 && (b & clash.B) != 0) || ((a & clash.B) != 0 && (b & clash.A) != 0))
                penalty = Math.Max(penalty, clash.Penalty);
        return penalty;
    }

    /// <summary>"cape, heavy armor": the shapes in plain English, for the AI; "-" for none.</summary>
    public static string Describe(ShapeTag shapes)
    {
        if (shapes == ShapeTag.None)
            return "-";
        var names = new List<string>();
        foreach (var tag in Enum.GetValues<ShapeTag>())
            if (tag != ShapeTag.None && (shapes & tag) != 0)
                names.Add(Spaced(tag.ToString()));
        return string.Join(", ", names);
    }

    // "capas" -> "capa", "botas" -> "bota", "pantalones" -> "pantalon": both are tried, so one of them hits.
    private static string Singular(string word)
        => word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && !word.EndsWith("ses", StringComparison.Ordinal) ? word[..^2]
         : word.Length > 3 && word.EndsWith('s') ? word[..^1]
         : word;

    // "HeavyArmor" -> "heavy armor".
    private static string Spaced(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) text.Append(' ');
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    private sealed class FileJson
    {
        public List<ShapeJson>? Shapes  { get; set; }
        public List<ClashJson>? Clashes { get; set; }
    }

    private sealed class ShapeJson
    {
        public string?   Shape { get; set; }
        public string[]? Names { get; set; }
        public string[]? Words { get; set; }
    }

    private sealed class ClashJson
    {
        public string? A       { get; set; }
        public string? B       { get; set; }
        public float   Penalty { get; set; }
    }
}
