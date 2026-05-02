using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 01 — StringBuilder Optimizer (Cached / Pooled StringBuilder)
//
// This benchmark demonstrates the dramatic performance difference between
// naive string concatenation and various StringBuilder strategies, including
// a thread-static caching pattern similar to .NET's internal StringBuilderCache.
// ============================================================================

BenchmarkRunner.Run<StringConcatBenchmarks>();

// ============================================================================
// CachedStringBuilder — A thread-static pooled wrapper around StringBuilder
// ============================================================================
//
// .NET's runtime contains an internal class called StringBuilderCache that uses
// the exact same pattern shown here: a [ThreadStatic] field holds a single
// StringBuilder instance per thread so it can be reused without allocation.
//
// Why thread-static instead of a ConcurrentBag or ObjectPool?
//   - Zero contention: each thread owns its own slot, no locks needed.
//   - Predictable lifetime: Acquire/Dispose is a simple swap, not a queue op.
//   - One instance per thread is usually enough because string building is
//     rarely nested (and when it is, the inner call just allocates normally).
//
// The capacity cap (1024 chars) in Dispose prevents a single large build from
// permanently pinning a huge buffer on the thread. This is the same heuristic
// the BCL uses in StringBuilderCache (MaxBuilderSize = 360 in .NET, we use
// 1024 here for demonstration).
// ============================================================================

sealed class CachedStringBuilder : IDisposable
{
    // Each thread gets its own cached instance — no locking required.
    [ThreadStatic]
    private static StringBuilder? _cached;

    private StringBuilder _inner;

    private CachedStringBuilder(StringBuilder sb)
    {
        _inner = sb;
    }

    /// <summary>
    /// Acquires a StringBuilder, reusing the thread-static cache when possible.
    /// If the cached instance is null or too small, a fresh one is allocated.
    /// </summary>
    public static CachedStringBuilder Acquire(int capacity = 256)
    {
        StringBuilder? sb = _cached;
        if (sb is not null && sb.Capacity >= capacity)
        {
            // Take ownership — set the cache slot to null so concurrent calls
            // on the same thread (e.g. nested usage) allocate a new one.
            _cached = null;
            sb.Clear();
            return new CachedStringBuilder(sb);
        }

        return new CachedStringBuilder(new StringBuilder(capacity));
    }

    // --- Fluent API mirroring StringBuilder ---

    public CachedStringBuilder Append(string value)
    {
        _inner.Append(value);
        return this;
    }

    public CachedStringBuilder Append(int value)
    {
        _inner.Append(value);
        return this;
    }

    public CachedStringBuilder AppendLine(string value)
    {
        _inner.AppendLine(value);
        return this;
    }

    public override string ToString() => _inner.ToString();

    /// <summary>
    /// Returns the inner StringBuilder to the thread-static cache if its
    /// capacity hasn't grown beyond the threshold. This avoids keeping
    /// oversized buffers alive indefinitely.
    /// </summary>
    public void Dispose()
    {
        if (_inner.Capacity <= 1024)
        {
            _cached = _inner;
        }
        // If capacity > 1024, we simply let it get GC'd.
    }
}

// ============================================================================
// Benchmarks — Four strategies for building a string from N pieces
// ============================================================================
//
// Expected results (rough guidance):
//
// | Method                    | N=100   | N=1,000  | N=10,000   |
// |---------------------------|---------|----------|------------|
// | PlusEquals (+=)           | slow    | SLOW     | VERY SLOW  |
// | PlainStringBuilder        | fast    | fast     | fast       |
// | StringBuilderWithCapacity | faster  | faster   | faster     |
// | CachedStringBuilderPool   | fastest | fastest  | fastest    |
//
// Why += is O(n²):
//   Each += creates a brand-new string, copying ALL previously accumulated
//   characters plus the new segment. After N iterations the total bytes
//   copied is roughly sum(1..N) = N*(N+1)/2, which is O(n²). For N=10,000
//   with ~15-char segments, that's ~750 MB of copying — catastrophic.
//
// Why StringBuilder is O(n):
//   StringBuilder maintains an internal char[] buffer and doubles it when
//   full (geometric / exponential growth). Appending is amortized O(1)
//   per character, so N appends cost O(n) total. The downside: without a
//   capacity hint, the first few doublings cause unnecessary copies and
//   intermediate GC pressure.
//
// Why pre-sizing helps:
//   new StringBuilder(N * estimatedLength) allocates the right buffer
//   up front, eliminating ALL intermediate resizes. This saves both CPU
//   (no copying) and memory (no abandoned intermediate buffers for GC).
//
// Why the cached/pooled pattern wins:
//   Even with pre-sizing, each call to "new StringBuilder()" allocates a
//   fresh object + char[] on the heap. The thread-static cache reuses the
//   same StringBuilder across benchmark iterations, avoiding that allocation
//   entirely. The improvement is modest in raw throughput but shows up
//   clearly in the Gen0/Allocated columns of BenchmarkDotNet output.
// ============================================================================

[MemoryDiagnoser]
public class StringConcatBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int N;

    /// <summary>
    /// Naive += concatenation. Each iteration allocates a new string that is
    /// a copy of the entire accumulated result plus the new segment.
    /// Total work: O(n²) characters copied. Generates massive GC pressure.
    /// </summary>
    [Benchmark(Baseline = true)]
    public string PlusEquals()
    {
        string result = string.Empty;
        for (int i = 0; i < N; i++)
        {
            result += $"Item_{i}_value";
        }
        return result;
    }

    /// <summary>
    /// Default StringBuilder with no capacity hint. The internal buffer
    /// starts at 16 chars and doubles on overflow. For large N this means
    /// several resize-and-copy operations before the buffer is big enough.
    /// Still O(n) overall, but the intermediate allocations add GC cost.
    /// </summary>
    [Benchmark]
    public string PlainStringBuilder()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < N; i++)
        {
            sb.Append("Item_").Append(i).Append("_value");
        }
        return sb.ToString();
    }

    /// <summary>
    /// StringBuilder with a pre-estimated capacity. By guessing that each
    /// segment is roughly 50 chars (generous), we allocate once up front
    /// and avoid all intermediate resizes. The slight over-allocation is
    /// far cheaper than repeated doubling.
    /// </summary>
    [Benchmark]
    public string StringBuilderWithCapacity()
    {
        var sb = new StringBuilder(N * 50);
        for (int i = 0; i < N; i++)
        {
            sb.Append("Item_").Append(i).Append("_value");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Thread-static cached StringBuilder. On the first call, Acquire()
    /// allocates a new StringBuilder. On subsequent calls (after Dispose
    /// returns it to the cache), the same instance is reused — zero
    /// allocation for the builder itself. Combined with capacity pre-sizing,
    /// this is the fastest approach and produces the least GC pressure.
    /// </summary>
    [Benchmark]
    public string CachedStringBuilderPool()
    {
        using var csb = CachedStringBuilder.Acquire(N * 50);
        for (int i = 0; i < N; i++)
        {
            csb.Append("Item_").Append(i).Append("_value");
        }
        return csb.ToString();
    }
}
