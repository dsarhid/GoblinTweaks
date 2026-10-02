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

public sealed class UniversalisService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
