namespace GoblinTweaks.Tests;

public class CraftMathTests
{
    // Items
    private const uint Ore = 1, Ingot = 2, Lumber = 3, Log = 4, LoopA = 5, LoopB = 6;
    // Recipes
    private const uint IngotRecipe = 20, LumberRecipe = 30, Spear = 100, Lance = 101, LoopARecipe = 50, LoopBRecipe = 60;

    private sealed class FakeData : ICraftData
    {
        public readonly Dictionary<uint, CraftRecipe> Recipes = new();
        public readonly Dictionary<uint, uint>        ByResult = new();
        public readonly Dictionary<uint, int>         Stock = new();

        public CraftRecipe? Recipe(uint recipeId) => Recipes.GetValueOrDefault(recipeId);
        public uint RecipeFor(uint itemId)        => ByResult.GetValueOrDefault(itemId);
        public int  Owned(uint itemId)            => Stock.GetValueOrDefault(itemId);
    }

    private static CraftPart P(uint id, int amount) => new(id, $"Item{id}", id * 10, amount);

    // Ingot: 3 Ore → 1. Lumber: 2 Log → 3. Spear: 2 Ingot + 1 Lumber. Lance: 1 Ingot + 4 Lumber.
    private static FakeData Data()
    {
        var d = new FakeData();
        d.Recipes[IngotRecipe]  = new CraftRecipe(1, [P(Ore, 3)]);
        d.Recipes[LumberRecipe] = new CraftRecipe(3, [P(Log, 2)]);
        d.Recipes[Spear]        = new CraftRecipe(1, [P(Ingot, 2), P(Lumber, 1)]);
        d.Recipes[Lance]        = new CraftRecipe(1, [P(Ingot, 1), P(Lumber, 4)]);
        d.ByResult[Ingot]  = IngotRecipe;
        d.ByResult[Lumber] = LumberRecipe;
        return d;
    }

    private static CartItem Item(uint recipeId, int count) => new() { RecipeId = recipeId, Count = count };

    private static Dictionary<uint, int> AsMap(List<Need> needs) => needs.ToDictionary(n => n.ItemId, n => n.Amount);

    [Fact]
    public void Direct_SumsAcrossRecipesTimesCount()
    {
        var needs = CraftMath.Direct([Item(Spear, 2), Item(Lance, 1)], Data());

        Assert.Equal(new uint[] { Ingot, Lumber }, needs.Select(n => n.ItemId));
        Assert.Equal(5, needs[0].Amount);
        Assert.Equal(6, needs[1].Amount);
        Assert.Equal("Item2", needs[0].Name);
        Assert.Equal(20u, needs[0].IconId);
    }

    [Fact]
    public void Direct_SkipsUnknownRecipes()
    {
        var needs = CraftMath.Direct([Item(999, 3), Item(Spear, 1)], Data());

        Assert.Equal(new uint[] { Ingot, Lumber }, needs.Select(n => n.ItemId));
    }

    [Fact]
    public void Crafts_RoundsUpAndDeducts()
    {
        Assert.Equal(2, CraftMath.Crafts(5, 0, 3, true));
        Assert.Equal(1, CraftMath.Crafts(5, 4, 3, true));
        Assert.Equal(0, CraftMath.Crafts(5, 9, 3, true));
        Assert.Equal(2, CraftMath.Crafts(5, 9, 3, false));
    }

    [Fact]
    public void Children_UsesSummedNeed()
    {
        var children = CraftMath.Children(Lumber, 5, Data(), true);

        Assert.Equal(4, AsMap(children)[Log]);
    }

    [Fact]
    public void Children_EmptyWhenNotCraftableOrNothingToCraft()
    {
        var data = Data();
        data.Stock[Lumber] = 10;

        Assert.Empty(CraftMath.Children(Ore, 5, data, true));
        Assert.Empty(CraftMath.Children(Lumber, 5, data, true));
        Assert.Equal(4, AsMap(CraftMath.Children(Lumber, 5, data, false))[Log]);
    }

