using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 03 — LINQ Hidden Allocations
// ============================================================================
// LINQ is expressive and readable, but every operator in a chain allocates at
// least one enumerator (state-machine) object on the heap.  This benchmark
// quantifies those allocations and shows two progressively cheaper alternatives.
// ============================================================================

BenchmarkRunner.Run<LinqAllocationBenchmarks>();

// ---------------------------------------------------------------------------
// Data type — record struct so the *data itself* lives on the stack / inline
// in the array, avoiding per-element class allocations.
// ---------------------------------------------------------------------------
record struct DataItem(int Id, string Category, double Value, bool IsValid);

// ---------------------------------------------------------------------------
// Benchmark class
// ---------------------------------------------------------------------------
// [MemoryDiagnoser] adds Gen0/Gen1/Gen2 collection counts and total allocated
// bytes to the output — the key metric for this exercise.
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class LinqAllocationBenchmarks
{
    // Two dataset sizes: 1K (cache-friendly) and 100K (realistic).
    [Params(1_000, 100_000)]
    public int N;

    private DataItem[] data = null!;
    private double threshold;

    private static readonly string[] Categories = ["Action", "Puzzle", "RPG", "Strategy", "Simulation"];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42); // fixed seed for reproducibility
        threshold = 0.5;

        data = new DataItem[N];
        for (int i = 0; i < N; i++)
        {
            data[i] = new DataItem(
                Id:       i,
                Category: Categories[rng.Next(Categories.Length)],
                Value:    rng.NextDouble(),
                IsValid:  rng.NextDouble() > 0.2  // ~80 % valid
            );
        }
    }

    // -----------------------------------------------------------------------
    // (a) Full LINQ chain — maximum allocations
    // -----------------------------------------------------------------------
    // Allocation sources:
    //   • Where()  → allocates a WhereArrayIterator<DataItem> (enumerator)
    //   • Where()  → another WhereEnumerableIterator<DataItem>
    //   • Select() → SelectEnumerableIterator + one anonymous-type class
    //                 instance per *yielded* element (new { Category, Score })
    //   • OrderByDescending() → buffers ALL selected elements into an
    //                 internal array + creates an OrderedEnumerable
    //   • Take()   → yet another iterator
    //   • ToList() → allocates a List<T> with its backing array
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public object LinqChain()
    {
        return data
            .Where(x => x.IsValid)
            .Where(x => x.Value > threshold)
            .Select(x => new { x.Category, Score = x.Value * 2.0 })
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // (b) Optimized LINQ — fewer allocations
    // -----------------------------------------------------------------------
    // Improvements over (a):
    //   • Merged two Where() calls into one → eliminates one enumerator.
    //   • Replaced anonymous type (new { }) with a value tuple
    //     (Category, Score).  Value tuples are structs, so they do NOT
    //     allocate a class instance per element — a significant saving
    //     when thousands of elements pass the filter.
    //   • The OrderBy buffer still allocates, but it now holds value tuples
    //     instead of heap-allocated anonymous objects.
    // -----------------------------------------------------------------------
    [Benchmark]
    public object LinqOptimizedOrder()
    {
        return data
            .Where(x => x.IsValid && x.Value > threshold)
            .Select(x => (x.Category, Score: x.Value * 2.0))
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // (c) Hand-written loop — near-zero allocation
    // -----------------------------------------------------------------------
    // Strategy:
    //   • Single pass through the array — no enumerator objects at all.
    //   • Filter inline (skip invalid / below-threshold items).
    //   • Maintain a fixed-size top-10 buffer using simple insertion sort.
    //     Because the buffer is only 10 elements, insertion sort is optimal
    //     (branch-predictor-friendly, no recursion, no extra memory).
    //   • The only heap allocation is the final result array.
    //
    // Expected result: dramatically lower Allocated bytes and zero (or near-
    // zero) Gen0 collections compared to LinqChain.
    // -----------------------------------------------------------------------
    [Benchmark]
    public (string Category, double Score)[] HandWrittenLoop()
    {
        const int TopCount = 10;

        // Stack-allocated top-10 buffer (value tuples — no heap).
        Span<(string Category, double Score)> top = new (string, double)[TopCount];
        int count = 0;

        for (int i = 0; i < data.Length; i++)
        {
            ref readonly var item = ref data[i];

            // Inline filter — no enumerator, no delegate invocation overhead.
            if (!item.IsValid || item.Value <= threshold)
                continue;

            double score = item.Value * 2.0;

            // Insertion sort into the top-10 buffer (descending by score).
            // With only 10 elements this is faster than any fancy data structure.
            if (count < TopCount)
            {
                // Buffer not full yet — insert at the correct position.
                int pos = count;
                while (pos > 0 && top[pos - 1].Score < score)
                {
                    top[pos] = top[pos - 1];
                    pos--;
                }
                top[pos] = (item.Category, score);
                count++;
            }
            else if (score > top[count - 1].Score)
            {
                // Replace the smallest element in the buffer.
                int pos = count - 1;
                while (pos > 0 && top[pos - 1].Score < score)
                {
                    top[pos] = top[pos - 1];
                    pos--;
                }
                top[pos] = (item.Category, score);
            }
        }

        // Only heap allocation: the result array.
        return top[..count].ToArray();
    }
}