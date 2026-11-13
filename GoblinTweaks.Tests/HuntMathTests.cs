namespace GoblinTweaks.Tests;

public class HuntMathTests
{
    [Fact]
    public void MarkerToMapCoord_Center()
    {
        Assert.Equal(21.5f, HuntMath.MarkerToMapCoord(1024, 100), 0.001f);
        Assert.Equal(11.25f, HuntMath.MarkerToMapCoord(1024, 200), 0.001f);
    }

    [Fact]
    public void Nearest_PicksClosest()
    {
        AetheryteSpot[] spots = [new(1, 10, 10), new(2, 30, 30)];
        Assert.Equal(2u, HuntMath.Nearest(spots, 27, 25));
        Assert.Equal(1u, HuntMath.Nearest(spots, 12, 15));
    }

    [Fact]
    public void Nearest_EmptyIsNull()
        => Assert.Null(HuntMath.Nearest([], 10, 10));

    [Fact]
    public void Nearest_AddsTheCostOfAnAwkwardAetheryte()
    {
        // The closer one is underwater (Tamamizu): 4 map units of cost make the other one win.
        AetheryteSpot[] spots = [new(1, 10, 10, 4f), new(2, 13, 10)];
        Assert.Equal(2u, HuntMath.Nearest(spots, 10.5f, 10));
    }

    [Fact]
    public void YalmsToMapUnits_FollowsTheMapScale()
    {
        Assert.Equal(1f, HuntMath.YalmsToMapUnits(2048f / 41f, 100), 0.001f);
        Assert.Equal(2f, HuntMath.YalmsToMapUnits(2048f / 41f, 200), 0.001f);
    }
}
