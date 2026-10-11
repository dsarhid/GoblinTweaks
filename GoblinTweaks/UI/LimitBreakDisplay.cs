using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GoblinTweaks.UI;

/// <summary>The Limit Break gauge of your party, as the game keeps it.</summary>
internal static unsafe class LimitBreak
{
    /// <summary>How many bars of the gauge are full: 0 while no Limit Break can be used. -1 when it cannot be read (PvP, no gauge).</summary>
    public static int Level()
    {
        var gauge = LimitBreakController.Instance();
        if (gauge == null || gauge->IsPvP || gauge->BarUnits == 0) return -1;

        return Math.Min(gauge->CurrentUnits / gauge->BarUnits, (int)gauge->BarCount);
    }
}
