// ---------------------------------------------------------------------------
// Exercise 01 — Benchmark Basics: List.Contains vs HashSet.Contains vs LINQ Any
// ---------------------------------------------------------------------------
// This exercise demonstrates one of the most fundamental performance trade-offs
// in everyday .NET programming: the difference between O(n) linear scanning and
// O(1) hash-based lookup. List<T>.Contains and LINQ's Any() both perform a
// sequential walk through the collection, examining each element until a match
// is found (or the end is reached). HashSet<T>.Contains, by contrast, computes
// a hash code and jumps directly to the relevant bucket, yielding amortized
// constant-time lookups regardless of collection size.
//
// BenchmarkDotNet allows us to quantify this difference with statistical rigor.
// Rather than relying on ad-hoc Stopwatch measurements — which are susceptible
// to JIT warm-up artifacts, garbage-collection pauses, and OS scheduling noise —
// BenchmarkDotNet runs each method through multiple warm-up and measurement
// iterations, reports the mean, median, and standard deviation, and flags any
// outliers. The [MemoryDiagnoser] attribute further instruments each benchmark
// to report heap allocations, letting us observe the hidden cost of delegate
// and closure creation in the LINQ path.
// ---------------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Exercise01.BenchmarkBasics;

// The MemoryDiagnoser attribute instructs BenchmarkDotNet to attach a memory
// profiler to every benchmark invocation. This surfaces per-call allocation
// counts and total bytes allocated, which is critical for identifying methods
// that appear fast in raw throughput but quietly pressure the garbage collector.
[MemoryDiagnoser]
public class ContainsLookupBenchmarks
{
    // -----------------------------------------------------------------------
    // Parameterized benchmarks allow a single benchmark class to be executed
    // across a range of input sizes without duplicating code. BenchmarkDotNet
    // generates a separate row in the results table for each value of N,
    // making it straightforward to observe how execution time scales with
    // input size. We deliberately choose sizes that span three orders of
    // magnitude — from 100 to 100,000 — so that the linear vs. constant
    // complexity distinction becomes visually unmistakable in the output.
    // -----------------------------------------------------------------------
    [Params(100, 1_000, 10_000, 100_000)]
    public int N;

    private List<int> _list = null!;
    private HashSet<int> _hashSet = null!;
    private int _target;

    // -----------------------------------------------------------------------
    // The [GlobalSetup] method runs exactly once before the benchmark
    // iterations begin for each parameter combination. Separating data
    // construction from measurement is essential: we want to benchmark the
    // lookup operation in isolation, not the cost of populating the
    // collections. Both the List<int> and HashSet<int> are filled with the
    // identical sequence of integers (0 through N-1) so that any performance
    // difference is attributable solely to the data structure's lookup
    // strategy, not to differences in content. The target is set to N/2,
    // placing it in the middle of the collection — this represents the
    // average-case scenario for a linear scan, which must examine roughly
    // half the elements before finding a match.
    // -----------------------------------------------------------------------
    [GlobalSetup]
    public void Setup()
    {
        _list = new List<int>(N);
        _hashSet = new HashSet<int>(N);

        for (int i = 0; i < N; i++)
        {
            _list.Add(i);
            _hashSet.Add(i);
        }

        _target = N / 2;
    }

    // -----------------------------------------------------------------------
    // List<T>.Contains performs a straightforward O(n) linear scan. Internally
    // it calls Array.IndexOf on the backing array, comparing each element via
    // EqualityComparer<int>.Default until a match is found. For our mid-point
    // target, this means roughly N/2 comparisons on average. Crucially, this
    // method allocates no heap memory — the equality comparer for value types
    // is cached, and no intermediate objects are created. The execution time
    // should therefore scale linearly with N while the allocation column in
    // the results table remains at zero.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public bool ListContains()
    {
        return _list.Contains(_target);
    }

