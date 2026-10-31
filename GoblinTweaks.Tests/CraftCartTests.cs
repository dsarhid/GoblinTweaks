namespace GoblinTweaks.Tests;

public class CraftCartTests
{
    [Fact]
    public void Toggle_AddsWithCountOneThenRemoves()
    {
        var cart = new List<CartItem>();

        CraftCart.Toggle(cart, 7);
        Assert.Single(cart);
        Assert.Equal(1, cart[0].Count);
        Assert.True(CraftCart.Contains(cart, 7));

        CraftCart.Toggle(cart, 7);
        Assert.Empty(cart);
        Assert.False(CraftCart.Contains(cart, 7));
    }

    [Fact]
    public void Toggle_KeepsOrderOfAdding()
    {
        var cart = new List<CartItem>();
        CraftCart.Toggle(cart, 3);
        CraftCart.Toggle(cart, 1);
        CraftCart.Toggle(cart, 2);

        Assert.Equal(new uint[] { 3, 1, 2 }, cart.Select(i => i.RecipeId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 999)]
    [InlineData(42, 42)]
    public void SetCount_Clamps(int input, int stored)
    {
        var cart = new List<CartItem> { new() { RecipeId = 9 } };
        CraftCart.SetCount(cart, 9, input);
        Assert.Equal(stored, cart[0].Count);
    }

    [Fact]
    public void SetCount_IgnoresRecipeNotInList()
    {
        var cart = new List<CartItem> { new() { RecipeId = 9, Count = 4 } };
        CraftCart.SetCount(cart, 8, 50);
        Assert.Single(cart);
        Assert.Equal(4, cart[0].Count);
    }

    [Fact]
    public void Listed_KeepsOnlyRecipesTheWindowShowsInOrder()
    {
        var cart = new List<CartItem>
        {
            new() { RecipeId = 3, Count = 2 },
            new() { RecipeId = 8 },
            new() { RecipeId = 1, Count = 4 },
        };

        var listed = CraftCart.Listed(cart, id => id != 8);

        Assert.Equal(new uint[] { 3, 1 }, listed.Select(i => i.RecipeId));
        Assert.Equal(new[] { 2, 4 }, listed.Select(i => i.Count));
        Assert.Equal(3, cart.Count);
    }

    [Fact]
    public void Sanitize_DropsUnknownAndDuplicates()
    {
        var cart = new List<CartItem>
        {
            new() { RecipeId = 1, Count = 2 },
            new() { RecipeId = 99 },
            new() { RecipeId = 1, Count = 5 },
            new() { RecipeId = 2, Count = 0 },
        };

        CraftCart.Sanitize(cart, id => id != 99);

        Assert.Equal(new uint[] { 1, 2 }, cart.Select(i => i.RecipeId));
        Assert.Equal(2, cart[0].Count);
        Assert.Equal(1, cart[1].Count);
    }
}
