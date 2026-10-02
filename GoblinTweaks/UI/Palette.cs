using System.Numerics;

namespace GoblinTweaks.UI;

/// <summary>GoblinTweaks colors. Neutral tones follow the user's Dalamud theme; accents are fixed.</summary>
internal static class Palette
{
    public static readonly Vector4 Accent = new(0.47f, 0.72f, 0.27f, 1f);        // goblin green
    public static readonly Vector4 AccentDim = new(0.47f, 0.72f, 0.27f, 0.35f);
    public static readonly Vector4 Error = new(0.92f, 0.36f, 0.33f, 1f);
    public static readonly Vector4 ErrorDim = new(0.92f, 0.36f, 0.33f, 0.18f);
    public static readonly Vector4 CardBackground = new(1f, 1f, 1f, 0.035f);
    public static readonly Vector4 CardHovered = new(1f, 1f, 1f, 0.06f);
    public static readonly Vector4 SwitchOff = new(0.35f, 0.35f, 0.38f, 1f);
    public static readonly Vector4 Knob = new(0.97f, 0.97f, 0.97f, 1f);
    public static readonly Vector4 Muted = new(0.66f, 0.66f, 0.70f, 1f);
    public static readonly Vector4 BadgeText = new(0.08f, 0.08f, 0.08f, 1f);
}
