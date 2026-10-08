using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.UI;

/// <summary>
/// The font of the titles and section headings of every GoblinTweaks window, chosen in the plugin settings.
/// Native text only offers the game's own fonts; each one has a different size, so sizes go through <see cref="Size"/>.
/// </summary>
internal static class UiFont
{
    /// <summary>The fonts the game offers, with the name that is stored in the config.</summary>
    public static readonly IReadOnlyList<(string Key, FontType Type)> Choices =
    [
        ("TrumpGothic",  FontType.TrumpGothic),
        ("Axis",         FontType.Axis),
        ("Jupiter",      FontType.Jupiter),
    ];

    public const string Default = "Jupiter";

    public static string Key { get; private set; } = Default;

    public static FontType Heading { get; private set; } = FontType.Jupiter;

    /// <summary>Raised when another font is chosen, so the open windows can be built again.</summary>
    public static event Action? Changed;

    public static void Set(string key, bool notify = true)
    {
        var choice = Choices.FirstOrDefault(c => c.Key == key);
        if (choice.Key is null) choice = Choices[0];

        Key     = choice.Key;
        Heading = choice.Type;
        if (notify) Changed?.Invoke();
    }

    /// <summary>A heading size for the chosen font: the condensed font needs to be bigger to read as well as the others.</summary>
    public static uint Size(uint size) => Size(Heading, size);

    public static uint Size(FontType font, uint size) => font switch
    {
        FontType.TrumpGothic  => (uint)MathF.Round(size * 1.25f),
        FontType.JupiterLarge => (uint)MathF.Round(size * 0.7f),
        FontType.Jupiter      => (uint)MathF.Round(size * 0.85f),
        _                     => (uint)MathF.Round(size * 0.8f),
    };

    /// <summary>Extra space between the letters: the condensed font looks squeezed without it.</summary>
    public static uint Spacing => Heading == FontType.TrumpGothic ? 1u : 0u;
}
