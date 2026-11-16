namespace GoblinTweaks.Crafting;

/// <summary>Coordinates of the gathering areas and the choice of gearset for the "By source" map and teleport buttons.</summary>
public static class TravelMath
{
    /// <summary>
    /// A world position (yalms) to a map coordinate (1.0–42.0 at size 100), as the game shows it: the map texture is
    /// 2048 pixels, <paramref name="sizeFactor"/> / 100 pixels a yalm, centred on -<paramref name="offset"/>.
    /// </summary>
    public static float WorldToMapCoord(float world, ushort sizeFactor, short offset)
    {
        var scale = sizeFactor / 100f;
        return 41f / scale * (((world + offset) * scale + 1024f) / 2048f) + 1f;
    }

    /// <summary>A pixel of the 2048-pixel map texture (FishingSpot, SpearfishingNotebook) to a world position (yalms).</summary>
    public static float PixelToWorld(float pixel, ushort sizeFactor, short offset)
        => (pixel - 1024f) / (sizeFactor / 100f) - offset;

    /// <summary>The gearset of <paramref name="classJob"/> with the highest item level (the lowest number on a tie); null if there is none.</summary>
    public static int? PickGearset(IEnumerable<(int Id, uint ClassJob, int ItemLevel)> sets, uint classJob)
    {
        int? best = null;
        var bestLevel = int.MinValue;
        foreach (var (id, job, level) in sets)
        {
            if (job != classJob) continue;
            if (level > bestLevel || (level == bestLevel && id < best))
            {
                best      = id;
                bestLevel = level;
            }
        }
        return best;
    }
}

public enum JobSwitchStep { Wait, Switch, Cancel }

/// <summary>
/// The job change that waits for the teleport to land: only after a zone load was seen and ended, in the zone of the
/// area, once the player is ready (loaded, not in combat or casting). Gives up in another zone or after a minute.
/// </summary>
public sealed class JobSwitch(uint territory)
{
    public const double TimeoutSeconds = 60;

    private bool _sawLoad;

    public uint Territory { get; } = territory;

    /// <param name="betweenAreas">A zone load is going on.</param>
    /// <param name="currentTerritory">The zone the player is in.</param>
    /// <param name="ready">The player is loaded and can change gear (not in combat, not casting).</param>
    /// <param name="elapsedSeconds">Time since the teleport was asked for.</param>
    public JobSwitchStep Step(bool betweenAreas, uint currentTerritory, bool ready, double elapsedSeconds)
    {
        if (elapsedSeconds > TimeoutSeconds) return JobSwitchStep.Cancel;
        if (betweenAreas)
        {
            _sawLoad = true;
            return JobSwitchStep.Wait;
        }
        if (!_sawLoad) return JobSwitchStep.Wait;
        if (currentTerritory != Territory) return JobSwitchStep.Cancel;
        return ready ? JobSwitchStep.Switch : JobSwitchStep.Wait;
    }
}
