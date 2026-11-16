using Dalamud.Game;
using GoblinTweaks.Crafting;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Core;

/// <summary>
/// Where items come from, read from the game data: the spots they are mined, logged, fished or speared at, whether a
/// gil shop sells them, and whether they are crystals. Built once per language, the first time it is asked for.
/// </summary>
internal sealed class GatheringSources
{
    /// <summary>ItemUICategory of shards, crystals and clusters.</summary>
    private const uint CrystalCategory = 59;

    /// <summary>EphemeralStartTime/EphemeralEndTime of a point that is always up.</summary>
    private const ushort NoEphemeralTime = 65535;

    /// <summary>Map icon of a fishing hole (fishing has no GatheringType row).</summary>
    private const uint FishingIcon = 60465;

    /// <summary>
    /// FishingSpot and SpearfishingNotebook give their radius in a unit about 7 times a yalm (a notebook spot of 800 is
    /// the 114-yalm area of its ExportedGatheringPoint). To be confirmed in game for fishing holes.
    /// </summary>
    private const float NotebookRadiusPerYalm = 7f;

    private static readonly Dictionary<ClientLanguage, GatheringSources> Cache = [];

    private readonly Dictionary<uint, GatherSpot[]>        _spots;
    private readonly Dictionary<AreaKey, List<GatherArea>> _areas;
    private readonly HashSet<uint>                         _vendor;
    private readonly ExcelSheet<Item>?                     _items;

    /// <summary>The areas of one item in one zone for one class.</summary>
    private readonly record struct AreaKey(uint Item, uint Zone, GatherClass Class);

    /// <summary>What a build collects: one spot per item, zone, place and class; the areas, once per point base or fishing spot.</summary>
    private sealed class Collected
    {
        public readonly Dictionary<(uint Item, uint Zone, string Place, GatherClass Class), GatherSpot> Spots = [];
        public readonly Dictionary<AreaKey, List<GatherArea>> Areas = [];
        public readonly HashSet<(uint Item, uint Source)> AreaSources = [];
    }

    private GatheringSources(Dictionary<uint, GatherSpot[]> spots, Dictionary<AreaKey, List<GatherArea>> areas,
        HashSet<uint> vendor, ExcelSheet<Item>? items)
    {
        _spots  = spots;
        _areas  = areas;
        _vendor = vendor;
        _items  = items;
    }

    /// <summary>The sources in <paramref name="lang"/>, read from the game data the first time.</summary>
    public static GatheringSources For(ClientLanguage lang)
    {
        if (!Cache.TryGetValue(lang, out var sources))
            Cache[lang] = sources = Build(lang);
        return sources;
    }

    /// <summary>Everything known about where <paramref name="itemId"/> comes from; <see cref="ItemSources.None"/> if nothing is.</summary>
    public ItemSources Get(uint itemId)
    {
        var spots   = _spots.GetValueOrDefault(itemId);
        var vendor  = _vendor.Contains(itemId);
        var crystal = _items != null && _items.TryGetRow(itemId, out var item) && item.ItemUICategory.RowId == CrystalCategory;
        if (spots == null && !vendor && !crystal) return ItemSources.None;
        return new ItemSources(spots ?? [], vendor, crystal);
    }

    /// <summary>The areas <paramref name="itemId"/> is gathered at in <paramref name="zoneId"/> by <paramref name="cls"/>; empty if none is known.</summary>
    public IReadOnlyList<GatherArea> Areas(uint itemId, uint zoneId, GatherClass cls)
        => _areas.TryGetValue(new AreaKey(itemId, zoneId, cls), out var list) ? list : [];

