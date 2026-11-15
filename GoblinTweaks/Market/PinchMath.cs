namespace GoblinTweaks.Market;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

public enum PinchOutcome { Repriced, Unchanged, Skipped }

/// <summary>Why Pinch left a listing as it was.</summary>
public enum PinchSkip
{
    None,
    /// <summary>Nobody else sells the item: there is no price to follow.</summary>
    NoSellers,
    /// <summary>Every other listing would cut more than the allowed share off the current price (bait listings, usually).</summary>
    CutTooDeep,
    /// <summary>The new price would raise the current one by more than the allowed share (a troll listing, usually).</summary>
    RiseTooHigh,
}

/// <summary>What Pinch decided for one listing.</summary>
/// <param name="Ignored">Cheaper listings of other sellers passed over as bait (below the largest change allowed).</param>
/// <param name="LowestIgnored">The cheapest of those, 0 when none was.</param>
public readonly record struct PinchDecision(PinchOutcome Outcome, int NewPrice, PinchSkip Why, int Ignored, uint LowestIgnored);

/// <summary>How Pinch prices a listing from the cheapest other one.</summary>
public static class PinchMath
{
    /// <summary>
    /// The new price is the cheapest listing of another seller minus the undercut. A listing so cheap that following
    /// it would move the price by more than <paramref name="maxChangePercent"/> % of the current one is bait: it is
    /// passed over and the next cheapest one is followed instead. Nothing changes when one of our own listings
    /// (<paramref name="ownLowest"/>) already is cheaper than the listing to follow, or nobody else sells the item.
    /// The listing is left alone (skipped) when every other listing is bait, or when following the cheapest real one
    /// would raise the price by more than the limit (a troll listing). 100 = no limit.
    /// </summary>
    /// <param name="others">Unit prices of the other sellers' listings, cheapest first.</param>
    public static PinchDecision Decide(
        IReadOnlyList<uint> others, uint? ownLowest, int currentPrice, Func<long, long> undercut, int minPrice, int maxChangePercent)
    {
        if (others.Count == 0) return new(PinchOutcome.Skipped, currentPrice, PinchSkip.NoSellers, 0, 0);

        var limited = currentPrice > 0 && maxChangePercent < 100;
        var limit   = Math.Max(0, maxChangePercent);
        var bottom  = (long)currentPrice * (100 - limit) / 100;
        var top     = (long)currentPrice * (100 + limit) / 100;

        for (var i = 0; i < others.Count; i++)
        {
            var cheapest = others[i];
            // One of ours already sells for less than the listing to follow: we are the cheapest.
            if (ownLowest is { } own && own < cheapest) return Result(PinchOutcome.Unchanged, currentPrice, PinchSkip.None, i);

            var target = (int)Math.Clamp(cheapest - undercut(cheapest), 0, int.MaxValue);
            target = Math.Max(target, Math.Max(1, minPrice));
            if (target == currentPrice) return Result(PinchOutcome.Unchanged, currentPrice, PinchSkip.None, i);

            if (limited && target < bottom) continue; // bait: look at the next seller
            if (limited && target > top) return Result(PinchOutcome.Skipped, currentPrice, PinchSkip.RiseTooHigh, i);

            return Result(PinchOutcome.Repriced, target, PinchSkip.None, i);
        }

        return Result(PinchOutcome.Skipped, currentPrice, PinchSkip.CutTooDeep, 0);

        PinchDecision Result(PinchOutcome outcome, int price, PinchSkip why, int ignored)
            => new(outcome, price, why, ignored, ignored > 0 ? others[0] : 0);
    }

    /// <summary>
    /// Price for a new listing: just below the cheapest other seller, or the same as our own cheapest listing when it
    /// is lower (never undercut ourselves). A listing more than <paramref name="maxChangePercent"/> % below the
    /// <paramref name="normal"/> price (Universalis) is bait and passed over. Null when there is nothing to follow:
    /// nobody sells the item, or every listing is bait. Not clamped to the minimum price.
    /// </summary>
    /// <param name="others">Unit prices of the other sellers' listings, cheapest first.</param>
    /// <param name="normal">The normal price of the item; 0 when unknown (then nothing counts as bait).</param>
    public static long? NewListingPrice(
        IReadOnlyList<uint> others, uint? ownLowest, long normal, Func<long, long> undercut, int maxChangePercent,
        out int ignored, out uint lowestIgnored)
    {
        ignored       = 0;
        lowestIgnored = 0;
        var bottom = normal > 0 && maxChangePercent < 100 ? normal * (100 - Math.Max(0, maxChangePercent)) / 100 : long.MinValue;

        // Our own cheapest listing comes in its place among the others, matched rather than undercut.
        var ownUsed = ownLowest is null;
        for (var i = 0; i <= others.Count; i++)
        {
            if (!ownUsed && (i == others.Count || ownLowest!.Value < others[i]))
            {
                ownUsed = true;
                if (ownLowest!.Value >= bottom) return ownLowest.Value;
            }

            if (i == others.Count) break;

            var target = others[i] - undercut(others[i]);
            if (target >= bottom) return target;

            if (ignored++ == 0) lowestIgnored = others[i];
        }

        return null;
    }

    /// <summary>How much <paramref name="to"/> differs from <paramref name="from"/>, in % of <paramref name="from"/> ("95.3").</summary>
    public static string ChangePercent(long from, long to)
        => (from > 0 ? Math.Abs(to - from) * 100d / from : 0d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
