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

    private static readonly Dictionary<ClientLanguage, GatheringSources> Cache = [];

    private readonly Dictionary<uint, GatherSpot[]> _spots;
    private readonly HashSet<uint>                  _vendor;
    private readonly ExcelSheet<Item>?              _items;

    private GatheringSources(Dictionary<uint, GatherSpot[]> spots, HashSet<uint> vendor, ExcelSheet<Item>? items)
    {
        _spots  = spots;
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

    private static GatheringSources Build(ClientLanguage lang)
    {
        var spots  = new Dictionary<(uint Item, uint Zone, string Place, GatherClass Class), GatherSpot>();
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
        Read("gathering", () => AddGathering(lang, spots));
        Read("fishing",   () => AddFishing(lang, spots));
        Read("vendor",    () =>
        {
            foreach (var shop in Svc.Data.GetSubrowExcelSheet<GilShopItem>(lang))
                foreach (var entry in shop)
                    if (entry.Item.RowId != 0) vendor.Add(entry.Item.RowId);
        });

        var byItem = spots
            .GroupBy(kv => kv.Key.Item, kv => kv.Value)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(s => s.ZoneName, StringComparer.Ordinal)
                .ThenBy(s => s.ZoneId)
                .ThenBy(s => s.PlaceName, StringComparer.Ordinal)
                .ThenBy(s => s.Class)
                .ToArray());

        Svc.Log.Info($"GatheringSources: {byItem.Count} gatherable items, {vendor.Count} vendor items ({lang})");
        return new GatheringSources(byItem, vendor, items);
    }

    /// <summary>
    /// Mining, botany and spearfishing points. GatheringItemPoint lists the points of each gathering item, but only one
    /// point of each group, and it misses items the points list themselves in GatheringPointBase; both are read.
    /// Territory 1 holds thousands of unused points; its empty name leaves them out.
    /// </summary>
    private static void AddGathering(ClientLanguage lang, Dictionary<(uint, uint, string, GatherClass), GatherSpot> spots)
    {
        var gatheringItems = Svc.Data.GetExcelSheet<GatheringItem>(lang);
        var itemPoints     = Svc.Data.GetSubrowExcelSheet<GatheringItemPoint>(lang);
        var points         = Svc.Data.GetExcelSheet<GatheringPoint>(lang);
        var transients     = Svc.Data.GetExcelSheet<GatheringPointTransient>(lang);
        var fishes         = Svc.Data.GetExcelSheet<SpearfishingItem>(lang);

        foreach (var gatheringItem in gatheringItems)
        {
            if (!itemPoints.TryGetRow(gatheringItem.RowId, out var subrows)) continue;
            foreach (var entry in subrows)
                if (entry.GatheringPoint.IsValid)
                    AddGatheringItem(spots, gatheringItem, entry.GatheringPoint.Value, transients);
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
                        AddPoint(spots, fish.Item.RowId, LevelOf(fish.GatheringItemLevel), point, transients);
                }
                else if (gatheringItems.TryGetRow(entry.RowId, out var gatheringItem))
                    AddGatheringItem(spots, gatheringItem, point, transients);
            }
        }
    }

    /// <summary>Adds the gathering item at the point, unless it is a quest item (whose GatheringItem.Item is an EventItem).</summary>
    private static void AddGatheringItem(Dictionary<(uint, uint, string, GatherClass), GatherSpot> spots, GatheringItem gatheringItem,
        GatheringPoint point, ExcelSheet<GatheringPointTransient> transients)
    {
        if (gatheringItem.Item.RowId == 0 || !gatheringItem.Item.Is<Item>()) return;
        AddPoint(spots, gatheringItem.Item.RowId, LevelOf(gatheringItem.GatheringItemLevel), point, transients);
    }

    private static void AddPoint(Dictionary<(uint, uint, string, GatherClass), GatherSpot> spots, uint itemId, int level,
        GatheringPoint point, ExcelSheet<GatheringPointTransient> transients)
    {
        if (!point.GatheringPointBase.IsValid) return;
        if (ClassOf(point.GatheringPointBase.Value.GatheringType.RowId) is not { } cls) return;
        if (!TryZone(point.TerritoryType, out var zoneId, out var zoneName)) return;

        Add(spots, itemId, new GatherSpot(zoneId, zoneName, PlaceOr(point.PlaceName, zoneName), cls, level, IsTimed(point.RowId, transients)));
    }

    /// <summary>Unspoiled and legendary points have a pop time table; ephemeral ones a start and end time.</summary>
    private static bool IsTimed(uint pointId, ExcelSheet<GatheringPointTransient> transients)
    {
        if (!transients.TryGetRow(pointId, out var transient)) return false;
        return transient.GatheringRarePopTimeTable.RowId != 0
            || (transient.EphemeralStartTime != NoEphemeralTime && transient.EphemeralStartTime != transient.EphemeralEndTime);
    }

    private static void AddFishing(ClientLanguage lang, Dictionary<(uint, uint, string, GatherClass), GatherSpot> spots)
    {
        foreach (var spot in Svc.Data.GetExcelSheet<FishingSpot>(lang))
        {
            if (!TryZone(spot.TerritoryType, out var zoneId, out var zoneName)) continue;
            var gatherSpot = new GatherSpot(zoneId, zoneName, PlaceOr(spot.PlaceName, zoneName), GatherClass.Fisher, spot.GatheringLevel, false);

            foreach (var item in spot.Item)
                if (item.RowId != 0 && item.IsValid) Add(spots, item.RowId, gatherSpot);
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