    private static GatheringSources Build(ClientLanguage lang)
    {
        var found  = new Collected();
        var vendor = new HashSet<uint>();
        ExcelSheet<Item>? items = null;

        // Each part is read on its own: a sheet that no longer reads loses only its part (keeping what was read before
        // it), and the rest still works. The partial result is cached like a full one, so a broken sheet isn't retried.
        void Read(string part, System.Action read)
        {
            try { read(); }
            catch (Exception ex) { Svc.Log.Warning(ex, $"GatheringSources: could not read the {part} data ({lang})"); }
        }

        Read("item",      () => items = Svc.Data.GetExcelSheet<Item>(lang));
        Read("gathering", () => AddGathering(lang, found));
        Read("fishing",   () => AddFishing(lang, found));
        Read("vendor",    () =>
        {
            foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>(lang))
                foreach (var entry in shop)
                    if (entry.Item.RowId != 0) vendor.Add(entry.Item.RowId);
        });

        var byItem = found.Spots
            .GroupBy(kv => kv.Key.Item, kv => kv.Value)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(s => s.ZoneName, StringComparer.Ordinal)
                .ThenBy(s => s.ZoneId)
                .ThenBy(s => s.PlaceName, StringComparer.Ordinal)
                .ThenBy(s => s.Class)
                .ToArray());

        Svc.Log.Info($"GatheringSources: {byItem.Count} gatherable items, {found.Areas.Count} item areas, {vendor.Count} vendor items ({lang})");
        return new GatheringSources(byItem, found.Areas, vendor, items);
    }

    /// <summary>
    /// Mining, botany and spearfishing points. GatheringItemPoint lists the points of each gathering item, but only one
    /// point of each group, and it misses items the points list themselves in GatheringPointBase; both are read.
    /// Territory 1 holds thousands of unused points; its empty name leaves them out.
    /// </summary>
    private static void AddGathering(ClientLanguage lang, Collected found)
    {
        var gatheringItems = Svc.Data.GetExcelSheet<GatheringItem>(lang);
        var itemPoints     = Svc.Data.GetSubrowExcelSheet<GatheringItemPoint>(lang);
        var points         = Svc.Data.GetExcelSheet<GatheringPoint>(lang);
        var fishes         = Svc.Data.GetExcelSheet<SpearfishingItem>(lang);
        var ctx            = new PointContext(lang, found);

        foreach (var gatheringItem in gatheringItems)
        {
            if (!itemPoints.TryGetRow(gatheringItem.RowId, out var subrows)) continue;
            foreach (var entry in subrows)
                if (entry.GatheringPoint.IsValid)
                    AddGatheringItem(ctx, gatheringItem, entry.GatheringPoint.Value);
        }

        foreach (var point in points)
        {
            if (!point.GatheringPointBase.IsValid) continue;
            var pointBase = point.GatheringPointBase.Value;

            // The items of a spearfishing point are SpearfishingItem rows. SpearfishingItem.TerritoryType is not a
            // territory (it holds 0-63 and 10000), so this is the only place their zone comes from.
            var spearfishing = ClassOf(pointBase.GatheringType.RowId) == GatherClass.Fisher;
            foreach (var entry in pointBase.Item)
            {
                if (entry.RowId == 0) continue;
                if (spearfishing)
                {
                    if (fishes.TryGetRow(entry.RowId, out var fish) && fish.Item.RowId != 0 && fish.Item.IsValid)
                        AddPoint(ctx, fish.Item.RowId, LevelOf(fish.GatheringItemLevel), point);
                }
                else if (gatheringItems.TryGetRow(entry.RowId, out var gatheringItem))
                    AddGatheringItem(ctx, gatheringItem, point);
            }
        }
    }

    /// <summary>Adds the gathering item at the point, unless it is a quest item (whose GatheringItem.Item is an EventItem).</summary>
    private static void AddGatheringItem(PointContext ctx, GatheringItem gatheringItem, GatheringPoint point)
    {
        if (gatheringItem.Item.RowId == 0 || !gatheringItem.Item.Is<Item>()) return;
        AddPoint(ctx, gatheringItem.Item.RowId, LevelOf(gatheringItem.GatheringItemLevel), point);
    }

    private static void AddPoint(PointContext ctx, uint itemId, int level, GatheringPoint point)
    {
        if (!point.GatheringPointBase.IsValid) return;
        var pointBase = point.GatheringPointBase.Value;
        if (ClassOf(pointBase.GatheringType.RowId) is not { } cls) return;
        if (!TryZone(point.TerritoryType, out var zoneId, out var zoneName)) return;

        Add(ctx.Found.Spots, itemId, new GatherSpot(zoneId, zoneName, PlaceOr(point.PlaceName, zoneName), cls, level, IsTimed(point.RowId, ctx.Transients)));

        // The area once per point base: the points of a base share one circle on the map.
        if (!ctx.Found.AreaSources.Add((itemId, pointBase.RowId))) return;
        if (ctx.AreaOf(point, pointBase, zoneId, cls) is { } area)
            AddArea(ctx.Found, itemId, area);
    }

    private static void AddArea(Collected found, uint itemId, GatherArea area)
    {
        var key = new AreaKey(itemId, area.ZoneId, area.Class);
        if (!found.Areas.TryGetValue(key, out var list)) found.Areas[key] = list = [];
        list.Add(area);
    }

    /// <summary>The sheets the areas of mining, botany and spearfishing points are read from.</summary>
    private sealed class PointContext
    {
        public readonly Collected Found;
        public readonly ExcelSheet<GatheringPointTransient> Transients;
        private readonly ExcelSheet<ExportedGatheringPoint> _exported;
        private readonly ExcelSheet<GatheringPointName>     _names;
        private readonly Dictionary<uint, SpearfishingNotebook> _notebook = [];
        private readonly bool _english;

        public PointContext(ClientLanguage lang, Collected found)
        {
            Found      = found;
            Transients = Svc.Data.GetExcelSheet<GatheringPointTransient>(lang);
            _exported  = Svc.Data.GetExcelSheet<ExportedGatheringPoint>(lang);
            _names     = Svc.Data.GetExcelSheet<GatheringPointName>(lang);
            _english   = lang == ClientLanguage.English;
            foreach (var row in Svc.Data.GetExcelSheet<SpearfishingNotebook>(lang))
                if (row.GatheringPointBase.RowId != 0) _notebook.TryAdd(row.GatheringPointBase.RowId, row);
        }

        /// <summary>
        /// The circle the Gathering Log draws for a point base: ExportedGatheringPoint (world centre, radius in yalms);
        /// for a spearfishing base without one, its SpearfishingNotebook row (texture pixels). Null without usable data.
        /// </summary>
        public GatherArea? AreaOf(GatheringPoint point, GatheringPointBase pointBase, uint zoneId, GatherClass cls)
        {
            if (!point.TerritoryType.IsValid || point.TerritoryType.Value.Map.ValueNullable is not { } map || map.RowId == 0) return null;
            var level = pointBase.GatheringLevel;
            var icon  = (uint)Math.Max(0, pointBase.GatheringType.ValueNullable?.IconMain ?? 0);

            if (_exported.TryGetRow(pointBase.RowId, out var exported) && exported.Radius > 0)
            {
                var nameRow = TravelMath.PointNameRow(exported.GatheringType.RowId, exported.GatheringPointType);
                var kind    = nameRow != 0 && _names.TryGetRow(nameRow, out var name) ? name.Singular.ExtractText() : string.Empty;
                return new GatherArea(zoneId, map.RowId, cls, exported.X, exported.Y, exported.Radius, icon, Label(level, kind, _english));
            }

            if (_notebook.TryGetValue(pointBase.RowId, out var spot) && spot.Radius > 0)
                return new GatherArea(zoneId, map.RowId, cls,
                    TravelMath.PixelToWorld(spot.X, map.SizeFactor, map.OffsetX), TravelMath.PixelToWorld(spot.Y, map.SizeFactor, map.OffsetY),
                    spot.Radius / NotebookRadiusPerYalm, icon, Label(level, PlaceOr(spot.PlaceName, string.Empty), false));

            return null;
        }
    }

    /// <summary>"Lv. 35 Mineral Deposit": the game's names are lower case ("mineral deposit"); English gets title case, the rest a capital first letter.</summary>
    private static string Label(int level, string kind, bool titleCase)
    {
        kind = kind.Trim();
        if (kind.Length > 0)
            kind = titleCase
                ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind)
                : char.ToUpperInvariant(kind[0]) + kind[1..];
        return kind.Length > 0 ? $"Lv. {level} {kind}" : $"Lv. {level}";
    }

    /// <summary>Unspoiled and legendary points have a pop time table; ephemeral ones a start and end time.</summary>
    private static bool IsTimed(uint pointId, ExcelSheet<GatheringPointTransient> transients)
    {
        if (!transients.TryGetRow(pointId, out var transient)) return false;
        return transient.GatheringRarePopTimeTable.RowId != 0
            || (transient.EphemeralStartTime != NoEphemeralTime && transient.EphemeralStartTime != transient.EphemeralEndTime);
    }

    private static void AddFishing(ClientLanguage lang, Collected found)
    {
        foreach (var spot in Svc.Data.GetExcelSheet<FishingSpot>(lang))
        {
            if (!TryZone(spot.TerritoryType, out var zoneId, out var zoneName)) continue;
            var place      = PlaceOr(spot.PlaceName, zoneName);
            var gatherSpot = new GatherSpot(zoneId, zoneName, place, GatherClass.Fisher, spot.GatheringLevel, false);

            // The fishing hole's circle: X/Z are pixels of the map texture.
            GatherArea? area = null;
            if (spot.Radius > 0 && spot.TerritoryType.Value.Map.ValueNullable is { RowId: not 0 } map)
                area = new GatherArea(zoneId, map.RowId, GatherClass.Fisher,
                    TravelMath.PixelToWorld(spot.X, map.SizeFactor, map.OffsetX), TravelMath.PixelToWorld(spot.Z, map.SizeFactor, map.OffsetY),
                    spot.Radius / NotebookRadiusPerYalm, FishingIcon, Label(spot.GatheringLevel, place, false));

            foreach (var item in spot.Item)
            {
                if (item.RowId == 0 || !item.IsValid) continue;
                Add(found.Spots, item.RowId, gatherSpot);
                // Fishing spot ids and point base ids are rows of different sheets: the high bit keeps them apart.
                if (area != null && found.AreaSources.Add((item.RowId, 0x8000_0000u | spot.RowId)))
                    AddArea(found, item.RowId, area);
            }
        }
    }

    private static int LevelOf(RowRef<GatheringItemLevelConvertTable> level) => level.ValueNullable?.GatheringItemLevel ?? 0;

    private static string PlaceOr(RowRef<PlaceName> place, string fallback)
    {
        var name = place.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    /// <summary>One spot per item, zone, place and class: the easiest one, not timed first, then the lowest level.</summary>
    private static void Add(Dictionary<(uint, uint, string, GatherClass), GatherSpot> spots, uint itemId, GatherSpot spot)
    {
        var key = (itemId, spot.ZoneId, spot.PlaceName, spot.Class);
        if (spots.TryGetValue(key, out var known)
            && (known.Timed, known.Level).CompareTo((spot.Timed, spot.Level)) <= 0) return;
        spots[key] = spot;
    }

    private static bool TryZone(RowRef<TerritoryType> territory, out uint zoneId, out string zoneName)
    {
        zoneId   = territory.RowId;
        zoneName = zoneId != 0 && territory.IsValid ? territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "" : "";
        return zoneName.Length > 0;
    }

    /// <summary>GatheringType 0-1 mining and quarrying, 2-3 logging and harvesting, 4-5 spearfishing.</summary>
    private static GatherClass? ClassOf(uint gatheringType) => gatheringType switch
    {
        0 or 1 => GatherClass.Miner,
        2 or 3 => GatherClass.Botanist,
        4 or 5 => GatherClass.Fisher,
        _      => null,
    };
}
