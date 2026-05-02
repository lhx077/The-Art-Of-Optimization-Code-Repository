// ============================================================================
// Exercise 03 — Multi-Level Caching
// ============================================================================
// Demonstrates a local memory cache layered over a simulated distributed cache,
// with penetration protection, stampede protection, cache warming, and a simple
// BenchmarkDotNet workload to compare cached and uncached access.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Caching.Memory;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<CacheBenchmarks>();
    return;
}

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        using MemoryCache memoryCache = new(new MemoryCacheOptions { SizeLimit = 1_024 });
        SimulatedDistributedCache distributedCache = new();
        ProductRepository repository = new();
        MultiLevelProductCache cache = new(memoryCache, distributedCache, repository);

        await cache.WarmAsync([1, 2, 3]);

        ProductSnapshot? first = await cache.GetAsync(1);
        ProductSnapshot? second = await cache.GetAsync(999);
        ProductSnapshot? third = await cache.GetAsync(1);

        Console.WriteLine(first is null ? "missing" : $"product={first.Name}, price={first.Price}");
        Console.WriteLine(second is null ? "not found cached" : "unexpected");
        Console.WriteLine(third is null ? "missing" : $"L1 hit for {third.Name}");
    }
}

public sealed class MultiLevelProductCache
{
    private static readonly ProductSnapshot Missing = new(-1, "<missing>", 0m, 0, 0);

    private readonly MemoryCache _localCache;
    private readonly SimulatedDistributedCache _distributedCache;
    private readonly ProductRepository _repository;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public MultiLevelProductCache(MemoryCache localCache, SimulatedDistributedCache distributedCache, ProductRepository repository)
    {
        _localCache = localCache;
        _distributedCache = distributedCache;
        _repository = repository;
    }

    public async Task<ProductSnapshot?> GetAsync(int productId)
    {
        string key = CacheKeys.Product(productId);
        if (_localCache.TryGetValue<ProductSnapshot>(key, out ProductSnapshot? local))
        {
            return ReferenceEquals(local, Missing) ? null : local;
        }

        if (await _distributedCache.TryGetAsync(key) is ProductSnapshot distributed)
        {
            SetLocal(key, distributed, TimeSpan.FromSeconds(15));
            return ReferenceEquals(distributed, Missing) ? null : distributed;
        }

        SemaphoreSlim gate = _stripes[(uint)productId % (uint)_stripes.Length];
        await gate.WaitAsync();
        try
        {
            if (_localCache.TryGetValue<ProductSnapshot>(key, out local))
            {
                return ReferenceEquals(local, Missing) ? null : local;
            }

            ProductSnapshot? fresh = await _repository.GetAsync(productId);
            ProductSnapshot toStore = fresh ?? Missing;
            TimeSpan ttl = fresh is null ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);

            SetLocal(key, toStore, ttl);
            await _distributedCache.SetAsync(key, toStore, ttl);
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WarmAsync(IEnumerable<int> productIds)
    {
        foreach (int productId in productIds)
        {
            await GetAsync(productId);
        }
    }

    private void SetLocal(string key, ProductSnapshot value, TimeSpan ttl)
    {
        _localCache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1
        });
    }
}

public sealed class SimulatedDistributedCache
{
    private readonly ConcurrentDictionary<string, CacheEnvelope> _store = new(StringComparer.Ordinal);

    public Task<ProductSnapshot?> TryGetAsync(string key)
    {
        if (_store.TryGetValue(key, out CacheEnvelope? value) && value.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult<ProductSnapshot?>(value.Value);
        }

        return Task.FromResult<ProductSnapshot?>(null);
    }

    public Task SetAsync(string key, ProductSnapshot value, TimeSpan ttl)
    {
        _store[key] = new CacheEnvelope(value, DateTimeOffset.UtcNow.Add(ttl));
        return Task.CompletedTask;
    }

    private sealed record CacheEnvelope(ProductSnapshot Value, DateTimeOffset ExpiresAt);
}

public sealed class ProductRepository
{
    private readonly Dictionary<int, ProductSnapshot> _products = new()
    {
        [1] = new ProductSnapshot(1, "Keyboard", 399m, 12, 7),
        [2] = new ProductSnapshot(2, "Mouse", 199m, 20, 4),
        [3] = new ProductSnapshot(3, "Monitor", 1599m, 6, 2)
    };

    public async Task<ProductSnapshot?> GetAsync(int productId)
    {
        await Task.Delay(3);
        _products.TryGetValue(productId, out ProductSnapshot? product);
        return product;
    }
}

public static class CacheKeys
{
    public static string Product(int productId) => $"product:{productId}";
}

public sealed record ProductSnapshot(int Id, string Name, decimal Price, int Stock, int Version);

[MemoryDiagnoser]
public class CacheBenchmarks
{
    private MultiLevelProductCache _cache = null!;
    private ProductRepository _repository = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _repository = new ProductRepository();
        _cache = new MultiLevelProductCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1_024 }), new SimulatedDistributedCache(), _repository);
        await _cache.WarmAsync([1, 2, 3]);
    }

    [Benchmark(Baseline = true)]
    public async Task<ProductSnapshot?> CachedHit() => await _cache.GetAsync(1);

    [Benchmark]
    public async Task<ProductSnapshot?> CacheMiss() => await _cache.GetAsync(404);

    [Benchmark]
    public async Task<ProductSnapshot?> RepositoryOnly() => await _repository.GetAsync(1);
}
