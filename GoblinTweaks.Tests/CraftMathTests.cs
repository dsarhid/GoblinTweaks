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
    public void Cost_SumsAndFlags()
    {
        var result = CraftMath.Cost(
            [(1u, 3), (2u, 2), (3u, 1), (4u, 0)],
            id => id switch { 1 => 10, 2 => null, 3 => 0, _ => 500 });

        Assert.Equal(new CostResult(30, Loading: true, Partial: true), result);
    }
}
