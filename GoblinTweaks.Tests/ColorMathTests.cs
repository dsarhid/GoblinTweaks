namespace GoblinTweaks.Tests;

public class ColorMathTests
{
    [Fact]
    public void ToOklab_White_IsL1()
    {
        var (l, a, b) = ColorMath.ToOklab(new Rgb(255, 255, 255));
        Assert.InRange(l, 0.999f, 1.001f);
        Assert.InRange(a, -0.001f, 0.001f);
        Assert.InRange(b, -0.001f, 0.001f);
    }

    [Fact]
    public void Distance_SameColor_IsZero()
        => Assert.Equal(0f, ColorMath.Distance(Rgb.FromHex("7A3E9D"), Rgb.FromHex("7A3E9D")));

    [Fact]
    public void Harmony_Complementary_IsHigh()
        => Assert.True(ColorMath.Harmony(Rgb.FromHex("C83232"), Rgb.FromHex("32A0C8")) >= 0.8f);

    [Fact]
    public void Harmony_Clash_IsLow()
        => Assert.True(ColorMath.Harmony(Rgb.FromHex("C83232"), Rgb.FromHex("32C832")) < 0.5f);

    [Fact]
    public void Harmony_Neutral_Is08()
    {
        Assert.InRange(ColorMath.Harmony(Rgb.FromHex("808080"), Rgb.FromHex("C83232")), 0.799f, 0.801f);
        Assert.InRange(ColorMath.Harmony(Rgb.FromHex("32C832"), Rgb.FromHex("808080")), 0.799f, 0.801f);
    }

    [Fact]
    public void DominantColors_IgnoresTransparent()
    {
        var rgba = new byte[10 * 10 * 4];
        for (var i = 0; i < 100; i++)
        {
            var red = i < 70;
            rgba[i * 4]     = (byte)(red ? 200 : 0);
            rgba[i * 4 + 1] = 0;
            rgba[i * 4 + 2] = (byte)(red ? 0 : 255);
            rgba[i * 4 + 3] = (byte)(red ? 255 : 0);
        }

        var (primary, _) = ColorMath.DominantColors(rgba, 10, 10);
        Assert.True(ColorMath.Distance(primary, new Rgb(200, 0, 0)) < 0.02f, $"primary was {primary.ToHex()}");
    }

    [Fact]
    public void FromHex_ToHex_RoundTrips()
    {
        Assert.Equal("7A3E9D", Rgb.FromHex("7A3E9D").ToHex());
        Assert.Equal("7A3E9D", Rgb.FromHex("#7a3e9d").ToHex());
    }

    [Fact]
    public void FromOklch_RoundTrips()
    {
        var color = Rgb.FromHex("3A7FC2");
        var (l, a, b) = ColorMath.ToOklab(color);
        var back = ColorMath.FromOklch(l, MathF.Sqrt(a * a + b * b), ColorMath.Hue(color));
        Assert.True(ColorMath.Distance(color, back) < 0.01f, $"got {back.ToHex()}");
    }
}
