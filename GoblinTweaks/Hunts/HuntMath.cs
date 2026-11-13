namespace GoblinTweaks.Hunts;

/// <summary>An aetheryte drawn on a map, in map coordinates; <see cref="Cost"/> is extra distance (map units) for an awkward one.</summary>
public readonly record struct AetheryteSpot(uint Id, float X, float Y, float Cost = 0f);

public static class HuntMath
{
    /// <summary>MapMarker X/Y are pixels of the 2048-pixel map texture; this turns one into a map coordinate (1.0–42.0 at size 100).</summary>
    public static float MarkerToMapCoord(short pixel, ushort sizeFactor)
        => 41f / (sizeFactor / 100f) * (pixel / 2048f) + 1f;

    /// <summary>Game distance (yalms) to map units: a size-100 map is 41 units across 2048 yalms.</summary>
    public static float YalmsToMapUnits(float yalms, ushort sizeFactor) => yalms * 41f * (sizeFactor / 100f) / 2048f;

    /// <summary>The aetheryte closest to (x, y) counting its cost, or null when there is none.</summary>
    public static uint? Nearest(IReadOnlyList<AetheryteSpot> spots, float x, float y)
    {
        uint? best = null;
        var bestDist = float.MaxValue;
        foreach (var s in spots)
        {
            var dx = s.X - x;
            var dy = s.Y - y;
            var d = MathF.Sqrt(dx * dx + dy * dy) + s.Cost;
            if (d < bestDist) { bestDist = d; best = s.Id; }
        }
        return best;
    }
}
