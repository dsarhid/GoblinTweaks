namespace GoblinTweaks.Crafting;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

/// <summary>A recipe in the Crafting Materials list ("My List") and how many times it will be crafted.</summary>
public sealed class CartItem
{
    public uint RecipeId { get; set; }
    public int  Count    { get; set; } = 1;
}

/// <summary>Operations on the list of recipes to craft. The list keeps the order in which recipes were added.</summary>
public static class CraftCart
{
    public const int MinCount = 1;
    public const int MaxCount = 999;

    public static bool Contains(List<CartItem> cart, uint recipeId) => cart.Exists(i => i.RecipeId == recipeId);

    /// <summary>Adds the recipe at the end with a count of 1, or takes it out if it is already there.</summary>
    public static void Toggle(List<CartItem> cart, uint recipeId)
    {
        if (cart.RemoveAll(i => i.RecipeId == recipeId) == 0)
            cart.Add(new CartItem { RecipeId = recipeId, Count = MinCount });
    }

    /// <summary>Sets how many times a listed recipe is crafted, kept within 1–999. Does nothing if it isn't listed.</summary>
    public static void SetCount(List<CartItem> cart, uint recipeId, int count)
    {
        var item = cart.Find(i => i.RecipeId == recipeId);
        if (item is not null) item.Count = Math.Clamp(count, MinCount, MaxCount);
    }

    /// <summary>
    /// Cleans a list read from the settings: drops recipes that no longer exist and repeated ones (the first stays),
    /// and brings every count within 1–999.
    /// </summary>
    public static void Sanitize(List<CartItem> cart, Func<uint, bool> recipeExists)
    {
        var seen = new HashSet<uint>();
        cart.RemoveAll(i => !recipeExists(i.RecipeId) || !seen.Add(i.RecipeId));
        foreach (var item in cart)
            item.Count = Math.Clamp(item.Count, MinCount, MaxCount);
    }
}
