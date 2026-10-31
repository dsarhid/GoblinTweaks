namespace GoblinTweaks.Crafting;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

/// <summary>One ingredient of a recipe: the item and how many of it one craft uses.</summary>
public readonly record struct CraftPart(uint ItemId, string Name, uint IconId, int Amount);

/// <summary>A recipe: how many items one craft makes and what it takes.</summary>
public sealed record CraftRecipe(int ResultAmount, IReadOnlyList<CraftPart> Ingredients);

/// <summary>The recipe data and inventory the calculations read.</summary>
public interface ICraftData
{
    /// <summary>The recipe with this id, or null if there is none.</summary>
    CraftRecipe? Recipe(uint recipeId);

    /// <summary>The recipe that makes this item, or 0 if it can't be crafted.</summary>
    uint RecipeFor(uint itemId);

    /// <summary>How many of the item the player has, counting every place the window counts.</summary>
    int Owned(uint itemId);
}

/// <summary>An item and how many of it are needed.</summary>
public readonly record struct Need(uint ItemId, string Name, uint IconId, int Amount);

/// <summary>
/// What a set of items costs. <see cref="Loading"/>: some price hasn't arrived yet. <see cref="Partial"/>: some item has
/// no price, so <see cref="Gil"/> is a minimum.
/// </summary>
public readonly record struct CostResult(long Gil, bool Loading, bool Partial);

/// <summary>The materials a list of recipes needs, with or without counting what the player already has.</summary>
public static class CraftMath
{
    /// <summary>Deepest level of sub-recipes followed (same as the ingredient tree of the window).</summary>
    public const int MaxDepth = 6;

    /// <summary>
    /// The ingredients of every listed recipe, times how many times it is crafted, added up by item, in the order they
    /// first appear. Recipes that don't exist are skipped.
    /// </summary>
    public static List<Need> Direct(IEnumerable<CartItem> items, ICraftData data)
    {
        var sum = new Sum();
        foreach (var item in items)
            if (data.Recipe(item.RecipeId) is { } recipe)
                foreach (var part in recipe.Ingredients)
                    sum.Add(part.ItemId, part.Name, part.IconId, part.Amount * item.Count);
        return sum.ToList();
    }

    /// <summary>Crafts needed to have <paramref name="need"/> items when each craft makes <paramref name="resultAmount"/>.</summary>
    public static int Crafts(int need, int owned, int resultAmount, bool deductOwned)
    {
        var missing = Math.Max(0, need - (deductOwned ? owned : 0));
        var makes   = Math.Max(1, resultAmount);
        return (missing + makes - 1) / makes;
    }

    /// <summary>
    /// What it takes to craft the missing part of <paramref name="need"/> items: the ingredients of its recipe times the
    /// crafts needed. Empty when the item can't be crafted or no craft is needed.
    /// </summary>
    public static List<Need> Children(uint itemId, int need, ICraftData data, bool deductOwned)
    {
        var recipeId = data.RecipeFor(itemId);
        if (recipeId == 0 || data.Recipe(recipeId) is not { } recipe) return [];

        var crafts = Crafts(need, data.Owned(itemId), recipe.ResultAmount, deductOwned);
        if (crafts == 0) return [];

        return recipe.Ingredients.Select(p => new Need(p.ItemId, p.Name, p.IconId, p.Amount * crafts)).ToList();
    }

    /// <summary>
    /// The base materials the listed recipes need, every craftable ingredient broken down into what it is made of (as
    /// many crafts of it as are needed, taking into account how many each craft makes). With
    /// <paramref name="deductOwned"/>, what the player has is used first, intermediate items included; either way the
    /// leftovers of a sub-craft serve the next one. Each entry's amount is the total needed, in order of first appearance.
    /// </summary>
    public static List<Need> BaseMaterials(IEnumerable<CartItem> items, ICraftData data, bool deductOwned)
    {
        var stock = new Dictionary<uint, int>(); // what is left to use, by item
        var raw   = new Sum();                   // base materials
        var path  = new HashSet<uint>();         // sub-recipes being followed (no loops)

        int Take(uint id, int amount)
        {
            var left = stock.TryGetValue(id, out var known) ? known : deductOwned ? data.Owned(id) : 0;
            var used = Math.Min(left, amount);
            stock[id] = left - used;
            return used;
        }

        void Add(uint id, string name, uint icon, int amount, int depth)
        {
            var subId = data.RecipeFor(id);
            if (depth < MaxDepth && !path.Contains(id) && subId != 0
                && data.Recipe(subId) is { } sub && sub.Ingredients.Count > 0)
            {
                var missing = amount - Take(id, amount);
                if (missing <= 0) return;

                var makes  = Math.Max(1, sub.ResultAmount);
                var crafts = (missing + makes - 1) / makes;
                stock[id] = stock.GetValueOrDefault(id) + crafts * makes - missing; // what the crafts make beyond what is needed

                path.Add(id);
                foreach (var part in sub.Ingredients)
                    Add(part.ItemId, part.Name, part.IconId, part.Amount * crafts, depth + 1);
                path.Remove(id);
                return;
            }

            raw.Add(id, name, icon, amount);
        }

        foreach (var item in items)
            if (data.Recipe(item.RecipeId) is { } recipe)
                foreach (var part in recipe.Ingredients)
                    Add(part.ItemId, part.Name, part.IconId, part.Amount * item.Count, 1);

        return raw.ToList();
    }

    /// <summary>
    /// What buying the given quantities costs. <paramref name="unitPrice"/> returns null while a price is loading and
    /// 0 or less when the item has no price; those items are left out of the sum and flagged.
    /// </summary>
    public static CostResult Cost(IEnumerable<(uint ItemId, int Qty)> buys, Func<uint, long?> unitPrice)
    {
        long gil     = 0;
        var  loading = false;
        var  partial = false;

        foreach (var (itemId, qty) in buys)
        {
            if (qty <= 0) continue;

            var price = unitPrice(itemId);
            if (price is null) { loading = true; continue; }
            if (price <= 0)    { partial = true; continue; }
            gil += price.Value * qty;
        }

        return new CostResult(gil, loading, partial);
    }

    /// <summary>Amounts added up by item, keeping the order in which items first appear.</summary>
    private sealed class Sum
    {
        private readonly List<uint>             _order  = [];
        private readonly Dictionary<uint, Need> _byItem = [];

        public void Add(uint id, string name, uint icon, int amount)
        {
            if (_byItem.TryGetValue(id, out var known))
                _byItem[id] = known with { Amount = known.Amount + amount };
            else
            {
                _order.Add(id);
                _byItem[id] = new Need(id, name, icon, amount);
            }
        }

        public List<Need> ToList() => _order.Select(id => _byItem[id]).ToList();
    }
}
