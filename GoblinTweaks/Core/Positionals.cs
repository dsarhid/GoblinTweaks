using System.Globalization;
using System.Text.RegularExpressions;

namespace GoblinTweaks.Core;

/// <summary>
/// Tells whether an action that hits harder from one side of its target was made from that side.
/// </summary>
/// <remarks>
/// The game does not say "positional hit". Each hit of an action comes with the share of its potency
/// that is a bonus, in whole percent: the bonus of a combo plus the bonus of the positional, over the
/// potency of the hit. Nothing here is a list kept by hand: the potencies are the ones the game itself
/// writes in the description of the action, read at the level of the character, so they follow level
/// sync and patches on their own.
///
/// That share alone does not always settle it. An effect that raises the potency of an action shifts it,
/// and the share of a positional that was hit with such an effect can be the very share of one that was
/// missed without it. So the verdict has two witnesses, in <see cref="Judge"/>: the share, read against
/// what the description says it should be, and where the character stood, which the caller works out.
/// </remarks>
internal static partial class Positionals
{
    internal enum Side : byte { Rear, Flank }

    /// <summary>What the description of an action says about its positional. A potency it does not give is 0.</summary>
    /// <param name="Base">Potency with neither combo nor positional.</param>
    /// <param name="Positional">Potency from the right side, without a combo.</param>
    /// <param name="Combo">Potency in a combo, from the wrong side.</param>
    /// <param name="PositionalCombo">Potency in a combo, from the right side.</param>
    /// <param name="HasCombo">The action continues a combo, which is a bonus of its own, written or not.</param>
    internal sealed record Info(Side Side, int Base, int Positional, int Combo, int PositionalCombo, bool HasCombo);

    /// <summary>Where the character stood, as far as it could be told.</summary>
    internal enum Stance : byte
    {
        /// <summary>Not known.</summary>
        Unknown,

        /// <summary>On the wrong side.</summary>
        Wrong,

        /// <summary>On the right side; or the side does not matter (True North, an enemy with no sides).</summary>
        Right,
    }

    // A side is a quarter of the circle around the target: the front and the rear, 45 degrees to each side
    // of its facing and of its back; the flanks, what is left.
    private const float SideHalfAngle = 45f;

    [GeneratedRegex(@"potency of ([\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BasePattern();

    [GeneratedRegex(@"([\d,]+) when executed from a target's (?:rear|flank)", RegexOptions.IgnoreCase)]
    private static partial Regex PositionalPattern();

    [GeneratedRegex(@"^\s*Combo Potency: ([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ComboPattern();

    [GeneratedRegex(@"^\s*(?:Rear|Flank) Combo Potency: ([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PositionalComboPattern();

    /// <summary>
    /// Reads the positional of an action from its description in English, with its numbers already worked out
    /// for the level of the character. Null when the action has none.
    /// </summary>
    internal static Info? Read(string description, bool hasCombo)
    {
        Side side;
        if (description.Contains("target's rear", StringComparison.OrdinalIgnoreCase))       side = Side.Rear;
        else if (description.Contains("target's flank", StringComparison.OrdinalIgnoreCase)) side = Side.Flank;
        else return null;

        return new Info(side,
            Number(BasePattern().Match(description)),
            Number(PositionalPattern().Match(description)),
            Number(ComboPattern().Match(description)),
            Number(PositionalComboPattern().Match(description)),
            hasCombo);
    }

    private static int Number(Match match)
        => match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>The share of a hit that is a bonus, as the game gives it: whole percent, rounded down.</summary>
    private static int Share(int bonus, int total) => total <= 0 ? -1 : bonus * 100 / total;

    /// <summary>
    /// What the share of a hit says by itself, read against the description: true for a positional that was hit,
    /// false for one that was missed, null when the description does not account for it (an effect is raising
    /// the potency, or the action has a bonus the description does not put a number on).
    /// </summary>
    internal static bool? FromShare(Info info, int share)
    {
        // No bonus at all: not the positional's either.
        if (share <= 0) return false;

        // The positional is the only bonus this action can have. Effects that raise its potency make the
        // share smaller, never zero, so this holds with or without them.
        if (!info.HasCombo) return true;

        if (info.Base <= 0 || info.Positional <= info.Base) return null;

        var hit  = share == Share(info.Positional - info.Base, info.Positional);
        var miss = false;
        if (info.Combo > info.Base && info.PositionalCombo > info.Combo)
        {
            hit  |= share == Share(info.PositionalCombo - info.Base, info.PositionalCombo);
            miss  = share == Share(info.Combo - info.Base, info.Combo);
        }

        return hit == miss ? null : hit;
    }

    /// <summary>
    /// Where someone stands around a target, for an action that wants one side of it.
    /// </summary>
    /// <param name="degrees">Angle between where the target faces and where the character is: 0 in front of it, 180 behind.</param>
    internal static Stance StanceAt(Side side, float degrees)
    {
        var rearLine  = 180f - SideHalfAngle;
        var frontLine = SideHalfAngle;

        var right = side == Side.Rear ? degrees > rearLine : degrees > frontLine && degrees < rearLine;
        return right ? Stance.Right : Stance.Wrong;
    }

    /// <summary>
    /// The verdict, from the two witnesses; null when neither can tell, and nothing should be said.
    /// </summary>
    /// <remarks>
    /// A hit with no bonus at all missed its positional, wherever the character seemed to stand. A share that
    /// says "hit" is believed: the game gave the bonus, it is the one that measures, and it knows of things that
    /// make the side not matter. A share that says "missed" is believed unless the character stood on the right
    /// side, which is what an effect raising the potency looks like. With no word from the share, where the
    /// character stood decides, if it is known.
    /// </remarks>
    internal static bool? Judge(int share, bool? fromShare, Stance stance)
    {
        if (share <= 0) return false;
        if (fromShare == true) return true;
        if (stance == Stance.Right) return true;
        if (fromShare == false || stance == Stance.Wrong) return false;

        return null;
    }
}
