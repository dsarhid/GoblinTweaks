using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Fits a word-wrapped text in the height of its node.</summary>
internal static class TextFit
{
    /// <summary>
    /// Writes <paramref name="text"/> in <paramref name="node"/> (word-wrapped, at its width) with the first font size and
    /// line spacing of <paramref name="sizes"/> that keeps it within <paramref name="maxHeight"/>. When even the last one
    /// does not fit, the text is cut at a word and ends with "…", so the caller should show all of it elsewhere (a
    /// tooltip). Returns false when it had to be cut.
    /// </summary>
    public static bool Fit(TextNode node, string text, float maxHeight, ReadOnlySpan<(uint Font, uint Spacing)> sizes)
    {
        if (node.Width <= 0f || sizes.IsEmpty)
        {
            node.String = text;
            return true;
        }

        foreach (var (font, spacing) in sizes)
        {
            node.FontSize    = font;
            node.LineSpacing = spacing;
            node.String      = text;
            if (node.GetTextDrawSize(false).Y <= maxHeight) return true;
        }

        var words = text.Split(' ');
        for (var n = words.Length - 1; n > 0; n--)
        {
            node.String = string.Join(' ', words[..n]).TrimEnd(',', '.', ';', ':') + "…";
            if (node.GetTextDrawSize(false).Y <= maxHeight) return false;
        }

        return false;
    }
}