    // -----------------------------------------------------------------------
    // HashSet<T>.Contains provides amortized O(1) lookup by computing the
    // hash code of the target value and probing directly into the appropriate
    // bucket. Regardless of whether the set contains 100 or 100,000 elements,
    // the number of comparisons remains bounded by a small constant (typically
    // one or two, depending on the load factor and collision rate). Like
    // List.Contains, this method performs no heap allocation — the hash
    // computation and bucket traversal operate entirely on the existing
    // internal arrays. In the benchmark results, you should observe that the
    // execution time for this method remains nearly flat across all values of
    // N, in stark contrast to the linear growth of the other two methods.
    // -----------------------------------------------------------------------
    [Benchmark]
    public bool HashSetContains()
    {
        return _hashSet.Contains(_target);
    }

    // -----------------------------------------------------------------------
    // LINQ's Any() with a predicate lambda performs the same O(n) linear scan
    // as List.Contains — iterating through the collection and applying the
    // predicate to each element. However, there is a subtle but important
    // difference in memory behavior: each invocation of Any() must capture
    // the predicate delegate. Because the lambda `x => x == _target` closes
    // over the instance field _target, the compiler generates a closure
    // object (or, in some cases, a cached delegate via a display class).
    // This results in a small heap allocation on every call, which will
    // appear in the Memory Diagnoser output. While a single 64-byte
    // allocation is negligible in isolation, in a hot loop processing
    // millions of items it can create measurable GC pressure. This benchmark
    // method thus illustrates why understanding allocation behavior matters
    // even when two approaches share the same algorithmic complexity.
    // -----------------------------------------------------------------------
    [Benchmark]
    public bool LinqAny()
    {
        return _list.Any(x => x == _target);
    }
}

// ---------------------------------------------------------------------------
// Expected Results Analysis
// ---------------------------------------------------------------------------
// After running this benchmark suite, the results table should reveal several
// key patterns that reinforce fundamental algorithmic principles:
//
// 1. LINEAR SCALING (List.Contains and LINQ Any):
//    Both List.Contains and LINQ Any exhibit execution times that grow in
//    direct proportion to N. When N increases from 1,000 to 10,000, expect
//    the measured time to increase by roughly 10x. This is the hallmark of
//    O(n) complexity — each additional element adds a constant amount of
//    work to the search.
//
// 2. CONSTANT-TIME LOOKUP (HashSet.Contains):
//    HashSet.Contains should remain nearly constant across all tested sizes.
//    Whether the set holds 100 or 100,000 elements, the lookup time stays
//    in the low nanosecond range. At N = 100, the difference between List
//    and HashSet may be modest (perhaps 2-5x), but at N = 100,000 the gap
//    becomes dramatic — often 1,000x or more. This crossover point is where
//    the O(1) advantage transforms from a theoretical curiosity into a
//    practical necessity.
//
// 3. ALLOCATION OVERHEAD (LINQ Any):
//    The Memory Diagnoser column will show that LINQ Any allocates a small
//    amount of memory on each invocation (typically 64-96 bytes for the
//    delegate and enumerator), while List.Contains and HashSet.Contains
//    report zero allocations. In micro-benchmark terms the allocation cost
//    is minor, but in production code paths executed millions of times per
//    second, these small allocations accumulate and increase the frequency
//    of Gen0 garbage collections.
//
// 4. AMORTIZED SETUP COST:
//    It is worth noting that constructing a HashSet is more expensive than
//    constructing a List of the same size, because each insertion requires
//    computing a hash code and potentially resizing the bucket array. This
//    setup cost is deliberately excluded from the benchmark by placing it
//    in [GlobalSetup]. In real-world scenarios, the construction overhead
//    is amortized over the lifetime of the collection — if you perform many
//    lookups against the same data, the one-time cost of building the hash
//    table is quickly repaid by the per-lookup savings. As a rule of thumb,
//    if you expect more than a handful of lookups, prefer HashSet<T> over
//    List<T> for existence checks.
// ---------------------------------------------------------------------------

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<ContainsLookupBenchmarks>();
    }
}
