using GoblinTweaks.Market;

namespace GoblinTweaks.Tests;

public class PinchMathTests
{
    private static long OneGil(long _) => 1;

    private static (PinchOutcome Outcome, int Price, PinchSkip Why) Run(uint? lowest, int current, bool own = false, int maxChange = 70, int minPrice = 1)
    {
        var outcome = PinchMath.Decide(lowest, own, current, OneGil, minPrice, maxChange, out var price, out var why);
        return (outcome, price, why);
    }

    [Fact]
    public void UndercutsTheCheapestOtherSeller()
        => Assert.Equal((PinchOutcome.Repriced, 74_989, PinchSkip.None), Run(74_990, 79_989));

    [Fact]
    public void RisesTowardTheCheapestWithinTheLimit()
        => Assert.Equal((PinchOutcome.Repriced, 14_999, PinchSkip.None), Run(15_000, 10_000));

    [Fact]
    public void TrollListingDoesNotPushThePriceUp()
    {
        // Titanbronze Bayonet: 123,453 would have gone to 999,999,988.
        var (outcome, price, why) = Run(999_999_989, 123_453);
        Assert.Equal(PinchOutcome.Skipped, outcome);
        Assert.Equal(123_453, price);
        Assert.Equal(PinchSkip.RiseTooHigh, why);
    }

    [Fact]
    public void CutDeeperThanTheLimitIsSkipped()
    {
        // Nightsteel Mail of Maiming: 666,655 against 31,569 is a 95% cut.
        Assert.Equal((PinchOutcome.Skipped, 666_655, PinchSkip.CutTooDeep), Run(31_569, 666_655));
        // With no limit it goes through.
        Assert.Equal((PinchOutcome.Repriced, 31_568, PinchSkip.None), Run(31_569, 666_655, maxChange: 100));
    }

    [Fact]
    public void OwnCheapestOrSamePriceStays()
    {
        Assert.Equal(PinchOutcome.Unchanged, Run(5_000, 5_000, own: true).Outcome);
        Assert.Equal(PinchOutcome.Unchanged, Run(5_001, 5_000).Outcome);
    }

    [Fact]
    public void NobodyElseSellsIt()
        => Assert.Equal((PinchOutcome.Skipped, 5_000, PinchSkip.NoSellers), Run(null, 5_000));

    [Fact]
    public void NeverBelowTheMinimumPrice()
        => Assert.Equal((PinchOutcome.Repriced, 900, PinchSkip.None), Run(800, 1_000, minPrice: 900));

    [Fact]
    public void LimitEdgesAreAllowed()
    {
        // 70%: from 10,000 the price may go to 3,000 or to 17,000, not past them.
        Assert.Equal(PinchOutcome.Repriced, Run(3_001, 10_000).Outcome);
        Assert.Equal(PinchOutcome.Skipped,  Run(3_000, 10_000).Outcome);
        Assert.Equal(PinchOutcome.Repriced, Run(17_001, 10_000).Outcome);
        Assert.Equal(PinchOutcome.Skipped,  Run(17_002, 10_000).Outcome);
    }

    [Fact]
    public void ChangePercentIsOneDecimal()
    {
        Assert.Equal("95.3", PinchMath.ChangePercent(666_655, 31_569));
        Assert.Equal("0", PinchMath.ChangePercent(0, 10));
    }
}
