namespace GoblinTweaks.Tests;

public class ColorWordsTests
{
    [Theory]
    [InlineData("0F0F0F", "black")]
    [InlineData("F2F2F2", "white")]
    [InlineData("808080", "gray")]
    [InlineData("6E0F0F", "dark red")]
    [InlineData("2846C8", "blue")]
    [InlineData("2E8B3C", "green")]
    [InlineData("6E4628", "brown")]
    public void Name_GivesAPlainColorWord(string hex, string expected)
        => Assert.Equal(expected, ColorWords.Name(Rgb.FromHex(hex)));

    [Fact]
    public void InRequest_ReadsSpanishColorsAndPlurals()
    {
        var colors = ColorWords.InRequest("algo oscuro con detalles rojos");
        Assert.Equal(2, colors.Count);
        Assert.True(ColorMath.Lightness(colors[0]) < 0.3f);              // oscuro = dark
        Assert.Equal("red", ColorWords.Name(colors[1]).Split(' ')[^1]);  // rojos = red
    }

    [Fact]
    public void InRequest_ReadsEnglishColorsAndAccents()
    {
        Assert.Single(ColorWords.InRequest("a black knight"));
        Assert.Single(ColorWords.InRequest("algo marrón"));
    }

    [Fact]
    public void InRequest_NoColor_Empty()
        => Assert.Empty(ColorWords.InRequest("capitana pirata elegante"));

    [Fact]
    public void InRequest_WholeWordsOnly()
        => Assert.Empty(ColorWords.InRequest("busco un tesoro"));   // "oro" inside "tesoro" is not gold

    [Fact]
    public void InRequest_SameColorOnce()
        => Assert.Single(ColorWords.InRequest("negro, oscuro y black"));

    [Fact]
    public void WordsInRequest_TheWordsThatNamedAColor()
    {
        // As the player wrote them, without accents or plurals, once each: shown back as "what was understood".
        Assert.Equal(["negro", "gris", "oscuro", "azul", "rojo"],ColorWords.WordsInRequest("Negro, gris oscuro? azules y rojos, negro otra vez"));
        Assert.Empty(ColorWords.WordsInRequest("capitana pirata elegante"));
    }
}
