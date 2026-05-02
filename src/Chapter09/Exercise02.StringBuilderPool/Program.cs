// ============================================================================
// Exercise 02 — Pooled StringBuilder Service
// ============================================================================
// This exercise implements a StringBuilder pool based on
// Microsoft.Extensions.ObjectPool with the following features:
//
//   1. Configurable default initial capacity for pooled StringBuilders.
//   2. Automatic state reset (Clear) on return, with oversized rejection.
//   3. A convenient IDisposable-based rental pattern (PooledStringBuilder).
//   4. Statistics tracking: pool hit rate, creation count, return count.
//   5. BenchmarkDotNet comparison of pooled vs non-pooled performance.
//
// Run with --benchmark to execute the BenchmarkDotNet comparison.
// Run without arguments for a demonstration of the pool API.
// ============================================================================

using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.ObjectPool;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<StringBuilderPoolBenchmarks>();
    return;
}

// --- Demonstration ---
var pool = new StringBuilderPoolService(initialCapacity: 256, maxRetainedCapacity: 4096);

Console.WriteLine("=== Pooled StringBuilder Service Demo ===");
Console.WriteLine();

// Pattern 1: Manual rent/return
var sb = pool.Rent();
sb.Append("Hello, ");
sb.Append("pooled ");
sb.Append("world!");
string result = sb.ToString();
pool.Return(sb);
Console.WriteLine($"Result: {result}");

// Pattern 2: Using pattern (recommended)
using (var rental = pool.RentScoped())
{
    rental.Builder.Append("Using pattern: ");
    rental.Builder.AppendJoin(", ", new[] { "zero", "allocation", "overhead" });
    Console.WriteLine($"Result: {rental.Builder}");
}

// Pattern 3: High-throughput loop
for (int i = 0; i < 10_000; i++)
{
    using var rental = pool.RentScoped();
    rental.Builder.Append("Item #");
    rental.Builder.Append(i);
    rental.Builder.Append(": processed");
    _ = rental.Builder.ToString();
}

Console.WriteLine();
Console.WriteLine("=== Pool Statistics ===");
Console.WriteLine($"Total Rentals:    {pool.Stats.TotalRentals}");
Console.WriteLine($"Pool Hits:        {pool.Stats.PoolHits}");
Console.WriteLine($"Pool Misses:      {pool.Stats.PoolMisses}");
Console.WriteLine($"Hit Rate:         {pool.Stats.HitRate:P1}");
Console.WriteLine($"Objects Created:  {pool.Stats.ObjectsCreated}");
Console.WriteLine($"Returns Accepted: {pool.Stats.ReturnsAccepted}");
Console.WriteLine($"Returns Rejected: {pool.Stats.ReturnsRejected}");

// ============================================================================
// Pool Statistics Tracker
// ============================================================================
public class PoolStatistics
{
    private long _totalRentals;
    private long _poolHits;
    private long _poolMisses;
    private long _objectsCreated;
    private long _returnsAccepted;
    private long _returnsRejected;

    public long TotalRentals => Interlocked.Read(ref _totalRentals);
    public long PoolHits => Interlocked.Read(ref _poolHits);
    public long PoolMisses => Interlocked.Read(ref _poolMisses);
    public long ObjectsCreated => Interlocked.Read(ref _objectsCreated);
    public long ReturnsAccepted => Interlocked.Read(ref _returnsAccepted);
    public long ReturnsRejected => Interlocked.Read(ref _returnsRejected);

    public double HitRate => TotalRentals == 0 ? 0.0 : (double)PoolHits / TotalRentals;

    internal void RecordRental(bool wasHit)
    {
        Interlocked.Increment(ref _totalRentals);
        if (wasHit)
            Interlocked.Increment(ref _poolHits);
        else
            Interlocked.Increment(ref _poolMisses);
    }

    internal void RecordCreation() => Interlocked.Increment(ref _objectsCreated);
    internal void RecordReturnAccepted() => Interlocked.Increment(ref _returnsAccepted);
    internal void RecordReturnRejected() => Interlocked.Increment(ref _returnsRejected);
}

// ============================================================================
// Custom ObjectPool Policy with statistics
// ============================================================================
public class TrackedStringBuilderPolicy : IPooledObjectPolicy<StringBuilder>
{
    private readonly int _initialCapacity;
    private readonly int _maxRetainedCapacity;
    private readonly PoolStatistics _stats;

    public TrackedStringBuilderPolicy(int initialCapacity, int maxRetainedCapacity, PoolStatistics stats)
    {
        _initialCapacity = initialCapacity;
        _maxRetainedCapacity = maxRetainedCapacity;
        _stats = stats;
    }

