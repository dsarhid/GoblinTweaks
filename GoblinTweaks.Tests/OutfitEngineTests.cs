using System.Diagnostics;

namespace GoblinTweaks.Tests;

public class OutfitEngineTests
{
    private static readonly GlamourSlot[] Armor = [GlamourSlot.Head, GlamourSlot.Body, GlamourSlot.Hands, GlamourSlot.Legs, GlamourSlot.Feet];

    private static readonly string[] Hexes =
    [
        "C83232", "32A0C8", "32C832", "E0C040", "8040C0", "F0F0F0", "202020", "A06030", "40C0A0", "D070A0",
        "6080E0", "908070", "C0C0C0", "503020", "E08040", "3050A0", "70A040", "B03060", "604080", "A0A040",
    ];

    private static readonly CharacterPalette Palette = new(
        Skin: Rgb.FromHex("E8C0A0"), Hair: Rgb.FromHex("A02828"), Highlights: Rgb.FromHex("C04040"),
        LeftEye: Rgb.FromHex("3080C0"), RightEye: Rgb.FromHex("3080C0"), Lips: Rgb.FromHex("C06070"), FacePaint: null);

    private static readonly List<Stain> Stains =
    [
        new(1, "White", Rgb.FromHex("F0F0F0")),
        new(2, "Black", Rgb.FromHex("141414")),
        new(3, "Red", Rgb.FromHex("C81E1E")),
        new(4, "Blue", Rgb.FromHex("1E50C8")),
    ];

    private static CatalogItem Item(uint id, GlamourSlot slot, string hex, string? name = null, byte dyes = 0,
                                    SlotMask blocks = SlotMask.None, StyleFamily family = StyleFamily.Any, uint setKey = 0,
                                    ShapeTag shapes = ShapeTag.None)
        => new(id, name ?? $"Item {id}", name ?? $"Item {id}", slot, blocks, ModelKey: id, IconId: id,
               Rgb.FromHex(hex), Rgb.FromHex(hex), dyes, family, RaceCategory: 1, SetKey: setKey, Shapes: shapes);

    /// <summary>20 items per slot in varied colors, for every slot given.</summary>
    private static List<CatalogItem> Catalog(params GlamourSlot[] slots)
    {
        var items = new List<CatalogItem>();
        foreach (var slot in slots)
            for (var i = 0; i < Hexes.Length; i++)
                items.Add(Item((uint)((int)slot * 1000 + i + 1), slot, Hexes[i], dyes: (byte)(i % 3)));
        return items;
    }

    private static GenerateOptions Options(SlotMask? slots = null, bool hasOffHand = true, bool useDyes = false, Theme? theme = null,
                                           Dictionary<GlamourSlot, OutfitPiece>? locked = null, int count = 4,
                                           string[]? costumes = null, HashSet<uint>? disliked = null, HashSet<uint>? liked = null,
                                           Dictionary<uint, int>? recentItems = null, Dictionary<uint, int>? recentSets = null,
                                           Theme[]? styles = null, bool matchRole = false, bool useMyColors = true,
                                           ShapeTag wantedShapes = ShapeTag.None, ShapeClash[]? clashes = null)
        => new(slots ?? SlotMask.Armor | SlotMask.Weapons, hasOffHand, useDyes, theme, [], locked ?? [], count)
        {
            CostumeWords = costumes ?? [],
            Disliked = disliked ?? [],
            Liked = liked ?? [],
            RecentItems = recentItems ?? [],
            RecentSets = recentSets ?? [],
            StyleThemes = styles ?? [],
            MatchRole = matchRole,
            UseCharacterColors = useMyColors,
            WantedShapes = wantedShapes,
            Clashes = clashes ?? [],
        };

    private static IReadOnlyList<OutfitProposal> Run(IReadOnlyList<CatalogItem> catalog, GenerateOptions options, int seed = 7)
        => OutfitEngine.Generate(catalog, Stains, Palette, options, new Random(seed));

