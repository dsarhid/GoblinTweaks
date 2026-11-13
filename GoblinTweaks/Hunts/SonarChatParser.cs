using System.Globalization;
using System.Text.RegularExpressions;

namespace GoblinTweaks.Hunts;

/// <summary>A hunt or FATE announced by Sonar in the chat. <see cref="Percent"/> is FATE progress (null for hunts).</summary>
public sealed record SonarChatMessage(HuntKind Kind, string Name, string WorldName, uint InstanceId, float? Percent, bool Dead);

/// <summary>
/// Reads Sonar's chat reports (format of SonarPlugin/Game/PayloadExtensions.cs):
/// <c>Rank A: Name </c> or <c>FATE: Name </c>, then the map link, then an optional instance icon,
/// <c> &lt;World&gt;</c> (with an optional cross-world icon inside), and <c> was just killed</c> or the FATE status.
/// Only the fixed words are English; the name comes in Sonar's database language and is kept as is.
/// </summary>
public static class SonarChatParser
{
    private static readonly Regex HuntRx = new(@"^\s*Rank (\S+): (.+?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FateRx = new(@"^\s*FATE: (.+?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WorldRx = new(@"<([^<>]+)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProgressRx = new(@"\((\d+(?:\.\d+)?)% with", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const char Instance1 = '', Instance9 = '';

    /// <param name="before">Text before the map link payload.</param>
    /// <param name="after">Text after the map link, concatenated.</param>
    public static SonarChatMessage? Parse(string before, string after)
    {
        HuntKind kind;
        string name;
        var hunt = HuntRx.Match(before);
        if (hunt.Success)
        {
            switch (hunt.Groups[1].Value)
            {
                case "A": kind = HuntKind.A; break;
                case "S" or "SS" or "SSMinion": kind = HuntKind.S; break;
                default: return null;
            }
            name = hunt.Groups[2].Value;
        }
        else
        {
            var fate = FateRx.Match(before);
            if (!fate.Success) return null;
            kind = HuntKind.Fate;
            name = fate.Groups[1].Value;
        }

        var worlds = WorldRx.Matches(after);
        if (worlds.Count == 0) return null;
        var world = StripIcons(worlds[^1].Groups[1].Value).Trim();
        name = name.Trim();
        if (world.Length == 0 || name.Length == 0) return null;

        uint instance = 0;
        foreach (var c in after)
        {
            if (c is >= Instance1 and <= Instance9) { instance = (uint)(c - Instance1 + 1); break; }
        }

        float? percent = null;
        if (kind == HuntKind.Fate)
        {
            var p = ProgressRx.Match(after);
            if (p.Success && float.TryParse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) percent = v;
            else if (after.Contains("(in Preparation)", StringComparison.Ordinal)) percent = 0f;
        }

        var dead = after.Contains(" was just killed", StringComparison.Ordinal);
        return new SonarChatMessage(kind, name, world, instance, percent, dead);
    }

    /// <summary>Drops the game's icon glyphs (private use area), e.g. the cross-world icon before the world name.</summary>
    private static string StripIcons(string s)
        => string.Concat(s.Where(c => c is < '' or > ''));
}
