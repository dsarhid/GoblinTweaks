namespace GoblinTweaks.Glamour;

/// <summary>
/// Who can wear an item, from its EquipRaceCategory row: <c>Races[race - 1]</c> for the 8 races and the two
/// genders. Categories 0 and 1 mean everyone.
/// </summary>
public sealed record RaceRule(uint RaceCategory, bool[] Races, bool Male, bool Female);

public static class Wearability
{
    /// <summary>Whether a character of <paramref name="race"/> (1..8, as in the game) and gender can wear it.</summary>
    public static bool CanWear(RaceRule rule, int race, bool male)
    {
        if (rule.RaceCategory <= 1)
            return true;
        if (race < 1 || race > rule.Races.Length || !rule.Races[race - 1])
            return false;
        return male ? rule.Male : rule.Female;
    }
}