    public StringBuilder Create()
    {
        _stats.RecordCreation();
        return new StringBuilder(_initialCapacity);
    }

    public bool Return(StringBuilder obj)
    {
        // Reject oversized StringBuilders to prevent memory bloat.
        // When a StringBuilder grows beyond the threshold during use,
        // it is cheaper to let the GC reclaim it and create a fresh one
        // than to retain a disproportionately large buffer in the pool.
        if (obj.Capacity > _maxRetainedCapacity)
        {
            _stats.RecordReturnRejected();
            return false;
        }

        obj.Clear();
        _stats.RecordReturnAccepted();
        return true;
    }
}

// ============================================================================
// Scoped rental wrapper — provides using/IDisposable pattern
// ============================================================================
public readonly struct PooledStringBuilder : IDisposable
{
    private readonly StringBuilderPoolService _pool;

    public StringBuilder Builder { get; }

    internal PooledStringBuilder(StringBuilderPoolService pool, StringBuilder builder)
    {
        _pool = pool;
        Builder = builder;
    }

    public void Dispose()
    {
        _pool.Return(Builder);
    }

    public override string ToString() => Builder.ToString();
}

// ============================================================================
// Main pool service — wraps ObjectPool<StringBuilder> with statistics
// ============================================================================
public class StringBuilderPoolService
{
    private readonly ObjectPool<StringBuilder> _pool;
    private readonly PoolStatistics _stats;

    public PoolStatistics Stats => _stats;

    public StringBuilderPoolService(int initialCapacity = 256, int maxRetainedCapacity = 4096)
    {
        _stats = new PoolStatistics();
        var policy = new TrackedStringBuilderPolicy(initialCapacity, maxRetainedCapacity, _stats);
        _pool = new DefaultObjectPool<StringBuilder>(policy, maximumRetained: Environment.ProcessorCount * 2);
    }

    public StringBuilder Rent()
    {
        var sb = _pool.Get();
        // The DefaultObjectPool returns null from its internal storage if empty,
        // then calls Create(). We detect a "miss" by checking if this is a fresh object.
        // Since we always Clear() on return, a non-empty StringBuilder is impossible
        // for a pool hit — but the simplest approach is to track in the policy.
        // For accurate hit tracking, we rely on creation count vs rental count.
        bool wasHit = sb.Capacity > 0 && _stats.ObjectsCreated < _stats.TotalRentals + 1;
        _stats.RecordRental(!(_stats.ObjectsCreated > _stats.ReturnsAccepted + _stats.ReturnsRejected
            ? _stats.TotalRentals >= _stats.ObjectsCreated
            : false));
        return sb;
    }

    public void Return(StringBuilder sb)
    {
        _pool.Return(sb);
    }

    public PooledStringBuilder RentScoped()
    {
        return new PooledStringBuilder(this, Rent());
    }
}

// ============================================================================
// BenchmarkDotNet comparison: pooled vs non-pooled StringBuilder
// ============================================================================
[MemoryDiagnoser]
public class StringBuilderPoolBenchmarks
{
    private StringBuilderPoolService _pool = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new StringBuilderPoolService(initialCapacity: 256, maxRetainedCapacity: 4096);
    }

    // Simulates building a typical log/response message
    private static string BuildMessage(StringBuilder sb)
    {
        sb.Append("Timestamp: ");
        sb.Append(DateTime.UtcNow.Ticks);
        sb.Append(" | Level: INFO | Message: ");
        sb.Append("Request processed successfully");
        sb.Append(" | Duration: ");
        sb.Append(42);
        sb.Append("ms | RequestId: ");
        sb.Append("abc-def-123");
        return sb.ToString();
    }

    [Benchmark(Baseline = true)]
    public string NoPool()
    {
        var sb = new StringBuilder();
        return BuildMessage(sb);
    }

    [Benchmark]
    public string WithPool()
    {
        using var rental = _pool.RentScoped();
        return BuildMessage(rental.Builder);
    }

    [Benchmark]
    public string NoPool_100Iterations()
    {
        string last = "";
        for (int i = 0; i < 100; i++)
        {
            var sb = new StringBuilder();
            last = BuildMessage(sb);
        }
        return last;
    }

    [Benchmark]
    public string WithPool_100Iterations()
    {
        string last = "";
        for (int i = 0; i < 100; i++)
        {
            using var rental = _pool.RentScoped();
            last = BuildMessage(rental.Builder);
        }
        return last;
    }
}
