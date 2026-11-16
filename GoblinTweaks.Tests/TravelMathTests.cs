namespace GoblinTweaks.Tests;

public class TravelMathTests
{
    // Granite, Central Shroud (map 4, size 100, no offset): the Gathering Log draws its circle at about (14.1, 22.1).
    [Fact]
    public void WorldToMapCoordMatchesTheGatheringLog()
    {
        Assert.Equal(14.1f, TravelMath.WorldToMapCoord(-368.396f, 100, 0), 1);
        Assert.Equal(22.1f, TravelMath.WorldToMapCoord(29.0423f, 100, 0), 1);
    }

    [Fact]
    public void WorldToMapCoordUsesSizeAndOffset()
    {
        // Size 200: 41 / 2 units across the map; the centre (world = -offset) is the middle of the map.
        Assert.Equal(11.25f, TravelMath.WorldToMapCoord(-100f, 200, 100), 2);
        Assert.Equal(1f + 20.5f * 1224f / 2048f, TravelMath.WorldToMapCoord(100f, 200, 0), 3);
    }

    [Fact]
    public void PixelToWorldIsTheInverseOfTheTexture()
    {
        Assert.Equal(-50f, TravelMath.PixelToWorld(1024f, 100, 50), 3);
        Assert.Equal(100f, TravelMath.PixelToWorld(1224f, 200, 0), 3);
        // Round trip through the map coordinate: a pixel and its world position land on the same map coordinate.
        var world = TravelMath.PixelToWorld(690f, 100, 0);
        Assert.Equal(41f * 690f / 2048f + 1f, TravelMath.WorldToMapCoord(world, 100, 0), 3);
    }

    [Theory]
    [InlineData(0u, 1, 1u)]   // mineral deposit (Granite)
    [InlineData(2u, 1, 3u)]   // mature tree (Spruce Log)
    [InlineData(3u, 1, 4u)]   // lush vegetation patch (Rainbow Cotton Boll)
    [InlineData(1u, 2, 6u)]   // unspoiled rocky outcrop
    [InlineData(3u, 3, 12u)]  // ephemeral lush vegetation patch
    [InlineData(2u, 4, 15u)]  // legendary mature tree
    [InlineData(0u, 5, 17u)]  // concealed mineral deposit
    [InlineData(3u, 7, 26u)]  // clouded lush vegetation patch
    [InlineData(5u, 1, 21u)]  // teeming waters
    [InlineData(5u, 6, 22u)]  // swimming shadows
    [InlineData(0u, 6, 0u)]
    [InlineData(9u, 1, 0u)]
    public void PointNameRowCombinesTypeAndKind(uint gatheringType, byte pointType, uint expected)
        => Assert.Equal(expected, TravelMath.PointNameRow(gatheringType, pointType));

    [Fact]
    public void PickGearsetTakesTheHighestItemLevelOfTheClass()
    {
        var sets = new[] { (Id: 0, ClassJob: 16u, ItemLevel: 600), (1, 17u, 700), (2, 16u, 650), (3, 16u, 650) };
        Assert.Equal(2, TravelMath.PickGearset(sets, 16));
        Assert.Equal(1, TravelMath.PickGearset(sets, 17));
        Assert.Null(TravelMath.PickGearset(sets, 18));
        Assert.Null(TravelMath.PickGearset([], 16));
    }

    [Fact]
    public void JobSwitchWaitsForTheLoadThenSwitchesInTheZone()
    {
        var s = new JobSwitch(1189);
        Assert.Equal(JobSwitchStep.Wait, s.Step(false, 130, true, 1));   // still casting the teleport
        Assert.Equal(JobSwitchStep.Wait, s.Step(true, 130, false, 6));   // loading
        Assert.Equal(JobSwitchStep.Wait, s.Step(false, 1189, false, 8)); // landed, not ready yet
        Assert.Equal(JobSwitchStep.Switch, s.Step(false, 1189, true, 9));
    }

    [Fact]
    public void JobSwitchInTheSameZoneNeedsTheLoadToo()
    {
        var s = new JobSwitch(1189);
        Assert.Equal(JobSwitchStep.Wait, s.Step(false, 1189, true, 1)); // already here: the teleport hasn't happened yet
        Assert.Equal(JobSwitchStep.Wait, s.Step(true, 1189, false, 6));
        Assert.Equal(JobSwitchStep.Switch, s.Step(false, 1189, true, 8));
    }

    [Fact]
    public void JobSwitchCancelsInAnotherZoneOrAfterAMinute()
    {
        var other = new JobSwitch(1189);
        other.Step(true, 130, false, 5);
        Assert.Equal(JobSwitchStep.Cancel, other.Step(false, 132, true, 7));

        var late = new JobSwitch(1189);
        Assert.Equal(JobSwitchStep.Wait, late.Step(false, 130, true, 59));
        Assert.Equal(JobSwitchStep.Cancel, late.Step(false, 130, true, 61));

        // Landed but never ready (in combat on arrival): it still gives up after the minute.
        var busy = new JobSwitch(1189);
        busy.Step(true, 130, false, 5);
        Assert.Equal(JobSwitchStep.Wait, busy.Step(false, 1189, false, 30));
        Assert.Equal(JobSwitchStep.Cancel, busy.Step(false, 1189, false, 61));
    }
}
