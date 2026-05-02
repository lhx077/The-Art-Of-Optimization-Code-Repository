using System.Collections.Concurrent;
using System.Diagnostics;

// =============================================================================
// Exercise 04 — Thread-Safe TTL Cache with ConcurrentDictionary
// =============================================================================
//
// ConcurrentDictionary uses "striped locks": internally it partitions the hash
// space into multiple segments, each guarded by its own lock. This means
// threads operating on keys in *different* segments proceed in parallel —
// true concurrent writes, not serialized access.
//
// Reads in ConcurrentDictionary are often entirely lock-free (volatile reads),
// which makes read-heavy workloads dramatically faster than lock+Dictionary.
//
// By contrast, wrapping a plain Dictionary<K,V> with lock(obj) serializes
// ALL access — reads AND writes — through a single bottleneck. Under high
// thread counts this becomes the dominant cost.
//
// TTL strategy:
//   - Each entry stores an absolute expiration tick (DateTime.UtcNow + ttl).
//   - On read (TryGet), we check the timestamp; if expired, we remove lazily.
//   - A background Timer periodically scans the entire dictionary and evicts
//     stale entries, preventing unbounded memory growth for keys never re-read.
// =============================================================================

// ---------------------------------------------------------------------------
// Part 1: Basic TTL demo
// ---------------------------------------------------------------------------
Console.WriteLine("=== TTL Cache Demo ===\n");

using var cache = new TtlCache<string, string>(cleanupInterval: TimeSpan.FromSeconds(2));

cache.Set("greeting", "Hello, World!", TimeSpan.FromSeconds(1));
Console.WriteLine($"Set 'greeting' with 1-second TTL.");

if (cache.TryGet("greeting", out var val))
    Console.WriteLine($"TryGet immediately: hit  -> \"{val}\"");

Console.WriteLine("Waiting 1.5 seconds for expiry...");
Thread.Sleep(1500);

if (!cache.TryGet("greeting", out _))
    Console.WriteLine("TryGet after expiry: miss (expired)\n");

// ---------------------------------------------------------------------------
// Part 2: Concurrent stress test — ConcurrentDictionary vs lock+Dictionary
// ---------------------------------------------------------------------------
Console.WriteLine("=== Concurrent Stress Test ===\n");

const int threadCount = 8;
const int opsPerThread = 100_000;

// --- Benchmark ConcurrentDictionary-based TTL cache ---
double concurrentOpsPerSec = BenchmarkConcurrentCache(threadCount, opsPerThread);

// --- Benchmark lock+Dictionary cache ---
double lockOpsPerSec = BenchmarkLockCache(threadCount, opsPerThread);

// --- Results ---
Console.WriteLine("--- Results ---");
Console.WriteLine($"{"ConcurrentDictionary",-28} {concurrentOpsPerSec,14:N0} ops/sec");
Console.WriteLine($"{"lock + Dictionary",-28} {lockOpsPerSec,14:N0} ops/sec");
double speedup = concurrentOpsPerSec / lockOpsPerSec;
Console.WriteLine($"\nConcurrentDictionary is ~{speedup:F2}x the throughput of lock+Dictionary");
Console.WriteLine();

// Expected: under high contention with 8 threads, ConcurrentDictionary should
// show significantly higher throughput because its striped locks allow parallel
// access to different segments. The lock+Dictionary version forces every thread
// to wait on a single lock, creating a serial bottleneck.
//
// For read-heavy workloads the gap widens further because ConcurrentDictionary
// reads are often lock-free (volatile reads on the internal bucket arrays).

// ---------------------------------------------------------------------------
// Benchmark helpers (top-level local functions)
// ---------------------------------------------------------------------------
static double BenchmarkConcurrentCache(int threads, int opsPerThread)
{
    using var cache = new TtlCache<int, int>(cleanupInterval: TimeSpan.FromSeconds(5));
    var barrier = new Barrier(threads);
    var sw = new Stopwatch();
    int totalOps = threads * opsPerThread;

    var tasks = new Thread[threads];
    for (int t = 0; t < threads; t++)
    {
        int threadId = t;
        tasks[t] = new Thread(() =>
        {
            var rng = new Random(threadId * 31);
            barrier.SignalAndWait(); // synchronize start

            for (int i = 0; i < opsPerThread; i++)
            {
                int key = rng.Next(10_000);
                if (rng.Next(100) < 70) // 70% reads, 30% writes
                    cache.TryGet(key, out _);
                else
                    cache.Set(key, i, TimeSpan.FromSeconds(30));
            }
        });
    }

    foreach (var t in tasks) t.Start();
    sw.Start();
    foreach (var t in tasks) t.Join();
    sw.Stop();

    double opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"ConcurrentDictionary : {totalOps:N0} ops in {sw.ElapsedMilliseconds} ms " +
                      $"({opsPerSec:N0} ops/sec), final size = {cache.Count}");
    return opsPerSec;
}

