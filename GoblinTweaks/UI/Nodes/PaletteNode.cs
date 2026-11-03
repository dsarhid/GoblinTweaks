using System.Numerics;
using GoblinTweaks.Glamour;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// A row of color squares, each with its name and value as a tooltip: the character's own colors. The squares are
/// made once; showing other colors only recolors them and hides the ones not needed.
/// </summary>
internal sealed class PaletteNode : ResNode
{
    public const float Swatch = 22f;
    private const float Gap = 4f;

    private readonly List<ColorImageNode> _swatches = [];

    public PaletteNode(int capacity)
    {
        for (var i = 0; i < capacity; i++)
        {
            var swatch = new ColorImageNode
            {
                Position  = new Vector2(i * (Swatch + Gap), 0f),
                Size      = new Vector2(Swatch, Swatch),
                IsVisible = false,
            };
            swatch.AttachNode(this);
            _swatches.Add(swatch);
        }
        Size = new Vector2(capacity * (Swatch + Gap) - Gap, Swatch);
    }

    /// <summary>Shows these colors (at most as many as the capacity) and hides the remaining squares.</summary>
    public void SetColors(IReadOnlyList<(string Name, Rgb Color)> colors)
    {
        for (var i = 0; i < _swatches.Count; i++)
        {
            var swatch = _swatches[i];
            if (i >= colors.Count) { swatch.IsVisible = false; continue; }

            var (name, color) = colors[i];
            swatch.Color       = ToVector(color);
            swatch.TextTooltip = $"{name}  #{color.ToHex()}";
            swatch.IsVisible   = true;
        }
    }

    public static Vector4 ToVector(Rgb color) => new(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
}
