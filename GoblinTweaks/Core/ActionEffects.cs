using System.Globalization;
using System.Text.RegularExpressions;

namespace GoblinTweaks.Core;

/// <summary>
/// What the description of an action says of the effects it gives: how much damage each one takes off,
/// and whether it answers every hit taken with an attack of its own.
/// </summary>
/// <remarks>
/// The game keeps no number for any of this where a plugin can read it: a status only says "damage taken is
/// reduced". The number is in the description of the action that gives the status, which names the status when
/// it is not called like the action ("Stem the Flow Effect: Reduces damage taken by 10%"). Nothing here is a
/// list kept by hand, so it follows patches on its own; an effect whose action words it some other way is
/// simply not counted.
/// </remarks>
internal static partial class ActionEffects
{
    /// <summary>The damage a reduction applies to.</summary>
    internal enum Kind : byte { Any, Physical, Magical }

    /// <param name="Dealt">True when it lowers the damage dealt by who has it; false, the damage they take.</param>
    internal readonly record struct Reduction(Kind Kind, bool Dealt, int Percent);

    /// <summary>What one status does, and the action whose description says so.</summary>
    /// <param name="Counter">It answers the hits taken with an attack (Vengeance, Damnation, Ice Spikes...).</param>
    internal sealed record Effect(uint ActionId, bool Counter, Reduction[] Reductions);

    [GeneratedRegex(@"^\s*(.+?) Effect:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedPattern();

    // One sentence that lowers something, and each thing it lowers in it:
    // "Lowers target's physical damage dealt by 5% and magic damage dealt by 10%".
    [GeneratedRegex(@"\b(?:reduc|lower)\w*[^.\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"(physical |magic(?:al)? )?(damage taken|damage dealt|vulnerability)[^.%\n]*?\bby (\d+)%", RegexOptions.IgnoreCase)]
    private static partial Regex PartPattern();

    // Sacred Soil: "party members will only suffer 90% of all damage inflicted".
    [GeneratedRegex(@"only suffer (\d+)% of all damage", RegexOptions.IgnoreCase)]
    private static partial Regex SufferPattern();

    [GeneratedRegex(@"\b(?:every|each) time you (?:suffer|take)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CounterPattern();

    /// <summary>
    /// Reads the effects an action gives from its description in English, with its numbers already worked out.
    /// Each one comes under the name of its status: that of the action, unless the description names another.
    /// </summary>
    internal static IEnumerable<(string Status, Effect Effect)> Read(uint actionId, string actionName, string description)
    {
        foreach (var line in description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (status, text) = (actionName, line);
            if (NamedPattern().Match(line) is { Success: true } named)
            {
                text = named.Groups[2].Value;

                // "Additional Effect:" is still about the action itself.
                var name = named.Groups[1].Value.Trim();
                if (!name.Equals("Additional", StringComparison.OrdinalIgnoreCase))
                    status = name;
            }

            var counter    = CounterPattern().IsMatch(text)
                             && (text.Contains("attack", StringComparison.OrdinalIgnoreCase) || text.Contains("counter", StringComparison.OrdinalIgnoreCase));
            var reductions = Reductions(text);
            if (counter || reductions.Length > 0)
                yield return (status, new Effect(actionId, counter, reductions));
        }
    }

    private static Reduction[] Reductions(string text)
    {
        List<Reduction>? found = null;
        foreach (Match sentence in SentencePattern().Matches(text))
        {
            foreach (Match part in PartPattern().Matches(sentence.Value))
            {
                var percent = Number(part.Groups[3]);
                if (percent is <= 0 or >= 100) continue;

                var kind = part.Groups[1].Value.Length == 0                                         ? Kind.Any
                         : part.Groups[1].Value.StartsWith("physical", StringComparison.OrdinalIgnoreCase) ? Kind.Physical
                         : Kind.Magical;
                var dealt = part.Groups[2].Value.EndsWith("dealt", StringComparison.OrdinalIgnoreCase);
                (found ??= []).Add(new Reduction(kind, dealt, percent));
            }
        }

        if (SufferPattern().Match(text) is { Success: true } suffer && Number(suffer.Groups[1]) is > 0 and < 100 and var left)
            (found ??= []).Add(new Reduction(Kind.Any, Dealt: false, 100 - left));

        return found?.ToArray() ?? [];
    }

    private static int Number(Group group)
        => int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>What is left of a hit after one effect, given what was left before it.</summary>
    /// <param name="dealt">Whether the effect is on who attacks (true) or on who is hit.</param>
    /// <param name="damageType">As the game numbers it: 1 physical, 2 magical, anything else neither.</param>
    internal static float Apply(Effect effect, bool dealt, int damageType, float left)
    {
        foreach (var reduction in effect.Reductions)
        {
            if (reduction.Dealt != dealt) continue;
            if (reduction.Kind == Kind.Physical && damageType != 1) continue;
            if (reduction.Kind == Kind.Magical && damageType != 2) continue;

            left *= 1f - reduction.Percent / 100f;
        }

        return left;
    }
}
