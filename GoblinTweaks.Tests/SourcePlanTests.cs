namespace GoblinTweaks.Tests;

public class SourcePlanTests
{
    private static GatherSpot Spot(uint zone, string name, string place = "Place", GatherClass cls = GatherClass.Miner,
                                   int level = 90, bool timed = false)
        => new(zone, name, place, cls, level, timed);

    private static ItemSources Gather(params GatherSpot[] spots) => new(spots, false, false);

    private static readonly ItemSources Vendor  = new([], true, false);
    private static readonly ItemSources Crystal = new([], false, true);

    private static Need N(uint id, string name, int amount = 1) => new(id, name, 0, amount);

    private static List<SourceGroup> Build(IReadOnlyList<Need> needs, Dictionary<uint, ItemSources> sources,
                                           Dictionary<uint, int>? owned = null)
        => SourcePlan.Build(needs, id => owned?.GetValueOrDefault(id) ?? 0,
                            id => sources.GetValueOrDefault(id, ItemSources.None));

    private static SourceGroup ZoneGroup(List<SourceGroup> groups, uint zone)
        => groups.Single(g => g.Kind == SourceKind.Zone && g.ZoneId == zone);

    private static SourceLine Line(SourceGroup group, uint id) => group.Lines.Single(l => l.Need.ItemId == id);

    [Fact]
    public void CrystalsGoToCrystalsEvenWhenMissing()
    {
        var groups = Build([N(1, "Fire Shard")], new() { [1] = Crystal });
        var group = Assert.Single(groups);
        Assert.Equal(SourceKind.Crystals, group.Kind);
        Assert.Equal("crystals", group.Key);
    }

    [Fact]
    public void OwnedItemsGoToDone()
    {
        var groups = Build([N(1, "Ore", 5)], new() { [1] = Gather(Spot(10, "Yak T'el")) }, new() { [1] = 5 });
        var group = Assert.Single(groups);
        Assert.Equal(SourceKind.Done, group.Kind);
        Assert.Equal("done", group.Key);
        Assert.False(group.Lines[0].Missing);
    }

    [Fact]
    public void GatherableGoesToItsZone()
    {
        var groups = Build([N(1, "Ore")], new() { [1] = Gather(Spot(10, "Yak T'el", "Xmun Hojaw")) });
        var group = Assert.Single(groups);
        Assert.Equal(SourceKind.Zone, group.Kind);
        Assert.Equal("zone:10", group.Key);
        Assert.Equal(10u, group.ZoneId);
        Assert.Equal("Yak T'el", group.ZoneName);
        Assert.Equal("Xmun Hojaw", group.Lines[0].Spot!.PlaceName);
    }

    [Fact]
    public void GatherBeatsVendor()
    {
        var groups = Build([N(1, "Ore")], new() { [1] = new([Spot(10, "Z")], true, false) });
        var group = Assert.Single(groups);
        Assert.Equal(SourceKind.Zone, group.Kind);
        Assert.True(group.Lines[0].AlsoVendor);
    }

    [Fact]
    public void VendorOnlyGoesToVendor()
    {
        var group = Assert.Single(Build([N(1, "Thing")], new() { [1] = Vendor }));
        Assert.Equal(SourceKind.Vendor, group.Kind);
        Assert.Equal("vendor", group.Key);
    }

    [Fact]
    public void NoSpotsGoesToOther()
    {
        var group = Assert.Single(Build([N(1, "Thing")], new() { [1] = ItemSources.None }));
        Assert.Equal(SourceKind.Other, group.Kind);
        Assert.Equal("other", group.Key);
    }

    [Fact]
    public void MultiZoneItemJoinsTheZoneWithMostItems()
    {
        var groups = Build([N(1, "A"), N(2, "B"), N(3, "C")], new()
        {
            [1] = Gather(Spot(1, "Zone 1")),
            [2] = Gather(Spot(1, "Zone 1")),
            [3] = Gather(Spot(1, "Zone 1"), Spot(2, "Zone 2")),
        });
        var group = Assert.Single(groups);
        Assert.Equal(1u, group.ZoneId);
        Assert.Equal(3, group.Lines.Count);
        Assert.Equal(["Zone 2"], Line(group, 3).AlsoIn);
        Assert.Empty(Line(group, 1).AlsoIn);
    }

    [Fact]
    public void ThreeZoneItemPicksTheCoveringZone()
    {
        var groups = Build([N(4, "D"), N(5, "E")], new()
        {
            [4] = Gather(Spot(1, "Zone 1"), Spot(2, "Zone 2"), Spot(3, "Zone 3")),
            [5] = Gather(Spot(3, "Zone 3")),
        });
        var group = Assert.Single(groups);
        Assert.Equal(3u, group.ZoneId);
        Assert.Equal(["Zone 1", "Zone 2"], Line(group, 4).AlsoIn);
    }

