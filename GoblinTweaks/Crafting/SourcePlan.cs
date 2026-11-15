namespace GoblinTweaks.Crafting;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

/// <summary>The gathering class that harvests a spot.</summary>
public enum GatherClass { Miner, Botanist, Fisher }

/// <summary>Where a group of materials comes from.</summary>
public enum SourceKind { Zone, Vendor, Other, Crystals, Done }

/// <summary>One place an item can be gathered: the zone, the spot inside it, and what it takes to harvest it.</summary>
public sealed record GatherSpot(uint ZoneId, string ZoneName, string PlaceName, GatherClass Class, int Level, bool Timed);

/// <summary>Everything known about where one item comes from.</summary>
public sealed record ItemSources(IReadOnlyList<GatherSpot> Spots, bool Vendor, bool Crystal)
{
    /// <summary>An item with no known source.</summary>
    public static readonly ItemSources None = new([], false, false);
}

/// <summary>
/// One material in a group. <see cref="Spot"/> is the best place to gather it in the group's zone,
/// <see cref="AlsoIn"/> the names of the other zones it can be found in.
/// </summary>
public sealed record SourceLine(Need Need, int Owned, GatherSpot? Spot, IReadOnlyList<string> AlsoIn, bool AlsoVendor)
{
    /// <summary>True while the player has less than the item is needed.</summary>
    public bool Missing => Owned < Need.Amount;
}

/// <summary>The materials that come from one place, with the gathering classes the place needs.</summary>
public sealed record SourceGroup(SourceKind Kind, string Key, uint ZoneId, string ZoneName,
    IReadOnlyList<GatherClass> Classes, IReadOnlyList<SourceLine> Lines)
{
    /// <summary>How many of the lines are still missing.</summary>
    public int Missing => Lines.Count(l => l.Missing);
}

/// <summary>Groups base materials by where they are gathered or bought, so no trip is made twice.</summary>
public static class SourcePlan
{
    /// <summary>
    /// Every base material once: crystals, then what is already owned, then each missing gatherable item in the zone that
    /// covers the most of them, then vendor items, then the rest. Empty groups are left out.
    /// </summary>
    public static List<SourceGroup> Build(IReadOnlyList<Need> bases, Func<uint, int> owned, Func<uint, ItemSources> sources)
    {
        var crystals = new List<SourceLine>();
        var done     = new List<SourceLine>();
        var vendor   = new List<SourceLine>();
        var other    = new List<SourceLine>();
        var gather   = new List<(Need Need, int Owned, ItemSources Src, HashSet<uint> Zones)>();

        foreach (var need in bases)
        {
            var have = owned(need.ItemId);
            var src  = sources(need.ItemId);
            var line = new SourceLine(need, have, null, [], false);
            if (src.Crystal)             crystals.Add(line);
            else if (have >= need.Amount) done.Add(line);
            else if (src.Spots.Count > 0) gather.Add((need, have, src, src.Spots.Select(s => s.ZoneId).ToHashSet()));
            else if (src.Vendor)         vendor.Add(line);
            else                         other.Add(line);
        }

        // One name per zone, whatever order the spots come in.
        var zoneNames = new Dictionary<uint, string>();
        foreach (var g in gather)
            foreach (var spot in g.Src.Spots)
                if (!zoneNames.TryGetValue(spot.ZoneId, out var name) || string.CompareOrdinal(spot.ZoneName, name) < 0)
                    zoneNames[spot.ZoneId] = spot.ZoneName;

        var singleZone = new Dictionary<uint, int>();
        foreach (var g in gather)
            if (g.Zones.Count == 1)
            {
                var z = g.Zones.First();
                singleZone[z] = singleZone.GetValueOrDefault(z) + 1;
            }

        // Greedy cover: the zone that reaches the most unassigned items takes all of them.
        var assigned   = new Dictionary<uint, List<(Need Need, int Owned, ItemSources Src, HashSet<uint> Zones)>>();
        var unassigned = new List<(Need Need, int Owned, ItemSources Src, HashSet<uint> Zones)>(gather);
        while (unassigned.Count > 0)
        {
            var cover = new Dictionary<uint, int>();
            foreach (var g in unassigned)
                foreach (var z in g.Zones)
                    cover[z] = cover.GetValueOrDefault(z) + 1;

            var best = cover.Keys
                .OrderByDescending(z => cover[z])
                .ThenByDescending(z => singleZone.GetValueOrDefault(z))
                .ThenBy(z => zoneNames[z], StringComparer.Ordinal)
                .ThenBy(z => z)
                .First();

            var taken = unassigned.Where(g => g.Zones.Contains(best)).ToList();
            assigned[best] = taken;
            unassigned.RemoveAll(g => g.Zones.Contains(best));
        }

        var groups = new List<SourceGroup>();
        foreach (var (zone, items) in assigned)
        {
            var lines = items.Select(g => new SourceLine(
                g.Need, g.Owned, BestSpot(g.Src.Spots, zone),
                g.Src.Spots.Where(s => s.ZoneId != zone).Select(s => zoneNames[s.ZoneId])
                    .Distinct().Order(StringComparer.Ordinal).ToList(),
                g.Src.Vendor)).ToList();
            var classes = lines.Select(l => l.Spot!.Class).Distinct().Order().ToList();
            groups.Add(new SourceGroup(SourceKind.Zone, $"zone:{zone}", zone, zoneNames[zone], classes, Sorted(lines)));
        }

        var ordered = groups
            .OrderByDescending(g => g.Missing)
            .ThenBy(g => g.ZoneName, StringComparer.Ordinal)
            .ThenBy(g => g.ZoneId)
            .ToList();

        AddFixed(ordered, SourceKind.Vendor,   "vendor",   vendor);
        AddFixed(ordered, SourceKind.Other,    "other",    other);
        AddFixed(ordered, SourceKind.Crystals, "crystals", crystals);
        AddFixed(ordered, SourceKind.Done,     "done",     done);
        return ordered;
    }

    private static void AddFixed(List<SourceGroup> groups, SourceKind kind, string key, List<SourceLine> lines)
    {
        if (lines.Count > 0)
            groups.Add(new SourceGroup(kind, key, 0, "", [], Sorted(lines)));
    }

    private static List<SourceLine> Sorted(IEnumerable<SourceLine> lines)
        => lines.OrderByDescending(l => l.Missing).ThenBy(l => l.Need.Name, StringComparer.Ordinal).ToList();

    /// <summary>The spot in the zone that is easiest to use: not timed first, then the lowest level.</summary>
    private static GatherSpot BestSpot(IReadOnlyList<GatherSpot> spots, uint zone)
        => spots.Where(s => s.ZoneId == zone)
                .OrderBy(s => s.Timed).ThenBy(s => s.Level).ThenBy(s => s.PlaceName, StringComparer.Ordinal)
                .First();
}
