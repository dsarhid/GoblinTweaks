namespace GoblinTweaks.Market;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

public enum PinchOutcome { Repriced, Unchanged, Skipped }

/// <summary>Why Pinch left a listing as it was.</summary>
public enum PinchSkip
{
    None,
    /// <summary>Nobody else sells the item: there is no price to follow.</summary>
    NoSellers,
    /// <summary>The new price would cut more than the allowed share off the current one (a bait listing, usually).</summary>
    CutTooDeep,
    /// <summary>The new price would raise the current one by more than the allowed share (a troll listing, usually).</summary>
    RiseTooHigh,
}

/// <summary>How Pinch prices a listing from the cheapest other one.</summary>
public static class PinchMath
{
    /// <summary>
    /// The new price is the cheapest listing of another seller (<paramref name="lowest"/>) minus the undercut.
    /// Nothing changes when our own listing already is the cheapest or nobody else sells it, and the listing is left
    /// alone (skipped) when the price would move by more than <paramref name="maxChangePercent"/> % of the current
    /// one, down or up: one listing at 1 gil, or at a billion, must not drag the price with it. 100 = no limit.
    /// </summary>
    public static PinchOutcome Decide(
        uint? lowest, bool lowestIsOwn, int currentPrice, Func<long, long> undercut, int minPrice, int maxChangePercent,
        out int newPrice, out PinchSkip why)
    {
        newPrice = currentPrice;
        why      = PinchSkip.None;

        if (lowest is not { } cheapest) { why = PinchSkip.NoSellers; return PinchOutcome.Skipped; }
        if (lowestIsOwn) return PinchOutcome.Unchanged; // we are already the cheapest

        var target = (int)Math.Clamp(cheapest - undercut(cheapest), 0, int.MaxValue);
        target = Math.Max(target, Math.Max(1, minPrice));
        if (target == currentPrice) return PinchOutcome.Unchanged;

        if (currentPrice > 0 && maxChangePercent < 100)
        {
            var limit = Math.Max(0, maxChangePercent);
            if (target < (long)currentPrice * (100 - limit) / 100) { why = PinchSkip.CutTooDeep;  return PinchOutcome.Skipped; }
            if (target > (long)currentPrice * (100 + limit) / 100) { why = PinchSkip.RiseTooHigh; return PinchOutcome.Skipped; }
        }

        newPrice = target;
        return PinchOutcome.Repriced;
    }

    /// <summary>How much <paramref name="to"/> differs from <paramref name="from"/>, in % of <paramref name="from"/> ("95.3").</summary>
    public static string ChangePercent(long from, long to)
        => (from > 0 ? Math.Abs(to - from) * 100d / from : 0d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