    [Fact]
    public void ZoneTieBreaksBySingleZoneItemsThenName()
    {
        // Cover ties at 1 and nothing is single-zone: the name decides.
        var byName = Build([N(1, "F")], new() { [1] = Gather(Spot(2, "B"), Spot(1, "A")) });
        Assert.Equal("A", Assert.Single(byName).ZoneName);

        // K in P,Q; M only in P; N in Q,R. P and Q both cover 2; P has one single-zone item, Q none.
        var groups = Build([N(1, "K"), N(2, "M"), N(3, "N")], new()
        {
            [1] = Gather(Spot(1, "P"), Spot(2, "Q")),
            [2] = Gather(Spot(1, "P")),
            [3] = Gather(Spot(2, "Q"), Spot(3, "R")),
        });
        Assert.Equal(2, groups.Count);
        var p = ZoneGroup(groups, 1);
        Assert.Equal(new uint[] { 1, 2 }, p.Lines.Select(l => l.Need.ItemId).Order().ToArray());
        var q = ZoneGroup(groups, 2);
        Assert.Equal(3u, Assert.Single(q.Lines).Need.ItemId);
        Assert.Equal(["R"], q.Lines[0].AlsoIn);
    }

    [Fact]
    public void SpotPrefersNonTimedThenLowestLevel()
    {
        var timed = Spot(1, "Z", "Timed", level: 50, timed: true);
        var high  = Spot(1, "Z", "High", level: 90);
        var low   = Spot(1, "Z", "Low", level: 70);
        var group = Assert.Single(Build([N(1, "Ore")], new() { [1] = Gather(timed, high, low) }));
        Assert.Equal("Low", group.Lines[0].Spot!.PlaceName);

        var onlyTimed = Assert.Single(Build([N(1, "Ore")], new() { [1] = Gather(timed) }));
        Assert.Equal("Timed", onlyTimed.Lines[0].Spot!.PlaceName);
    }

    [Fact]
    public void GroupsAreOrdered()
    {
        var needs = new[]
        {
            N(1, "Ore"), N(2, "Sand"), N(3, "Vend"), N(4, "Misc"), N(5, "Shard"), N(6, "Done"),
            N(7, "Herb"),
        };
        var groups = Build(needs, new()
        {
            [1] = Gather(Spot(1, "Zeta")),
            [2] = Gather(Spot(1, "Zeta")),
            [3] = Vendor,
            [5] = Crystal,
            [6] = Gather(Spot(2, "Alpha")),
            [7] = Gather(Spot(2, "Alpha")),
        }, new() { [6] = 1 });
        Assert.Equal(
            ["zone:1", "zone:2", "vendor", "other", "crystals", "done"],
            groups.Select(g => g.Key).ToArray());

        // Same missing count: name order.
        var tie = Build([N(1, "a"), N(2, "b")], new()
        {
            [1] = Gather(Spot(1, "Zeta")),
            [2] = Gather(Spot(2, "Alpha")),
        });
        Assert.Equal(["zone:2", "zone:1"], tie.Select(g => g.Key).ToArray());
    }

    [Fact]
    public void LinesMissingFirstThenName()
    {
        var groups = Build([N(1, "Zinc"), N(2, "Apple"), N(3, "Mango"), N(4, "Bean")], new()
        {
            [1] = Gather(Spot(1, "Z")), [2] = Gather(Spot(1, "Z")),
            [3] = Gather(Spot(1, "Z")), [4] = Gather(Spot(1, "Z")),
        }, new() { [2] = 1 });
        // Apple is owned, so it is in Done; the zone holds the rest.
        Assert.Equal(["Bean", "Mango", "Zinc"], ZoneGroup(groups, 1).Lines.Select(l => l.Need.Name).ToArray());
        Assert.Equal(SourceKind.Done, groups[^1].Kind);

        // Within one group, missing lines come before owned-or-partial ones: Zed (1 of 2) is still missing.
        var vendor = Build([N(1, "Zed", 2), N(2, "Amy", 2), N(3, "Bob", 2)],
                           new() { [1] = Vendor, [2] = Vendor, [3] = Vendor }, new() { [1] = 1 });
        Assert.Equal(["Amy", "Bob", "Zed"], vendor.Single(g => g.Kind == SourceKind.Vendor).Lines.Select(l => l.Need.Name).ToArray());
    }

    [Fact]
    public void ZoneClassesAreDistinctInEnumOrder()
    {
        var groups = Build([N(1, "Herb"), N(2, "Ore"), N(3, "Ore2")], new()
        {
            [1] = Gather(Spot(1, "Z", cls: GatherClass.Botanist)),
            [2] = Gather(Spot(1, "Z", cls: GatherClass.Miner)),
            [3] = Gather(Spot(1, "Z", cls: GatherClass.Miner)),
        });
        Assert.Equal([GatherClass.Miner, GatherClass.Botanist], Assert.Single(groups).Classes);
    }

    [Fact]
    public void EverythingOwnedHasOnlyDoneAndCrystals()
    {
        var groups = Build([N(1, "Ore", 2), N(2, "Shard", 2), N(3, "Vend", 2)],
                           new() { [1] = Gather(Spot(1, "Z")), [2] = Crystal, [3] = Vendor },
                           new() { [1] = 2, [3] = 5 });
        Assert.Equal([SourceKind.Crystals, SourceKind.Done], groups.Select(g => g.Kind).ToArray());
    }

    [Fact]
    public void EmptyInputHasNoGroups()
        => Assert.Empty(Build([], new()));
}
