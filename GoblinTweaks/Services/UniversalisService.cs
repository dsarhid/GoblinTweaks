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

/// <summary>
/// Current listings (cheapest first) of one item and what it normally sells for.
/// The averages are 0 when Universalis has no sales to go by.
/// </summary>
public sealed record MarketSnapshot(uint ItemId, List<MarketListing> Listings, long AverageNq, long AverageHq);

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
    /// <paramref name="scope"/> is a world or data center name. Throws on network or parse errors.
    /// </summary>
    /// <remarks>
    /// Sale history is deliberately not requested: Universalis needs about a second per item for it and
    /// answers 504 beyond a handful of items. Listings without history and the pre-computed averages of
    /// the "aggregated" endpoint each come back in a second or two for a full batch.
    /// </remarks>
    public static async Task<List<MarketSnapshot>> GetMarketAsync(string scope, IReadOnlyCollection<uint> itemIds, bool wholeDataCenter, CancellationToken token)
    {
        var path = $"{Uri.EscapeDataString(scope)}/{string.Join(',', itemIds)}";
        var listingsTask = GetJsonAsync($"https://universalis.app/api/v2/{path}?listings=5&entries=0", token);
        var averagesTask = GetJsonAsync($"https://universalis.app/api/v2/aggregated/{path}", token);

        using var listings = await listingsTask.ConfigureAwait(false);
        using var averages = await averagesTask.ConfigureAwait(false);

        // Prefer the price of the area being scanned, then wider areas when it has no recent sales.
        string[] areas = wholeDataCenter ? ["dc", "region", "world"] : ["world", "dc", "region"];
        var normalPrices = new Dictionary<uint, (long Nq, long Hq)>();
        if (averages.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in results.EnumerateArray())
                if (entry.TryGetProperty("itemId", out var id) && id.TryGetUInt32(out var itemId))
                    normalPrices[itemId] = (NormalPrice(entry, "nq", areas), NormalPrice(entry, "hq", areas));
        }

        var result = new List<MarketSnapshot>();
        var root = listings.RootElement;

        // Several items come wrapped in "items"; a single item is returned as the root object.
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
                if (ParseSnapshot(item.Value, normalPrices) is { } snapshot)
                    result.Add(snapshot);
        }
        else if (ParseSnapshot(root, normalPrices) is { } snapshot)
        {
            result.Add(snapshot);
        }

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

    private static MarketSnapshot? ParseSnapshot(JsonElement item, Dictionary<uint, (long Nq, long Hq)> normalPrices)
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
        var (nq, hq2) = normalPrices.GetValueOrDefault(itemId);
        return new MarketSnapshot(itemId, listings, nq, hq2);
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