    [Fact]
    public void BaseMaterials_DeductsOwnedIntermediates()
    {
        var data = Data();
        data.Stock[Ingot] = 1;

        var map = AsMap(CraftMath.BaseMaterials([Item(Spear, 2)], data, true));

        Assert.Equal(9, map[Ore]);
        Assert.Equal(2, map[Log]);
        Assert.False(map.ContainsKey(Ingot));
        Assert.False(map.ContainsKey(Lumber));
    }

    [Fact]
    public void BaseMaterials_NoDeductIgnoresStock()
    {
        var data = Data();
        data.Stock[Ingot] = 1;

        var map = AsMap(CraftMath.BaseMaterials([Item(Spear, 2)], data, false));

        Assert.Equal(12, map[Ore]);
        Assert.Equal(2, map[Log]);
    }

    [Fact]
    public void BaseMaterials_LeftoversServeNextRecipe()
    {
        var map = AsMap(CraftMath.BaseMaterials([Item(Spear, 1), Item(Lance, 1)], Data(), true));

        Assert.Equal(4, map[Log]);
        Assert.Equal(9, map[Ore]);
    }

    [Fact]
    public void BaseMaterials_NoDeductStillUsesLeftovers()
    {
        var data = Data();
        data.Stock[Lumber] = 50;

        var map = AsMap(CraftMath.BaseMaterials([Item(Spear, 1), Item(Lance, 1)], data, false));

        Assert.Equal(4, map[Log]);
    }

    // Items for the Breakdown tests.
    private const uint Whetstone = 10, Annite = 11, WindCrystal = 12, Bolt = 13, Plank = 14, Blade = 15;
    private const uint WhetstoneRecipe = 210, BoltRecipe = 220, PlankRecipe = 230, BladeRecipe = 240;
    private const uint SwordA = 200, SwordB = 201, Chain = 300;

    // Whetstone: 1 Annite + 1 Wind Crystal → 1. Sword A: 2 Whetstone. Sword B: 1 Whetstone.
    private static FakeData WhetstoneData()
    {
        var d = new FakeData();
        d.Recipes[WhetstoneRecipe] = new CraftRecipe(1, [P(Annite, 1), P(WindCrystal, 1)]);
        d.Recipes[SwordA]          = new CraftRecipe(1, [P(Whetstone, 2)]);
        d.Recipes[SwordB]          = new CraftRecipe(1, [P(Whetstone, 1)]);
        d.ByResult[Whetstone] = WhetstoneRecipe;
        return d;
    }

    [Fact]
    public void BreakdownBaseEqualsBaseMaterials()
    {
        var data = Data();
        data.Stock[Ingot] = 1;
        CartItem[] cart = [Item(Spear, 2), Item(Lance, 1)];

        foreach (var deduct in new[] { true, false })
            Assert.Equal(CraftMath.BaseMaterials(cart, data, deduct), CraftMath.Breakdown(cart, data, deduct).Base);
    }

    [Fact]
    public void BreakdownCountsCraftsOfIntermediates()
    {
        var result = CraftMath.Breakdown([Item(SwordA, 1)], WhetstoneData(), true);

        Assert.Equal([new Craft(Whetstone, $"Item{Whetstone}", Whetstone * 10, 2, 1)], result.Crafts);
    }

    [Fact]
    public void BreakdownSumsCraftsAcrossRecipes()
    {
        var result = CraftMath.Breakdown([Item(SwordB, 1), Item(SwordB, 1)], WhetstoneData(), true);

        Assert.Equal(2, Assert.Single(result.Crafts).Crafts);
    }

    [Fact]
    public void BreakdownUsesLeftoversOfACraft()
    {
        var d = new FakeData();
        d.Recipes[BoltRecipe] = new CraftRecipe(3, [P(Ore, 1)]);
        d.Recipes[SwordA]     = new CraftRecipe(1, [P(Bolt, 2)]);
        d.Recipes[SwordB]     = new CraftRecipe(1, [P(Bolt, 2)]);
        d.ByResult[Bolt] = BoltRecipe;

        var result = CraftMath.Breakdown([Item(SwordA, 1), Item(SwordB, 1)], d, true);

        Assert.Equal(2, Assert.Single(result.Crafts).Crafts); // 4 needed, 6 made
    }

