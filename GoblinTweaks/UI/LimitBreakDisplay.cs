using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GoblinTweaks.UI;

/// <summary>The Limit Break gauge, as the game keeps it: your party's, or in PvP your own.</summary>
internal static unsafe class LimitBreak
{
    /// <summary>How many bars of the gauge are full: 0 while no Limit Break can be used. -1 when it cannot be read (no gauge).</summary>
    public static int Level()
    {
        var gauge = LimitBreakController.Instance();
        if (gauge == null || gauge->BarUnits == 0) return -1;

        return Math.Min(gauge->CurrentUnits / gauge->BarUnits, (int)gauge->BarCount);
    }

    /// <summary>Whether the gauge is the PvP one: a single Limit Break of your own, not your party's.</summary>
    public static bool IsPvP()
    {
        var gauge = LimitBreakController.Instance();
        return gauge != null && gauge->IsPvP;
    }

    /// <summary>The numbers of the gauge, for the log.</summary>
    public static string Describe()
    {
        var gauge = LimitBreakController.Instance();
        return gauge == null ? "no gauge" : $"{gauge->CurrentUnits}/{gauge->BarUnits} units, {gauge->BarCount} bars, PvP {gauge->IsPvP}";
    }
}
