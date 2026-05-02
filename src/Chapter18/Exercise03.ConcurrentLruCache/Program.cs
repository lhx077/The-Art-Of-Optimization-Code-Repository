// ============================================================================
// Exercise 03 — Striped Concurrent LRU Cache
// ============================================================================
// Uses stripe locks and per-stripe dictionaries plus linked lists to keep lookup,
// insertion, and LRU maintenance close to O(1) for each partition.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ConcurrentLruBenchmarks>();
    return;
}

StripedLruCache<int, string> cache = new(capacity: 128, stripeCount: 8, timeToLive: TimeSpan.FromMinutes(5));
cache.Set(42, "answer");
Console.WriteLine(cache.TryGet(42, out string? value) ? value : "miss");

// Core implementation used by the demo and benchmark code below.
public sealed class StripedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly Stripe<TKey, TValue>[] _stripes;

    public StripedLruCache(int capacity, int stripeCount, TimeSpan timeToLive)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (stripeCount <= 0) throw new ArgumentOutOfRangeException(nameof(stripeCount));

        int perStripeCapacity = Math.Max(1, (int)Math.Ceiling((double)capacity / stripeCount));
        _stripes = new Stripe<TKey, TValue>[stripeCount];
        for (int i = 0; i < stripeCount; i++)
        {
            _stripes[i] = new Stripe<TKey, TValue>(perStripeCapacity, timeToLive);
        }
    }

    // Keep the hot read path short so cache hits avoid extra coordination.
    public bool TryGet(TKey key, out TValue? value)
    {
        return GetStripe(key).TryGet(key, out value);
    }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(TKey key, TValue value)
    {
        GetStripe(key).Set(key, value);
    }

    private Stripe<TKey, TValue> GetStripe(TKey key)
    {
        int hash = key.GetHashCode() & 0x7FFF_FFFF;
        return _stripes[hash % _stripes.Length];
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class Stripe<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry<TKey, TValue>>> _map = new();
    private readonly LinkedList<CacheEntry<TKey, TValue>> _lru = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public Stripe(int capacity, TimeSpan ttl)
    {
        _capacity = capacity;
        _ttl = ttl;
    }

    // Keep the hot read path short so cache hits avoid extra coordination.
    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out LinkedListNode<CacheEntry<TKey, TValue>>? node))
            {
                value = default;
                return false;
            }

            if (node.Value.ExpiresAtUtc <= DateTime.UtcNow)
            {
                RemoveNode(node);
                value = default;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<CacheEntry<TKey, TValue>>? existing))
            {
                existing.Value = new CacheEntry<TKey, TValue>(key, value, DateTime.UtcNow + _ttl);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            if (_map.Count >= _capacity)
            {
                EvictOne();
            }

            LinkedListNode<CacheEntry<TKey, TValue>> node = new(new CacheEntry<TKey, TValue>(key, value, DateTime.UtcNow + _ttl));
            _lru.AddFirst(node);
            _map[key] = node;
        }
    }

    private void EvictOne()
    {
        LinkedListNode<CacheEntry<TKey, TValue>>? tail = _lru.Last;
        if (tail is not null)
        {
            RemoveNode(tail);
        }
    }

    private void RemoveNode(LinkedListNode<CacheEntry<TKey, TValue>> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct CacheEntry<TKey, TValue>(TKey Key, TValue Value, DateTime ExpiresAtUtc);

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ConcurrentLruBenchmarks
{
    private StripedLruCache<int, string> _cache = null!;

    [Params(10_000)]
    public int ItemCount;

    [Params(4, 8)]
    public int WorkerCount;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new StripedLruCache<int, string>(capacity: ItemCount, stripeCount: 16, timeToLive: TimeSpan.FromMinutes(10));
        for (int i = 0; i < ItemCount; i++)
        {
            _cache.Set(i, i.ToString());
        }
    }

    [Benchmark]
    public int MixedReadWrite()
    {
        int hits = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, worker =>
        {
            int local = 0;
            int baseKey = worker * 100_000;
            for (int i = 0; i < 20_000; i++)
            {
                int key = (baseKey + i) % ItemCount;
                if ((i & 7) == 0)
                {
                    _cache.Set(key, (key + 1).ToString());
                }
                else if (_cache.TryGet(key, out _))
                {
                    local++;
                }
            }

            Interlocked.Add(ref hits, local);
        });

        return hits;
    }
}
