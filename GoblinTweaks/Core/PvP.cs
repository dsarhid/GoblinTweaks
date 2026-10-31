using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace GoblinTweaks.Core;

/// <summary>What the PvP tweaks need to know about the players around: who is an enemy, and who has Guard on.</summary>
internal static unsafe class PvP
{
    /// <summary>The Guard statuses: the usual one, and the one of the second Guard some modes have.</summary>
    public static readonly uint[] GuardStatuses = [3054, 3673];

    // The plain attack: whoever it can be used on is an enemy, by the game's own rules.
    private const uint Attack = 7;

    /// <summary>
    /// A player you can fight: the other teams in a match, or the one you duel on the Wolves' Den Pier. A duel
    /// opponent is not marked hostile, so the game is also asked whether you could attack them.
    /// </summary>
    public static bool IsEnemy(GameObject* obj)
    {
        if (obj == null || obj->ObjectKind != ObjectKind.Pc) return false;
        return ((BattleChara*)obj)->IsHostile || ActionManager.CanUseActionOnTarget(Attack, obj);
    }

    /// <summary>Whether the player has the Guard status on now.</summary>
    public static bool HasGuard(GameObject* obj)
    {
        if (obj == null || obj->ObjectKind != ObjectKind.Pc) return false;

        var statuses = ((BattleChara*)obj)->GetStatusManager();
        if (statuses == null) return false;

        foreach (ref readonly var status in statuses->Status)
        {
            if (GuardStatuses.Contains(status.StatusId))
                return true;
        }

        return false;
    }
}