    [Fact]
    public void Generate_ReturnsCountOutfits()
    {
        var catalog = Catalog(Armor);
        Assert.Equal(4, Run(catalog, Options(slots: SlotMask.Armor, count: 4)).Count);
        Assert.Equal(9, Run(catalog, Options(slots: SlotMask.Armor, count: 9)).Count);
        Assert.Equal(15, Run(catalog, Options(slots: SlotMask.Armor, count: 20)).Count);
        Assert.Equal(3, Run(catalog, Options(slots: SlotMask.Armor, count: 1)).Count);
    }

    [Fact]
    public void Generate_OutfitsAreDistinct()
    {
        var proposals = Run(Catalog(Armor), Options(slots: SlotMask.Armor, count: 5));
        for (var i = 0; i < proposals.Count; i++)
            for (var j = i + 1; j < proposals.Count; j++)
            {
                var a = proposals[i].Pieces.Values.Select(p => p.ItemId).ToHashSet();
                var b = proposals[j].Pieces.Values.Select(p => p.ItemId).ToHashSet();
                var shared = a.Intersect(b).Count();
                Assert.True(shared * 2 <= Math.Min(a.Count, b.Count), $"outfits {i} and {j} share {shared} pieces");
            }
    }

    [Fact]
    public void Generate_KeepsLockedPiece()
    {
        var catalog = Catalog(Armor);
        var locked = new Dictionary<GlamourSlot, OutfitPiece> { [GlamourSlot.Head] = new(5, 3, 0) };
        foreach (var proposal in Run(catalog, Options(slots: SlotMask.Armor, locked: locked)))
            Assert.Equal(new OutfitPiece(5, 3, 0), proposal.Pieces[GlamourSlot.Head]);
    }

