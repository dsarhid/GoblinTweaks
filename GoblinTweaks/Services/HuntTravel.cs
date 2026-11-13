using Dalamud.Game;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GoblinTweaks.Hunts;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Services;

internal enum TravelResult { Teleported, TeleportedOtherWorld, TeleportedOtherInstance, AlreadyInUldah, NoAttunedAetheryte, Refused }

/// <summary>The map and teleport buttons of Goblin Hunts.</summary>
internal static unsafe class HuntTravel
{
    /// <summary>Ul'dah - Steps of Nald: where World Visit starts when the hunt is on another world.</summary>
    public const uint UldahAetheryte = 9;

    /// <summary>The two halves of Ul'dah (Steps of Nald, Steps of Thal): being there already, there is no need to teleport.</summary>
    private static readonly uint[] UldahTerritories = [130, 131];

    /// <summary>The Dravanian Hinterlands has no aetheryte of its own: Idyllshire, then Anyx Trine.</summary>
    private const uint Hinterlands = 399;
    private static readonly uint[] HinterlandsAetherytes = [75, 77];

    private const byte AetheryteMarker = 3;

    /// <summary>
    /// Extra distance, in yalms, Sonar adds to aetherytes that are awkward to leave (SonarResources AetheryteReader):
    /// underwater, in a cave, a subway, a base, or far below where you can fly up.
    /// </summary>
    private static readonly Dictionary<string, float> AwkwardByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tamamizu"] = 100f,
        ["Pla Enni"] = 50f,
        ["The Macarenses Angle"] = 500f,
        ["Tertium"] = 300f,
        ["Bestways Burrow"] = 500f,
    };

    private static Dictionary<uint, float>? _awkward;

    /// <summary>The awkward aetherytes by id, found once by their English names.</summary>
    private static Dictionary<uint, float> Awkward()
    {
        if (_awkward != null) return _awkward;
        var result = new Dictionary<uint, float>();
        foreach (var row in Svc.Data.GetExcelSheet<Aetheryte>(ClientLanguage.English))
        {
            var name = row.PlaceName.ValueNullable?.Name.ExtractText();
            if (name != null && AwkwardByName.TryGetValue(name, out var cost)) result[row.RowId] = cost;
        }
        return _awkward = result;
    }

    /// <summary>Opens the map with the flag on the hunt, like clicking the link in the chat.</summary>
    public static void OpenMap(HuntEntry e)
    {
        if (e.ZoneId == 0 || e.MapId == 0) return;
        Svc.GameGui.OpenMapWithMapLink(new MapLinkPayload(e.ZoneId, e.MapId, e.X, e.Y, 0f));
    }

    /// <summary>True when the hunt's world is in another region (NA, EU, JP, OC) than the current world: World Visit cannot reach it.</summary>
    public static bool OtherRegion(uint worldId, uint currentWorldId)
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        var a = worlds.GetRowOrDefault(worldId)?.DataCenter.ValueNullable?.Region.RowId;
        var b = worlds.GetRowOrDefault(currentWorldId)?.DataCenter.ValueNullable?.Region.RowId;
        return a is { } ra && b is { } rb && ra != rb;
    }

    /// <summary>
    /// Teleports toward the hunt: to the attuned aetheryte nearest to it on the same world, or to Ul'dah when it is on
    /// another world. The game may still refuse (in combat, in a duty, casting).
    /// </summary>
    public static TravelResult Teleport(HuntEntry e, uint currentWorldId, uint currentZoneId, uint currentInstance)
    {
        var telepo = Telepo.Instance();
        if (telepo == null) return TravelResult.Refused;

        if (e.WorldId != currentWorldId)
        {
            if (UldahTerritories.Contains(currentZoneId)) return TravelResult.AlreadyInUldah;
            return telepo->Teleport(UldahAetheryte, 0) ? TravelResult.TeleportedOtherWorld : TravelResult.Refused;
        }

        var attuned = Attuned(telepo);
        uint? target = null;
        if (e.ZoneId == Hinterlands)
        {
            foreach (var id in HinterlandsAetherytes)
                if (attuned.Contains(id)) { target = id; break; }
        }
        else
        {
            target = HuntMath.Nearest(AetherytesOn(e.MapId, attuned), e.X, e.Y);
        }

        if (target is not { } aetheryte) return TravelResult.NoAttunedAetheryte;
        if (!telepo->Teleport(aetheryte, 0)) return TravelResult.Refused;

        // An instanced hunt: unless you are already in its instance, say which one to change to.
        return e.InstanceId != 0 && (e.ZoneId != currentZoneId || e.InstanceId != currentInstance)
            ? TravelResult.TeleportedOtherInstance
            : TravelResult.Teleported;
    }

    private static HashSet<uint> Attuned(Telepo* telepo)
    {
        var set = new HashSet<uint>();
        var list = telepo->UpdateAetheryteList();
        if (list == null) return set;
        foreach (var info in *list) set.Add(info.AetheryteId);
        return set;
    }

    /// <summary>The attuned aetherytes drawn on a map, in map coordinates.</summary>
    private static List<AetheryteSpot> AetherytesOn(uint mapId, HashSet<uint> attuned)
    {
        var spots = new List<AetheryteSpot>();
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRowOrDefault(mapId) is not { } map) return spots;
        if (!Svc.Data.GetSubrowExcelSheet<MapMarker>().TryGetRow(map.MapMarkerRange, out var markers)) return spots;

        foreach (var marker in markers)
        {
            if (marker.DataType != AetheryteMarker) continue;
            var id = marker.DataKey.RowId;
            if (!attuned.Contains(id)) continue;
            var cost = Awkward().TryGetValue(id, out var yalms) ? HuntMath.YalmsToMapUnits(yalms, map.SizeFactor) : 0f;
            spots.Add(new AetheryteSpot(id, HuntMath.MarkerToMapCoord(marker.X, map.SizeFactor), HuntMath.MarkerToMapCoord(marker.Y, map.SizeFactor), cost));
        }
        return spots;
    }
}