static double BenchmarkLockCache(int threads, int opsPerThread)
{
    var cache = new LockDictCache<int, int>();
    var barrier = new Barrier(threads);
    var sw = new Stopwatch();
    int totalOps = threads * opsPerThread;

    var tasks = new Thread[threads];
    for (int t = 0; t < threads; t++)
    {
        int threadId = t;
        tasks[t] = new Thread(() =>
        {
            var rng = new Random(threadId * 31);
            barrier.SignalAndWait();

            for (int i = 0; i < opsPerThread; i++)
            {
                int key = rng.Next(10_000);
                if (rng.Next(100) < 70)
                    cache.TryGet(key, out _);
                else
                    cache.Set(key, i);
            }
        });
    }

    foreach (var t in tasks) t.Start();
    sw.Start();
    foreach (var t in tasks) t.Join();
    sw.Stop();

    double opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"lock + Dictionary    : {totalOps:N0} ops in {sw.ElapsedMilliseconds} ms " +
                      $"({opsPerSec:N0} ops/sec), final size = {cache.Count}");
    return opsPerSec;
}

// =============================================================================
// TtlCache<TKey, TValue> — Thread-safe cache with per-entry TTL
// =============================================================================
//
// Internally backed by ConcurrentDictionary which uses striped (segmented) locks.
// Multiple threads can read/write different segments simultaneously, giving us
// near-linear scaling on multi-core machines.
//
// Expiration is handled in two ways:
//   1. Lazy eviction on read — TryGet checks the timestamp and removes if stale.
//   2. Periodic cleanup — a Timer sweeps the dictionary to evict entries that
//      are never re-read, preventing unbounded memory growth.
// =============================================================================
class TtlCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();
    private readonly Timer _cleanupTimer;

    public TtlCache(TimeSpan cleanupInterval)
    {
        // The timer fires periodically to remove expired entries that were
        // never accessed again (and thus never lazily evicted by TryGet).
        _cleanupTimer = new Timer(
            callback: _ => RemoveExpiredEntries(),
            state: null,
            dueTime: cleanupInterval,
            period: cleanupInterval);
    }

    /// <summary>
    /// Attempts to retrieve a value. Returns false if the key is missing or expired.
    /// Expired entries are removed lazily on access — no lock contention for other keys.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (Environment.TickCount64 < entry.ExpiresAtTicks)
            {
                value = entry.Value;
                return true;
            }

            // Expired — remove lazily. TryRemove is atomic per-key.
            _store.TryRemove(key, out _);
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Adds or updates a cache entry with the specified TTL.
    /// ConcurrentDictionary.AddOrUpdate is atomic per-key thanks to striped locks.
    /// </summary>
    public void Set(TKey key, TValue value, TimeSpan ttl)
    {
        long expiresAt = Environment.TickCount64 + (long)ttl.TotalMilliseconds;
        var entry = new CacheEntry<TValue>(value, expiresAt);
        _store[key] = entry;
    }

    public bool TryRemove(TKey key) => _store.TryRemove(key, out _);

    /// <summary>
    /// Approximate count. ConcurrentDictionary.Count momentarily locks all
    /// segments to produce a snapshot — it is not free, but it is consistent.
    /// Under heavy contention the value may be slightly stale by the time
    /// you read it, hence "approximate".
    /// </summary>
    public int Count => _store.Count;

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private void RemoveExpiredEntries()
    {
        long now = Environment.TickCount64;
        foreach (var kvp in _store)
        {
            if (now >= kvp.Value.ExpiresAtTicks)
                _store.TryRemove(kvp.Key, out _);
        }
    }
}

/// <summary>
/// Immutable value type stored in the cache. Using a record struct avoids
/// heap allocation per entry — the struct is stored inline in the dictionary.
/// </summary>
record struct CacheEntry<T>(T Value, long ExpiresAtTicks);

// =============================================================================
// LockDictCache<TKey, TValue> — Baseline: single-lock Dictionary
// =============================================================================
//
// This is the naive approach: a plain Dictionary protected by a single lock.
// Every read and every write must acquire the same lock, which means all
// threads are serialized — only one can proceed at a time. Under high thread
// counts this becomes a severe bottleneck because of lock contention and
// context-switching overhead.
//
// No TTL support here — this class exists purely to compare raw throughput
// against ConcurrentDictionary under identical workloads.
// =============================================================================
class LockDictCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _dict = new();
    private readonly object _lock = new();

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            return _dict.TryGetValue(key, out value!);
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            _dict[key] = value;
        }
    }

    public bool TryRemove(TKey key)
    {
        lock (_lock)
        {
            return _dict.Remove(key);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _dict.Count;
            }
        }
    }
}
