using GoblinTweaks.Market;

namespace GoblinTweaks.Tests;

public class PinchMathTests
{
    private static long OneGil(long _) => 1;

    private static (PinchOutcome Outcome, int Price, PinchSkip Why) Run(
        uint[] others, int current, uint? own = null, int maxChange = 70, int minPrice = 1)
    {
        var d = PinchMath.Decide(others, own, current, OneGil, minPrice, maxChange);
        return (d.Outcome, d.NewPrice, d.Why);
    }

    [Fact]
    public void UndercutsTheCheapestOtherSeller()
        => Assert.Equal((PinchOutcome.Repriced, 74_989, PinchSkip.None), Run([74_990], 79_989));

    [Fact]
    public void RisesTowardTheCheapestWithinTheLimit()
        => Assert.Equal((PinchOutcome.Repriced, 14_999, PinchSkip.None), Run([15_000], 10_000));

    [Fact]
    public void TrollListingDoesNotPushThePriceUp()
    {
        // Titanbronze Bayonet: 123,453 would have gone to 999,999,988.
        var (outcome, price, why) = Run([999_999_989], 123_453);
        Assert.Equal(PinchOutcome.Skipped, outcome);
        Assert.Equal(123_453, price);
        Assert.Equal(PinchSkip.RiseTooHigh, why);
    }

    [Fact]
    public void CutDeeperThanTheLimitIsSkipped()
    {
        // Nightsteel Mail of Maiming: 666,655 against 31,569 is a 95% cut.
        Assert.Equal((PinchOutcome.Skipped, 666_655, PinchSkip.CutTooDeep), Run([31_569], 666_655));
        // With no limit it goes through.
        Assert.Equal((PinchOutcome.Repriced, 31_568, PinchSkip.None), Run([31_569], 666_655, maxChange: 100));
    }

    [Fact]
    public void BaitListingIsPassedOverForTheNextSeller()
    {
        // Savage Might Materia VI at 766: one at 200 (bait), then 690, 694...
        var d = PinchMath.Decide([200, 690, 694, 700], 766, 766, OneGil, 1, 70);
        Assert.Equal(PinchOutcome.Repriced, d.Outcome);
        Assert.Equal(689, d.NewPrice);
        Assert.Equal(1, d.Ignored);
        Assert.Equal(200u, d.LowestIgnored);
    }

    [Fact]
    public void SeveralBaitListingsArePassedOver()
    {
        var d = PinchMath.Decide([1, 50, 200, 9_000], 10_000, 10_000, OneGil, 1, 70);
        Assert.Equal((PinchOutcome.Repriced, 8_999, 3, 1u), (d.Outcome, d.NewPrice, d.Ignored, d.LowestIgnored));
    }

    [Fact]
    public void EveryListingBelowTheLimitIsSkipped()
    {
        var d = PinchMath.Decide([200, 210, 220], 766, 766, OneGil, 1, 70);
        Assert.Equal((PinchOutcome.Skipped, PinchSkip.CutTooDeep, 766), (d.Outcome, d.Why, d.NewPrice));
    }

    [Fact]
    public void OwnListingCheaperThanTheRealSellerStays()
    {
        // Another of our listings at 650 is already below the 690 that would be followed after the bait.
        var d = PinchMath.Decide([200, 690], 650, 766, OneGil, 1, 70);
        Assert.Equal(PinchOutcome.Unchanged, d.Outcome);
    }

    [Fact]
    public void TrollAfterBaitIsStillSkipped()
        => Assert.Equal((PinchOutcome.Skipped, 766, PinchSkip.RiseTooHigh), Run([200, 5_000], 766));

    [Fact]
    public void OwnCheapestOrSamePriceStays()
    {
        Assert.Equal(PinchOutcome.Unchanged, Run([5_001], 5_000, own: 4_000).Outcome);
        Assert.Equal(PinchOutcome.Unchanged, Run([5_001], 5_000, own: 5_000).Outcome);
    }

    [Fact]
    public void NobodyElseSellsIt()
        => Assert.Equal((PinchOutcome.Skipped, 5_000, PinchSkip.NoSellers), Run([], 5_000));

    [Fact]
    public void NeverBelowTheMinimumPrice()
        => Assert.Equal((PinchOutcome.Repriced, 900, PinchSkip.None), Run([800], 1_000, minPrice: 900));

    [Fact]
    public void LimitEdgesAreAllowed()
    {
        // 70%: from 10,000 the price may go to 3,000 or to 17,000, not past them.
        Assert.Equal(PinchOutcome.Repriced, Run([3_001], 10_000).Outcome);
        Assert.Equal(PinchOutcome.Skipped,  Run([3_000], 10_000).Outcome);
        Assert.Equal(PinchOutcome.Repriced, Run([17_001], 10_000).Outcome);
        Assert.Equal(PinchOutcome.Skipped,  Run([17_002], 10_000).Outcome);
    }

    // ── New listings (Sell) ──────────────────────────────────────────────────

    private static long? List(uint[] others, uint? own, long normal, out int ignored, out uint lowestIgnored)
        => PinchMath.NewListingPrice(others, own, normal, OneGil, 70, out ignored, out lowestIgnored);

    [Fact]
    public void NewListingUndercutsTheCheapest()
    {
        Assert.Equal(689, List([690, 700], null, 700, out var ignored, out _));
        Assert.Equal(0, ignored);
    }

    [Fact]
    public void NewListingMatchesOurOwnCheapest()
        => Assert.Equal(650, List([690], 650, 700, out _, out _));

    [Fact]
    public void NewListingPassesOverBait()
    {
        // Normal price 700, so anything below 210 is bait.
        Assert.Equal(689, List([200, 690], null, 700, out var ignored, out var lowest));
        Assert.Equal((1, 200u), (ignored, lowest));
    }

    [Fact]
    public void NewListingWithOnlyBaitHasNoPrice()
        => Assert.Null(List([100, 200], null, 700, out _, out _));

    [Fact]
    public void NewListingWithoutNormalPriceFollowsTheCheapest()
        => Assert.Equal(199, List([200, 690], null, 0, out _, out _));

    [Fact]
    public void NewListingNobodySells()
        => Assert.Null(List([], null, 700, out _, out _));

    [Fact]
    public void ChangePercentIsOneDecimal()
    {
        Assert.Equal("95.3", PinchMath.ChangePercent(666_655, 31_569));
        Assert.Equal("0", PinchMath.ChangePercent(0, 10));
    }
}
