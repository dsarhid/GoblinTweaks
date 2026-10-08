using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoblinTweaks.Services;

public sealed class UniversalisData
{
    [JsonPropertyName("averagePrice")]   public float AveragePrice   { get; init; }
    [JsonPropertyName("averagePriceHQ")] public float AveragePriceHQ { get; init; }
    [JsonPropertyName("minPrice")]       public int   MinPrice        { get; init; }
    [JsonPropertyName("minPriceHQ")]     public int   MinPriceHQ      { get; init; }
}

/// <summary>One retainer listing currently on the market board.</summary>
public sealed record MarketListing(long Price, int Quantity, string? World, string? Retainer, bool Hq, DateTime Reviewed);

/// <summary>What Universalis knows about the sales of one item, as seen from the home world.</summary>
/// <param name="AverageNq">Normal sale price, 0 when there are no sales to go by.</param>
/// <param name="WorldSalesPerDay">Units sold per day on the home world (NQ + HQ).</param>
/// <param name="DataCenterSalesPerDay">Units sold per day on the whole data center (NQ + HQ).</param>
/// <param name="LastSale">Most recent sale in the scanned area, <see cref="DateTime.MinValue"/> if unknown.</param>
public sealed record MarketSales(long AverageNq, long AverageHq, double WorldSalesPerDay, double DataCenterSalesPerDay, DateTime LastSale)
{
    public static readonly MarketSales None = new(0, 0, 0, 0, DateTime.MinValue);
}

/// <summary>One sale from the history of an item.</summary>
public sealed record SaleEntry(long PricePerUnit, int Quantity, bool Hq, DateTime Time);

/// <summary>Current listings (cheapest first) of one item and how it has been selling.</summary>
public sealed record MarketSnapshot(uint ItemId, List<MarketListing> Listings, MarketSales Sales);

public sealed class UniversalisService : IDisposable
{
    private const int MaxItemsPerRequest = 100;
    private const int MarketAttempts = 3;
    private static readonly TimeSpan MarketRetryDelay = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Universalis itself gives up after 10 s; a little more leaves room for slow connections.
    private static readonly HttpClient MarketHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Largest number of items <see cref="GetMarketAsync"/> accepts in one call.</summary>
    public static int MarketBatchSize => MaxItemsPerRequest;

    /// <summary>
    /// Fetches listings and normal sale prices for up to <see cref="MarketBatchSize"/> items.
    /// <paramref name="scope"/> is the world or data center whose listings are wanted; sales figures are
    /// always asked for <paramref name="homeWorld"/>, which also returns its data center and region.
    /// Throws on network or parse errors.
    /// </summary>
    /// <remarks>
    /// Sale history is deliberately not requested: Universalis needs about a second per item for it and
    /// answers 504 beyond a handful of items. Listings without history and the pre-computed averages of
    /// the "aggregated" endpoint each come back in a second or two for a full batch.
    /// </remarks>
    public static async Task<List<MarketSnapshot>> GetMarketAsync(string scope, string homeWorld, IReadOnlyCollection<uint> itemIds, bool wholeDataCenter, CancellationToken token)
    {
        var ids = string.Join(',', itemIds);
        var listingsTask = GetJsonAsync($"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{ids}?listings=5&entries=0", token);
        var averagesTask = GetJsonAsync($"https://universalis.app/api/v2/aggregated/{Uri.EscapeDataString(homeWorld)}/{ids}", token);

        using var listings = await listingsTask.ConfigureAwait(false);
        using var averages = await averagesTask.ConfigureAwait(false);

        // Prefer the price of the area being scanned, then wider areas when it has no recent sales.
        string[] areas = wholeDataCenter ? ["dc", "region", "world"] : ["world", "dc", "region"];
        var sales = new Dictionary<uint, MarketSales>();
        if (averages.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in results.EnumerateArray())
            {
                if (!entry.TryGetProperty("itemId", out var id) || !id.TryGetUInt32(out var itemId))
                    continue;

                var lastNq = LastSale(entry, "nq", areas);
                var lastHq = LastSale(entry, "hq", areas);
                sales[itemId] = new MarketSales(
                    NormalPrice(entry, "nq", areas),
                    NormalPrice(entry, "hq", areas),
                    SalesPerDay(entry, "nq", "world") + SalesPerDay(entry, "hq", "world"),
                    SalesPerDay(entry, "nq", "dc") + SalesPerDay(entry, "hq", "dc"),
                    lastNq > lastHq ? lastNq : lastHq);
            }
        }

        var result = new List<MarketSnapshot>();
        var root = listings.RootElement;

