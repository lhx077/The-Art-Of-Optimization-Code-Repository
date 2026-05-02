// =============================================================================
// Exercise 03 — CPU Cache Hierarchy and Memory Access Patterns
// =============================================================================
//
// This benchmark demonstrates one of the most important performance concepts
// in modern computing: the CPU cache hierarchy.
//
// BACKGROUND — THE MEMORY WALL
// -----------------------------
// CPU speeds have grown far faster than memory speeds. To bridge this gap,
// processors use a hierarchy of small, fast caches:
//
//   Level   | Typical Size     | Latency (cycles) | Notes
//   --------|------------------|-------------------|-------------------------------
//   L1      | 32–48 KB/core    | ~4 cycles         | Split into L1d (data) + L1i (instruction)
//   L2      | 256 KB–1 MB/core | ~12 cycles        | Per-core, unified
//   L3      | 8–64 MB shared   | ~40 cycles        | Shared across all cores
//   RAM     | GBs              | ~200 cycles       | Off-chip, orders of magnitude slower
//
// Each level is roughly 3–10x slower than the one above it. When data is not
// found in a cache level (a "cache miss"), the CPU stalls while it fetches
// from the next level — or worse, from main memory.
//
// THE CACHE LINE
// --------------
// Memory is transferred in fixed-size blocks called cache lines, typically
// 64 bytes. When you read a single int (4 bytes), the hardware actually loads
// the entire 64-byte line — that's 16 ints for free. This is why sequential
// access is so efficient: you pay for one miss and get 15 subsequent hits.
//
// HARDWARE PREFETCHER
// -------------------
// Modern CPUs have a hardware prefetcher that detects regular access patterns
// (especially stride-1 sequential access) and speculatively loads cache lines
// before you ask for them. This effectively hides memory latency for
// predictable access patterns.
//
// WHAT THIS BENCHMARK MEASURES
// ----------------------------
// We test two access patterns across four array sizes that target each cache
// level:
//
//   - 4 KB     → fits entirely in L1 cache
//   - 256 KB   → fits in L2 cache (spills out of L1)
//   - 4096 KB  → fits in L3 cache (spills out of L2)
//   - 65536 KB → exceeds L3, must go to main memory
//
// Sequential access: iterates linearly through the array. The prefetcher
// detects the pattern and keeps the pipeline fed. Performance should be
// roughly constant regardless of array size.
//
// Random access: uses a pre-shuffled index array to access elements in
// unpredictable order. This completely defeats the prefetcher. Every access
// is a potential cache miss, and performance degrades dramatically as the
// array exceeds each cache level.
//
// EXPECTED RESULTS
// ----------------
// You should observe a "staircase" pattern in random access times:
//
//   - 4 KB:     Fast — everything in L1
//   - 256 KB:   Slower — L1 misses, served from L2
//   - 4096 KB:  Much slower — L2 misses, served from L3
//   - 65536 KB: Dramatically slower — L3 misses, served from RAM
//
// Sequential access, by contrast, should remain nearly flat across all sizes.
//
// TIP: Plot the results (ns/op vs. array size) on a log-log scale to see
// the classic staircase pattern. This is one of the most iconic graphs in
// computer architecture.
// =============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<CacheHierarchyBenchmarks>();

// -----------------------------------------------------------------------------
// Benchmark class
// -----------------------------------------------------------------------------

[MemoryDiagnoser]
public class CacheHierarchyBenchmarks
{
    // Array sizes in KB, chosen to target each cache level.
    [ParamsSource(nameof(Sizes))]
    public int ArraySizeKB { get; set; }

    public IEnumerable<int> Sizes => [4, 256, 4096, 65536];

    private int[] _data = null!;
    private int[] _indices = null!;
    private int _elementCount;

    [GlobalSetup]
    public void Setup()
    {
        // Convert KB to element count: each int is 4 bytes (sizeof(int)).
        _elementCount = ArraySizeKB * 1024 / sizeof(int);

        // Fill the data array with sequential values.
        // The actual values don't matter — we just need something to read.
        _data = new int[_elementCount];
        for (int i = 0; i < _elementCount; i++)
            _data[i] = i;

        // Build a shuffled index array for random access using Fisher-Yates.
        // By pre-computing the random order, we ensure the benchmark loop
        // itself has no overhead from the RNG — only the memory access pattern
        // matters.
        _indices = new int[_elementCount];
        for (int i = 0; i < _elementCount; i++)
            _indices[i] = i;

        var rng = new Random(42); // Fixed seed for reproducibility
        for (int i = _elementCount - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_indices[i], _indices[j]) = (_indices[j], _indices[i]);
        }
    }

    /// <summary>
    /// Iterates through the array in order: 0, 1, 2, 3, ...
    ///
    /// This is the best-case scenario for the memory subsystem:
    ///   - Spatial locality: consecutive elements share the same cache line.
    ///     Each 64-byte line holds 16 ints, so only 1 in 16 accesses can miss.
    ///   - Hardware prefetcher: detects the stride-1 pattern and loads upcoming
    ///     cache lines before we need them, effectively hiding latency.
    ///
    /// As a result, throughput is dominated by ALU speed, not memory speed,
    /// and remains roughly constant regardless of array size.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int SequentialAccess()
    {
        int sum = 0;
        var data = _data;

        for (int i = 0; i < data.Length; i++)
            sum += data[i];

        return sum;
    }

    /// <summary>
    /// Accesses array elements in a pre-shuffled random order.
    ///
    /// This is the worst-case scenario for the memory subsystem:
    ///   - No spatial locality: consecutive accesses jump to unrelated addresses,
    ///     so loading a full cache line provides no benefit for the next access.
    ///   - Prefetcher defeated: the access pattern is unpredictable, so the
    ///     hardware prefetcher cannot anticipate which line to load next.
    ///
    /// Every access is a potential cache miss. The cost of each miss depends on
    /// which cache level (or main memory) must service the request. As the array
    /// grows beyond each cache level, the average miss penalty increases sharply
    /// — producing the characteristic "staircase" performance degradation.
    /// </summary>
    [Benchmark]
    public int RandomAccess()
    {
        int sum = 0;
        var data = _data;
        var indices = _indices;

        for (int i = 0; i < indices.Length; i++)
            sum += data[indices[i]];

        return sum;
    }
}
