using GoblinTweaks.Services;

namespace GoblinTweaks.Core;

/// <summary>
/// Price estimates from Universalis: the median price per unit of the sales on the home world during the
/// last 30 days, separately for normal and HQ stacks. 0 means there was no sale in that time.
/// </summary>
internal static class SellPrices
{
    /// <summary>The history endpoint is slow, so only a few items are asked at a time.</summary>
    public const int ChunkSize = 8;

    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Prices of one small group of items. Null when Universalis could not be reached.</summary>
    public static async Task<Dictionary<uint, (long Nq, long Hq)>?> FetchChunkAsync(IReadOnlyCollection<uint> ids)
    {
        try
        {
            var world = Svc.PlayerState.HomeWorld.Value.Name.ExtractText();
            var sales = await UniversalisService.GetRecentSalesAsync(world, ids, Window, CancellationToken.None).ConfigureAwait(false);

            var cutoff = DateTime.UtcNow - Window;
            return sales.ToDictionary(
                pair => pair.Key,
                pair => (Average(pair.Value, cutoff, hq: false), Average(pair.Value, cutoff, hq: true)));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "SellPrices: Universalis sales unavailable");
            return null;
        }
    }

    /// <summary>Prices of every item, group after group. Items whose group failed are left out.</summary>
    public static async Task<Dictionary<uint, (long Nq, long Hq)>?> FetchAsync(uint[] ids)
    {
        var prices = new Dictionary<uint, (long, long)>();
        foreach (var chunk in ids.Chunk(ChunkSize))
            if (await FetchChunkAsync(chunk).ConfigureAwait(false) is { } found)
                foreach (var (id, price) in found) prices[id] = price;

        return prices;
    }

    /// <summary>
    /// Fetches the groups one after another in the background, reporting each price as it arrives, so a long list
    /// fills in gradually. Stops when <paramref name="keepGoing"/> turns false or Universalis cannot be reached.
    /// </summary>
    public static Task StreamAsync(List<uint[]> batches, Func<bool> keepGoing, Action<uint, (long Nq, long Hq)> report)
        => Task.Run(async () =>
        {
            foreach (var batch in batches)
            {
                if (!keepGoing()) return;

                var found = await FetchChunkAsync(batch).ConfigureAwait(false);
                if (found is null) return; // offline or overloaded: the next window open tries again

                foreach (var id in batch) report(id, found.GetValueOrDefault(id));
            }
        });

    /// <summary>
    /// The median price per unit of the sales of that quality in the window, 0 if there were none. The median
    /// ignores the odd sale at a silly price that would drag an average away from what the item really fetches.
    /// </summary>
    private static long Average(List<SaleEntry> sales, DateTime cutoff, bool hq)
    {
        var prices = sales.Where(s => s.Hq == hq && s.Time >= cutoff).Select(s => s.PricePerUnit).Order().ToList();
        if (prices.Count == 0) return 0;

        var middle = prices.Count / 2;
        return prices.Count % 2 == 1 ? prices[middle] : (long)Math.Round((prices[middle - 1] + prices[middle]) / 2.0);
    }
}