        // Several items come wrapped in "items"; a single item is returned as the root object.
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
                if (ParseSnapshot(item.Value, sales) is { } snapshot)
                    result.Add(snapshot);
        }
        else if (ParseSnapshot(root, sales) is { } snapshot)
        {
            result.Add(snapshot);
        }

        return result;
    }

    /// <summary>
    /// The sales of up to a handful of items on one world within <paramref name="within"/>, newest first.
    /// Items without sales come back with an empty list. Throws on network or parse errors.
    /// </summary>
    public static async Task<Dictionary<uint, List<SaleEntry>>> GetRecentSalesAsync(string world, IReadOnlyCollection<uint> itemIds, TimeSpan within, CancellationToken token)
    {
        var ids = string.Join(',', itemIds);
        var url = $"https://universalis.app/api/v2/history/{Uri.EscapeDataString(world)}/{ids}?entriesWithin={(long)within.TotalSeconds}&entriesToReturn=100";

        using var document = await GetJsonAsync(url, token).ConfigureAwait(false);
        var root   = document.RootElement;
        var result = itemIds.ToDictionary(id => id, _ => new List<SaleEntry>());

        void Read(JsonElement item)
        {
            if (!item.TryGetProperty("itemID", out var idElement) || !idElement.TryGetUInt32(out var id)) return;
            if (!result.TryGetValue(id, out var list)) return;
            if (!item.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("pricePerUnit", out var price) || !price.TryGetInt64(out var unitPrice)) continue;
                if (!entry.TryGetProperty("timestamp", out var time) || !time.TryGetInt64(out var seconds)) continue;

                var quantity = entry.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var n) ? n : 1;
                var hq       = entry.TryGetProperty("hq", out var h) && h.ValueKind == JsonValueKind.True;
                list.Add(new SaleEntry(unitPrice, Math.Max(1, quantity), hq, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime));
            }
        }

        // Several items come wrapped in "items"; a single item is returned as the root object.
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            foreach (var item in items.EnumerateObject()) Read(item.Value);
        else
            Read(root);

        return result;
    }

    /// <summary>GETs a JSON document, retrying when Universalis is momentarily overloaded.</summary>
    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await MarketHttp.GetAsync(url, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MarketAttempts && !token.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(MarketRetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Server errors (5xx), rate limiting and timeouts usually pass on their own.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || (int)status == 429,
        HttpRequestException or TaskCanceledException   => true,
        _                                               => false,
    };

    /// <summary>Average sale price of the last days, or the last sale when there were none.</summary>
    private static long NormalPrice(JsonElement entry, string quality, string[] areas)
    {
        if (!entry.TryGetProperty(quality, out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var field in (string[])["averageSalePrice", "recentPurchase"])
        {
            if (!data.TryGetProperty(field, out var byArea) || byArea.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var area in areas)
                if (byArea.TryGetProperty(area, out var value) && value.TryGetProperty("price", out var price) && price.TryGetDouble(out var gil) && gil > 0)
                    return (long)Math.Round(gil);
        }

        return 0;
    }

    /// <summary>Average units sold per day over the last days, 0 when nothing sold.</summary>
    private static double SalesPerDay(JsonElement entry, string quality, string area)
        => entry.TryGetProperty(quality, out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("dailySaleVelocity", out var byArea) && byArea.ValueKind == JsonValueKind.Object
            && byArea.TryGetProperty(area, out var value) && value.TryGetProperty("quantity", out var quantity)
            && quantity.TryGetDouble(out var perDay) && perDay > 0
                ? perDay
                : 0;

    /// <summary>When the item last sold in the first of <paramref name="areas"/> that has a sale on record.</summary>
    private static DateTime LastSale(JsonElement entry, string quality, string[] areas)
    {
        if (!entry.TryGetProperty(quality, out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("recentPurchase", out var byArea) || byArea.ValueKind != JsonValueKind.Object)
            return DateTime.MinValue;

        foreach (var area in areas)
            if (byArea.TryGetProperty(area, out var value) && value.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetInt64(out var milliseconds))
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

        return DateTime.MinValue;
    }

    private static MarketSnapshot? ParseSnapshot(JsonElement item, Dictionary<uint, MarketSales> sales)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("itemID", out var id) || !id.TryGetUInt32(out var itemId))
            return null;

        var listings = new List<MarketListing>();
        if (item.TryGetProperty("listings", out var listingArray) && listingArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var listing in listingArray.EnumerateArray())
            {
                if (!listing.TryGetProperty("pricePerUnit", out var price) || !price.TryGetInt64(out var pricePerUnit) || pricePerUnit <= 0)
                    continue;

                listings.Add(new MarketListing(
                    pricePerUnit,
                    listing.TryGetProperty("quantity", out var quantity) && quantity.TryGetInt32(out var q) ? q : 1,
                    GetString(listing, "worldName"),
                    GetString(listing, "retainerName"),
                    listing.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True,
                    listing.TryGetProperty("lastReviewTime", out var reviewed) && reviewed.TryGetInt64(out var seconds)
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                        : DateTime.MinValue));
            }
        }

        listings.Sort((a, b) => a.Price.CompareTo(b.Price));
        return new MarketSnapshot(itemId, listings, sales.GetValueOrDefault(itemId) ?? MarketSales.None);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private readonly ConcurrentDictionary<uint, UniversalisData?> _cache   = new();
    private readonly ConcurrentDictionary<uint, byte>             _pending = new();

    public bool HasData(uint itemId)   => _cache.ContainsKey(itemId);
    public bool IsPending(uint itemId) => _pending.ContainsKey(itemId);

    /// <summary>Returns cached data (null if fetch failed), or null if not yet fetched.</summary>
    public UniversalisData? Get(uint itemId) =>
        _cache.TryGetValue(itemId, out var d) ? d : null;

    public void RequestIfNeeded(uint itemId, uint worldId)
    {
        if (worldId == 0 || HasData(itemId) || !_pending.TryAdd(itemId, 0)) return;
        _ = FetchAsync(itemId, worldId);
    }

    private async Task FetchAsync(uint itemId, uint worldId)
    {
        try
        {
            var url  = $"https://universalis.app/api/v2/{worldId}/{itemId}?fields=averagePrice,averagePriceHQ,minPrice,minPriceHQ";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            _cache[itemId] = JsonSerializer.Deserialize<UniversalisData>(json);
        }
        catch
        {
            _cache[itemId] = null;
        }
        finally
        {
            _pending.TryRemove(itemId, out _);
        }
    }

    public void Dispose() { /* Http is shared static; don't dispose */ }
}