    [Fact]
    public void Generate_BodyBlocksLegs_NoLegs()
    {
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "C83232", blocks: SlotMask.Legs),
            Item(2, GlamourSlot.Legs, "C83232"),
            Item(3, GlamourSlot.Feet, "C83232"),
        };
        foreach (var proposal in Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs | SlotMask.Feet)))
        {
            Assert.Equal(1u, proposal.Pieces[GlamourSlot.Body].ItemId);
            Assert.False(proposal.Pieces.ContainsKey(GlamourSlot.Legs));
            Assert.DoesNotContain(proposal.Reasons, r => r.Code == ReasonCode.SlotEmpty && r.Slot == GlamourSlot.Legs);
        }
    }

    [Fact]
    public void Generate_NoDyes_StainsZero()
    {
        foreach (var proposal in Run(Catalog(Armor), Options(slots: SlotMask.Armor, useDyes: false)))
            Assert.All(proposal.Pieces.Values, piece => Assert.Equal((0, 0), (piece.Stain0, piece.Stain1)));
    }

    [Fact]
    public void Generate_WithDyes_PicksStainNearAnchor()
    {
        var palette = Palette with { Hair = Rgb.FromHex("FFFFFF") };
        var catalog = new List<CatalogItem> { Item(1, GlamourSlot.Body, "806040", dyes: 1), Item(2, GlamourSlot.Body, "406080", dyes: 2) };
        var proposals = OutfitEngine.Generate(catalog, Stains, palette, Options(slots: SlotMask.Body, useDyes: true), new Random(1));
        var hairOutfit = proposals.Single(p => p.Reasons.Any(r => r.Code == ReasonCode.EchoesHair));
        Assert.Equal(1, hairOutfit.Pieces[GlamourSlot.Body].Stain0);
    }

    [Fact]
    public void Generate_AccessoriesOff_NoAccessorySlots()
    {
        var catalog = Catalog([.. Armor, GlamourSlot.Ears, GlamourSlot.Neck, GlamourSlot.Wrists, GlamourSlot.Ring, GlamourSlot.Facewear]);
        foreach (var proposal in Run(catalog, Options(slots: SlotMask.Armor)))
        {
            Assert.All(proposal.Pieces.Keys, slot => Assert.True(SlotMask.Armor.Has(slot), $"{slot} proposed"));
            Assert.Equal(5, proposal.Pieces.Count);
        }
    }

    [Fact]
    public void Generate_NoOffHand_OmitsOffHand()
    {
        var catalog = Catalog([.. Armor, GlamourSlot.MainHand, GlamourSlot.OffHand]);
        foreach (var proposal in Run(catalog, Options(hasOffHand: false)))
        {
            Assert.True(proposal.Pieces.ContainsKey(GlamourSlot.MainHand));
            Assert.False(proposal.Pieces.ContainsKey(GlamourSlot.OffHand));
            Assert.DoesNotContain(proposal.Reasons, r => r.Slot == GlamourSlot.OffHand && r.Code == ReasonCode.SlotEmpty);
        }
        Assert.All(Run(catalog, Options(hasOffHand: true)), p => Assert.True(p.Pieces.ContainsKey(GlamourSlot.OffHand)));
    }

    [Fact]
    public void Generate_EmptySlotCandidates_LeavesSlotEmpty()
    {
        var catalog = Catalog(GlamourSlot.Head, GlamourSlot.Body, GlamourSlot.Hands, GlamourSlot.Legs);
        var proposals = Run(catalog, Options(slots: SlotMask.Armor));
        Assert.Equal(4, proposals.Count);
        foreach (var proposal in proposals)
        {
            Assert.False(proposal.Pieces.ContainsKey(GlamourSlot.Feet));
            Assert.Contains(proposal.Reasons, r => r.Code == ReasonCode.SlotEmpty && r.Slot == GlamourSlot.Feet);
        }

        // Nothing at all: still 3–5 empty proposals, never an exception.
        Assert.All(Run([], Options(slots: SlotMask.Armor)), p => Assert.Empty(p.Pieces));
    }

    [Fact]
    public void Generate_ThemePrefersMatching()
    {
        var pirate = new Theme("pirate", ["Corsair", "Buccaneer"], []);
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "806040", name: "Plain Coat"),
            Item(2, GlamourSlot.Body, "806040", name: "Corsair's Coat"),
        };
        var proposals = Run(catalog, Options(slots: SlotMask.Body, theme: pirate, count: 5));
        Assert.True(proposals.Count(p => p.Pieces[GlamourSlot.Body].ItemId == 2) >= 3);
        Assert.Contains(proposals, p => p.Reasons.Any(r => r.Code == ReasonCode.MatchesTheme));
    }

    [Fact]
    public void Generate_WantedShapePreferred()
    {
        // Two bodies alike in every way but the shape: asked for a cape, the cape wins most outfits.
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "806040", name: "Plain Coat", shapes: ShapeTag.Coat),
            Item(2, GlamourSlot.Body, "806040", name: "Plain Mantle", shapes: ShapeTag.Cape),
        };
        var proposals = Run(catalog, Options(slots: SlotMask.Body, count: 5, wantedShapes: ShapeTag.Cape));
        Assert.True(proposals.Count(p => p.Pieces[GlamourSlot.Body].ItemId == 2) >= 3);
    }

    [Fact]
    public void Generate_ClashingShapesAvoided()
    {
        // Every body is heavy armor; half the legs are swimwear, in the same colors as the other half.
        var catalog = new List<CatalogItem>();
        for (var i = 0; i < 10; i++)
        {
            catalog.Add(Item((uint)(100 + i), GlamourSlot.Body, Hexes[i], shapes: ShapeTag.HeavyArmor));
            catalog.Add(Item((uint)(200 + i), GlamourSlot.Legs, Hexes[i], shapes: ShapeTag.Swimsuit));
            catalog.Add(Item((uint)(300 + i), GlamourSlot.Legs, Hexes[i], shapes: ShapeTag.Pants));
        }
        static bool Swimwear(OutfitProposal p) => p.Pieces.TryGetValue(GlamourSlot.Legs, out var legs) && legs.ItemId is >= 200 and < 300;
        var clash = new ShapeClash(ShapeTag.HeavyArmor, ShapeTag.Swimsuit, 0.6f);
        Assert.Contains(Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 15)), Swimwear);   // without the rule they do mix
        Assert.DoesNotContain(Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 15, clashes: [clash])), Swimwear);
    }

    [Fact]
    public void Generate_UntaggedItem_NotPenalized()
    {
        // Without shapes on the items, the clash rules and a wanted shape change nothing.
        var catalog = Catalog(Armor);
        var clash = new ShapeClash(ShapeTag.HeavyArmor, ShapeTag.Swimsuit, 0.6f);
        var plain = Run(catalog, Options(slots: SlotMask.Armor, count: 6), seed: 3);
        var shaped = Run(catalog, Options(slots: SlotMask.Armor, count: 6, wantedShapes: ShapeTag.Cape, clashes: [clash]), seed: 3);
        Assert.Equal(plain.Count, shaped.Count);
        for (var i = 0; i < plain.Count; i++)
            Assert.Equal(plain[i].Pieces.OrderBy(p => p.Key), shaped[i].Pieces.OrderBy(p => p.Key));
    }

    [Fact]
    public void Generate_SameSeed_SameResult()
    {
        var catalog = Catalog([.. Armor, GlamourSlot.MainHand]);
        var a = Run(catalog, Options(useDyes: true, count: 5), seed: 42);
        var b = Run(catalog, Options(useDyes: true, count: 5), seed: 42);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i].Pieces.OrderBy(p => p.Key), b[i].Pieces.OrderBy(p => p.Key));
    }

    [Fact]
    public void Generate_FullCatalog_UnderOneSecond()
    {
        var slots = Enum.GetValues<GlamourSlot>();
        var random = new Random(3);
        var catalog = new List<CatalogItem>();
        for (uint id = 1; id <= 8000; id++)
            catalog.Add(Item(id, slots[(int)(id % (uint)slots.Length)],
                             $"{random.Next(256):X2}{random.Next(256):X2}{random.Next(256):X2}", dyes: (byte)(id % 3)));

        var all = Enum.GetValues<GlamourSlot>().Aggregate(SlotMask.None, (mask, slot) => mask | slot.Mask());
        Run(catalog, Options(slots: all, useDyes: true, count: 15)); // warm-up (JIT)
        var watch = Stopwatch.StartNew();
        var proposals = Run(catalog, Options(slots: all, useDyes: true, count: 15));
        watch.Stop();
        Assert.Equal(15, proposals.Count);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Generate_FifteenOutfits_UseTenDifferentAnchors()
    {
        var proposals = Run(Catalog(Armor), Options(slots: SlotMask.Armor, count: 15));
        Assert.Equal(15, proposals.Count);
        Assert.Equal(10, proposals.Select(p => p.Reasons[0].Code).Distinct().Count());
    }

    [Fact]
    public void Generate_ThemedPiece_NotRepeatedInEveryOutfit()
    {
        var samurai = new Theme("samurai", ["Katana"], []);
        var catalog = new List<CatalogItem> { Item(1, GlamourSlot.MainHand, "806040", name: "Steel Katana") };
        for (uint id = 2; id <= 6; id++)
            catalog.Add(Item(id, GlamourSlot.MainHand, "806040", name: $"Plain Blade {id}"));

        var proposals = Run(catalog, Options(slots: SlotMask.MainHand, theme: samurai, count: 15));
        var katanas = proposals.Count(p => p.Pieces[GlamourSlot.MainHand].ItemId == 1);
        Assert.InRange(katanas, 3, 8);
    }

    [Fact]
    public void Generate_BodySet_PullsMatchingPieces()
    {
        // One body, of set 77. The legs and hands of set 77 are a slightly different red and have the highest ids,
        // so only the set can make them win over the others, which match the hair exactly.
        var catalog = new List<CatalogItem> { Item(1, GlamourSlot.Body, "A02828", setKey: 77) };
        catalog.Add(Item(900, GlamourSlot.Legs, "B03838", setKey: 77));
        catalog.Add(Item(901, GlamourSlot.Hands, "B03838", setKey: 77));
        for (uint id = 10; id < 30; id++)
        {
            catalog.Add(Item(id, GlamourSlot.Legs, "A02828"));
            catalog.Add(Item(id + 100, GlamourSlot.Hands, "A02828"));
        }

        var first = Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs | SlotMask.Hands, count: 3))[0];
        Assert.Equal(900u, first.Pieces[GlamourSlot.Legs].ItemId);
        Assert.Equal(901u, first.Pieces[GlamourSlot.Hands].ItemId);
        Assert.Contains(first.Reasons, r => r.Code == ReasonCode.SetBase);
    }

    [Fact]
    public void Generate_BodySet_AtMostThreePieces()
    {
        var catalog = new List<CatalogItem> { Item(1, GlamourSlot.Body, "A02828", setKey: 77) };
        foreach (var slot in new[] { GlamourSlot.Head, GlamourSlot.Hands, GlamourSlot.Legs, GlamourSlot.Feet })
        {
            catalog.Add(Item((uint)((int)slot * 1000), slot, "A02828", setKey: 77));
            for (uint i = 1; i <= 10; i++)
                catalog.Add(Item((uint)((int)slot * 1000) + i, slot, "A02828"));
        }

        foreach (var proposal in Run(catalog, Options(slots: SlotMask.Armor, count: 3)))
        {
            var fromSet = proposal.Pieces.Values.Count(p => catalog.First(c => c.Id == p.ItemId).SetKey == 77);
            Assert.True(fromSet <= 3, $"{fromSet} pieces of the body's set");
        }
    }

    [Fact]
    public void Generate_Costume_OnlyWhenThemeAsksForIt()
    {
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "A02828", name: "Reindeer Suit"),
            Item(2, GlamourSlot.Body, "808080", name: "Plain Coat"),
        };
        string[] costumes = ["Reindeer"];

        Assert.All(Run(catalog, Options(slots: SlotMask.Body, costumes: costumes, count: 5)),
                   p => Assert.Equal(2u, p.Pieces[GlamourSlot.Body].ItemId));

        var festive = new Theme("winter", ["Reindeer"], []);
        Assert.Contains(Run(catalog, Options(slots: SlotMask.Body, theme: festive, costumes: costumes, count: 5)),
                        p => p.Pieces[GlamourSlot.Body].ItemId == 1);
    }

    [Fact]
    public void Generate_DislikedPiece_NeverProposed()
    {
        var catalog = Catalog(Armor);
        var favorite = Run(catalog, Options(slots: SlotMask.Armor, count: 3))[0].Pieces[GlamourSlot.Body].ItemId;

        var proposals = Run(catalog, Options(slots: SlotMask.Armor, count: 15, disliked: [favorite]));
        Assert.DoesNotContain(proposals, p => p.Pieces.Values.Any(piece => piece.ItemId == favorite));
    }

    [Fact]
    public void Generate_LikedPiece_Preferred()
    {
        // Two bodies of the same color; the liked one has the higher id, so the tie-break alone would not pick it.
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "A02828"),
            Item(2, GlamourSlot.Body, "A02828"),
        };
        var first = Run(catalog, Options(slots: SlotMask.Body, count: 3, liked: [2]))[0];
        Assert.Equal(2u, first.Pieces[GlamourSlot.Body].ItemId);
    }

    /// <summary>
    /// 200 dyeable bodies of varied colors, each its own set. With dyes on every one is dyed to the outfit's color,
    /// so only their own colors (the same every press) rank them: what made the same few come back every time.
    /// </summary>
    private static List<CatalogItem> ManyBodies()
    {
        var random = new Random(5);
        var catalog = new List<CatalogItem>();
        for (uint id = 1; id <= 200; id++)
            catalog.Add(Item(id, GlamourSlot.Body, $"{random.Next(256):X2}{random.Next(256):X2}{random.Next(256):X2}", dyes: 2, setKey: 1000 + id));
        return catalog;
    }

    [Fact]
    public void Generate_PressesRememberingTheLastThree_KeepBringingNewBodies()
    {
        // As the tweak does: every press is told what the last three presses showed.
        // Pieces older than three presses may come back (they are the ones that fit best), but none of the last three.
        var catalog = ManyBodies();
        var presses = new List<List<uint>>();
        for (var seed = 1; seed <= 6; seed++)
        {
            var recentItems = presses.TakeLast(3).SelectMany(ids => ids).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var recentSets = recentItems.ToDictionary(pair => 1000 + pair.Key, pair => pair.Value);
            var bodies = Run(catalog, Options(slots: SlotMask.Body, useDyes: true, count: 15, recentItems: recentItems, recentSets: recentSets), seed)
                .Select(p => p.Pieces[GlamourSlot.Body].ItemId).ToList();
            var repeated = bodies.Count(recentItems.ContainsKey);
            Assert.True(repeated <= 1, $"press {seed} repeats {repeated} bodies of the three before");
            presses.Add(bodies);
        }
        Assert.InRange(presses.Take(4).SelectMany(ids => ids).Distinct().Count(), 58, 60);
    }

    [Fact]
    public void Generate_RecentSets_Avoided()
    {
        var catalog = ManyBodies();
        var first = Run(catalog, Options(slots: SlotMask.Body, useDyes: true, count: 15), seed: 1);
        var recentItems = first.Select(p => p.Pieces[GlamourSlot.Body].ItemId).ToDictionary(id => id, _ => 1);
        var recentSets = recentItems.Keys.ToDictionary(id => 1000 + id, _ => 1);

        var second = Run(catalog, Options(slots: SlotMask.Body, useDyes: true, count: 15,
                                          recentItems: recentItems, recentSets: recentSets), seed: 2);
        var repeated = second.Count(p => recentItems.ContainsKey(p.Pieces[GlamourSlot.Body].ItemId));
        Assert.True(repeated <= 1, $"{repeated} bodies of the previous press came back");
    }

    [Fact]
    public void Generate_AnchorOffset_StartsFurtherOnTheList()
    {
        var catalog = Catalog(Armor);
        var plain = Run(catalog, Options(slots: SlotMask.Armor, count: 6)).Select(p => p.Reasons[0].Code).ToList();
        var shifted = Run(catalog, Options(slots: SlotMask.Armor, count: 3) with { AnchorOffset = 3 })
            .Select(p => p.Reasons[0].Code).ToList();
        Assert.Equal(plain.Skip(3).Take(3), shifted);
    }

    [Fact]
    public void Generate_WithDyes_DyeablePiecesNotRankedByTheirOwnColor()
    {
        // A dyeable piece takes the outfit's dye, so its own color must not keep it out: across a few presses
        // (no memory between them) far more than the same top few should come up.
        var catalog = ManyBodies();
        var seen = new HashSet<uint>();
        for (var seed = 1; seed <= 6; seed++)
            foreach (var proposal in Run(catalog, Options(slots: SlotMask.Body, useDyes: true, count: 15), seed))
                seen.Add(proposal.Pieces[GlamourSlot.Body].ItemId);
        Assert.True(seen.Count >= 75, $"only {seen.Count} different bodies in 6 presses");
    }

    [Fact]
    public void Generate_EveryPieceGoesWithAnotherByDesignOrColor()
    {
        // Undyed pieces in every color: each chosen piece must share a style (set or theme) with another piece of
        // its outfit, or go with one of them in color.
        var catalog = Catalog(Armor);
        var byId = catalog.ToDictionary(item => item.Id);
        for (var seed = 1; seed <= 5; seed++)
            foreach (var proposal in Run(catalog, Options(slots: SlotMask.Armor, count: 15), seed))
            {
                var items = proposal.Pieces.Values.Select(p => byId[p.ItemId]).ToList();
                foreach (var item in items)
                {
                    var related = items.Any(other => other != item
                        && ((item.SetKey != 0 && item.SetKey == other.SetKey)
                            || ColorMath.Harmony(item.Primary, other.Primary) >= 0.6f));
                    Assert.True(related, $"seed {seed}: {item.Name} goes with nothing else in its outfit");
                }
            }
    }

    [Fact]
    public void Generate_MoreLockedPieces_LessVariation()
    {
        var catalog = Catalog(Armor);
        var locked = new Dictionary<GlamourSlot, OutfitPiece>
        {
            [GlamourSlot.Body] = new(1001, 0, 0), [GlamourSlot.Legs] = new(3001, 0, 0),
            [GlamourSlot.Head] = new(1, 0, 0), [GlamourSlot.Hands] = new(2001, 0, 0),
        };

        int DistinctFeet(Dictionary<GlamourSlot, OutfitPiece>? held)
            => Enumerable.Range(1, 40)
                .Select(seed => Run(catalog, Options(slots: SlotMask.Armor, count: 3, locked: held), seed)[0].Pieces[GlamourSlot.Feet].ItemId)
                .Distinct().Count();

        var free = DistinctFeet(null);
        var held = DistinctFeet(locked);
        Assert.True(held < free, $"feet: {held} different with four pieces locked, {free} with none");
        Assert.True(held <= 4, $"feet: {held} different with four pieces locked");
    }

    [Fact]
    public void Generate_SharedStyle_BeatsSameColorWithoutIt()
    {
        // No theme chosen. Two legs of the same clashing color; only one shares a style with the body.
        var gothic = new Theme("gothic", ["Gothic"], []);
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "A02828", name: "Gothic Coat"),
            Item(2, GlamourSlot.Legs, "40C040", name: "Plain Trousers"),
            Item(3, GlamourSlot.Legs, "40C040", name: "Gothic Breeches"),
        };
        var gothicLegs = Enumerable.Range(1, 20).Count(seed =>
            Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 3, styles: [gothic]), seed)[0].Pieces[GlamourSlot.Legs].ItemId == 3);
        Assert.True(gothicLegs >= 18, $"the gothic legs won {gothicLegs} of 20 presses");
    }

    [Fact]
    public void Generate_SameRole_NotPreferred()
    {
        // A samurai may wear tank armor, a tank the loose clothes of a viper: the role a piece was made for does
        // not make it go better with the others. Two legs alike but for their role: neither should win every time.
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "A02828", family: StyleFamily.Tank),
            Item(2, GlamourSlot.Legs, "40C040", family: StyleFamily.Tank),
            Item(3, GlamourSlot.Legs, "40C040", family: StyleFamily.Melee),
        };
        int TankLegs(bool matchRole) => Enumerable.Range(1, 20).Count(seed =>
            Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 3, matchRole: matchRole), seed)[0].Pieces[GlamourSlot.Legs].ItemId == 2);

        Assert.InRange(TankLegs(matchRole: false), 4, 16);
        Assert.DoesNotContain(Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 3)),
                              p => p.Reasons.Any(r => r.Code == ReasonCode.SameFamily));
    }

    [Fact]
    public void Generate_MatchRoleOn_PrefersSameRole()
    {
        var catalog = new List<CatalogItem>
        {
            Item(1, GlamourSlot.Body, "A02828", family: StyleFamily.Tank),
            Item(2, GlamourSlot.Legs, "40C040", family: StyleFamily.Tank),
            Item(3, GlamourSlot.Legs, "40C040", family: StyleFamily.Melee),
        };
        var tankLegs = Enumerable.Range(1, 20).Count(seed =>
            Run(catalog, Options(slots: SlotMask.Body | SlotMask.Legs, count: 3, matchRole: true), seed)[0].Pieces[GlamourSlot.Legs].ItemId == 2);
        Assert.True(tankLegs >= 18, $"the tank legs won {tankLegs} of 20 presses");
    }

    [Fact]
    public void Generate_WithoutMyColors_TheCharacterDoesNotMatter()
    {
        var other = new CharacterPalette(
            Skin: Rgb.FromHex("402818"), Hair: Rgb.FromHex("E0E0F0"), Highlights: Rgb.FromHex("80F080"),
            LeftEye: Rgb.FromHex("F0C020"), RightEye: Rgb.FromHex("F0C020"), Lips: Rgb.FromHex("803040"), FacePaint: null);
        var catalog = Catalog(Armor);

        IReadOnlyList<OutfitProposal> With(CharacterPalette palette, bool useMyColors)
            => OutfitEngine.Generate(catalog, Stains, palette, Options(slots: SlotMask.Armor, useDyes: true, count: 6, useMyColors: useMyColors), new Random(9));

        static string Key(IReadOnlyList<OutfitProposal> proposals)
            => string.Join(";", proposals.Select(p => string.Join(",", p.Pieces.OrderBy(x => x.Key).Select(x => $"{x.Value.ItemId}/{x.Value.Stain0}/{x.Value.Stain1}"))));

        Assert.Equal(Key(With(Palette, useMyColors: false)), Key(With(other, useMyColors: false)));
        Assert.NotEqual(Key(With(Palette, useMyColors: true)), Key(With(other, useMyColors: true)));
        Assert.All(With(Palette, useMyColors: false), p => Assert.Equal(ReasonCode.OwnPalette, p.Reasons[0].Code));
    }
}