    [Fact]
    public void BreakdownDeductsOwnedIntermediates()
    {
        var data = WhetstoneData();
        data.Stock[Whetstone] = 2;

        Assert.Empty(CraftMath.Breakdown([Item(SwordA, 1)], data, true).Crafts);
        Assert.Equal(2, Assert.Single(CraftMath.Breakdown([Item(SwordA, 1)], data, false).Crafts).Crafts);
    }

    [Fact]
    public void BreakdownOrdersDeepestFirst()
    {
        var d = new FakeData();
        d.Recipes[Chain]       = new CraftRecipe(1, [P(Blade, 1)]);
        d.Recipes[BladeRecipe] = new CraftRecipe(1, [P(Plank, 1)]);
        d.Recipes[PlankRecipe] = new CraftRecipe(1, [P(Ore, 1)]);
        d.ByResult[Blade] = BladeRecipe;
        d.ByResult[Plank] = PlankRecipe;

        var crafts = CraftMath.Breakdown([Item(Chain, 1)], d, true).Crafts;

        Assert.Equal(new[] { Plank, Blade }, crafts.Select(c => c.ItemId));
        Assert.Equal(new[] { 2, 1 }, crafts.Select(c => c.Depth));
    }

    [Fact]
    public void BaseMaterials_StopsOnLoop()
    {
        var data = new FakeData();
        data.Recipes[LoopARecipe] = new CraftRecipe(1, [P(LoopB, 1)]);
        data.Recipes[LoopBRecipe] = new CraftRecipe(1, [P(LoopA, 1)]);
        data.ByResult[LoopA] = LoopARecipe;
        data.ByResult[LoopB] = LoopBRecipe;

        var needs = CraftMath.BaseMaterials([Item(LoopARecipe, 1)], data, true);

        Assert.Single(needs);
        Assert.Contains(needs[0].ItemId, new[] { LoopA, LoopB });
    }

    [Fact]
    public void BaseMaterials_StopsAtMaxDepth()
    {
        // Item 201 is made of 202, 202 of 203 … 208 of 209: a chain of 8 sub-recipes, no loop.
        var data = new FakeData();
        for (uint item = 201; item <= 208; item++)
        {
            data.Recipes[item + 1000] = new CraftRecipe(1, [P(item + 1, 1)]);
            data.ByResult[item]       = item + 1000;
        }
        data.Recipes[Spear] = new CraftRecipe(1, [P(201, 1)]);

        var needs = CraftMath.BaseMaterials([Item(Spear, 1)], data, true);

        // The root's ingredients enter at depth 1 and sub-recipes are followed while depth < 6: 201 … 205 are broken
        // down and 206 comes out as a base material, though it could still be crafted.
        Assert.Single(needs);
        Assert.Equal(206u, needs[0].ItemId);
    }

    [Fact]
    public void MissingBaseMaterials_CountsWhatIsMissingUnderCraftableIngredients()
    {
        // Spear: 2 Ingot (3 Ore each) + 1 Lumber (2 Log). With nothing owned, Ore and Log are missing.
        var data = Data();
        Assert.Equal(2, CraftMath.MissingBaseMaterials(Spear, data));

        // Enough Ore for both ingots: only Log is left to find.
        data.Stock[Ore] = 6;
        Assert.Equal(1, CraftMath.MissingBaseMaterials(Spear, data));

        // The Lumber itself is owned: nothing is missing, though the ingots still have to be crafted.
        data.Stock[Lumber] = 1;
        Assert.Equal(0, CraftMath.MissingBaseMaterials(Spear, data));
    }

    [Fact]
    public void MissingBaseMaterials_UnknownRecipeLacksNothing()
        => Assert.Equal(0, CraftMath.MissingBaseMaterials(999, Data()));

    [Fact]
    public void Cost_SumsAndFlags()
    {
        var result = CraftMath.Cost(
            [(1u, 3), (2u, 2), (3u, 1), (4u, 0)],
            id => id switch { 1 => 10, 2 => null, 3 => 0, _ => 500 });

        Assert.Equal(new CostResult(30, Loading: true, Partial: true), result);
    }
}
