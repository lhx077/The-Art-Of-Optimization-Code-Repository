using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<CollectionBenchmarks>();

// ---------------------------------------------------------------------------
// Chapter 6 – Exercise 01: Collection Benchmarks
// ---------------------------------------------------------------------------
// This benchmark compares the three most common .NET collection types across
// sequential iteration, random access, and mid-insertion scenarios.
//
// Key concepts:
//
//   Array & List<T> store elements in contiguous memory. When the CPU fetches
//   one element, the hardware prefetcher pulls neighbouring cache lines too,
//   making sequential iteration extremely fast ("cache-friendly").
//
//   LinkedList<T> allocates each node as a separate heap object. Traversing
//   the list chases pointers to scattered memory locations, causing frequent
//   cache misses — even though "next node" access is O(1) in theory, the
//   constant factor is much larger in practice.
//
//   For mid-insertion, List<T>.Insert must shift O(n) elements to open a gap.
//   LinkedList<T>.AddAfter is O(1) *once you already have the node reference*,
//   but finding that node is itself O(n) because you must walk from the head.
//
// Expected results:
//   • Sequential iteration:  Array ≈ List  >>  LinkedList
//   • Random access:         Array ≈ List  (LinkedList has no indexer)
//   • Mid-insertion:         LinkedList can win when the node reference is
//                            cached; otherwise the walk dominates and List
//                            may still be competitive for smaller N.
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class CollectionBenchmarks
{
    [Params(100, 1_000, 10_000, 100_000)]
    public int N;

    // Pre-filled collections used by iteration and random-access benchmarks.
    private int[] _array = null!;
    private List<int> _list = null!;
    private LinkedList<int> _linkedList = null!;

    // Pre-shuffled indices so random-access benchmarks hit unpredictable
    // locations, defeating the hardware prefetcher.
    private int[] _randomIndices = null!;

    // Fresh copies rebuilt before every iteration of the insertion benchmarks
    // so that repeated inserts don't compound on each other.
    private List<int> _listCopy = null!;
    private LinkedList<int> _linkedListCopy = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Build the source collections with values 0 .. N-1.
        _array = new int[N];
        _list = new List<int>(N);
        _linkedList = new LinkedList<int>();

        for (int i = 0; i < N; i++)
        {
            _array[i] = i;
            _list.Add(i);
            _linkedList.AddLast(i);
        }

        // Fisher-Yates shuffle to produce a random access pattern.
        _randomIndices = new int[N];
        for (int i = 0; i < N; i++)
            _randomIndices[i] = i;

        var rng = new Random(42); // fixed seed for reproducibility
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_randomIndices[i], _randomIndices[j]) = (_randomIndices[j], _randomIndices[i]);
        }
    }

    // Rebuild fresh copies before each benchmark iteration so that the 100
    // insertions don't accumulate across invocations.
    [IterationSetup(Targets = [nameof(List_InsertMiddle), nameof(LinkedList_InsertMiddle)])]
    public void IterationSetup()
    {
        _listCopy = new List<int>(_list);
        _linkedListCopy = new LinkedList<int>(_linkedList);
    }

    // -----------------------------------------------------------------------
    // Sequential Iteration
    // -----------------------------------------------------------------------
    // Array and List<T> benefit from spatial locality: elements sit next to
    // each other in memory, so the CPU cache line prefetcher keeps the
    // pipeline full. LinkedList nodes are scattered across the heap, causing
    // a cache miss on virtually every node visit.
    // -----------------------------------------------------------------------

    [Benchmark(Description = "Array – Sequential Iteration")]
    public int Array_SequentialIteration()
    {
        int sum = 0;
        for (int i = 0; i < _array.Length; i++)
            sum += _array[i];
        return sum;
    }

    [Benchmark(Description = "List<T> – Sequential Iteration")]
    public int List_SequentialIteration()
    {
        int sum = 0;
        for (int i = 0; i < _list.Count; i++)
            sum += _list[i];
        return sum;
    }

    [Benchmark(Description = "LinkedList<T> – Sequential Iteration")]
    public int LinkedList_SequentialIteration()
    {
        // LinkedList has no indexer; foreach uses the internal enumerator
        // which walks node.Next pointers — each dereference is likely a
        // cache miss because nodes are non-contiguous on the heap.
        int sum = 0;
        foreach (int value in _linkedList)
            sum += value;
        return sum;
    }

    // -----------------------------------------------------------------------
    // Random Access
    // -----------------------------------------------------------------------
    // Both Array and List<T> support O(1) indexed access. The shuffled index
    // array ensures we're not reading sequentially, which stresses the cache
    // hierarchy. LinkedList<T> doesn't expose an indexer at all, so it is
    // excluded from this group.
    // -----------------------------------------------------------------------

    [Benchmark(Description = "Array – Random Access")]
    public int Array_RandomAccess()
    {
        int sum = 0;
        for (int i = 0; i < _randomIndices.Length; i++)
            sum += _array[_randomIndices[i]];
        return sum;
    }

    [Benchmark(Description = "List<T> – Random Access")]
    public int List_RandomAccess()
    {
        int sum = 0;
        for (int i = 0; i < _randomIndices.Length; i++)
            sum += _list[_randomIndices[i]];
        return sum;
    }

    // -----------------------------------------------------------------------
    // Mid-Insertion (100 elements at position N/2)
    // -----------------------------------------------------------------------
    // List<T>.Insert at the middle must shift ~N/2 elements on every call —
    // that's an O(n) memmove per insertion, so 100 inserts cost O(100 * n).
    //
    // LinkedList<T>.AddAfter is O(1) for the actual insertion, but we must
    // first *find* the middle node by walking N/2 links — an O(n) traversal
    // that also suffers from cache misses. Once we have the node, the 100
    // subsequent inserts are pointer-only operations and very fast.
    //
    // For small N the List shift is cheap (fits in cache); for large N the
    // LinkedList approach can overtake because it avoids bulk memory moves.
    // -----------------------------------------------------------------------

    [Benchmark(Description = "List<T> – Insert Middle (×100)")]
    public void List_InsertMiddle()
    {
        int mid = _listCopy.Count / 2;
        for (int i = 0; i < 100; i++)
            _listCopy.Insert(mid, i);
    }

    [Benchmark(Description = "LinkedList<T> – Insert Middle (×100)")]
    public void LinkedList_InsertMiddle()
    {
        // Walk to the middle node — O(n/2) with cache misses on every step.
        LinkedListNode<int> node = _linkedListCopy.First!;
        int mid = _linkedListCopy.Count / 2;
        for (int i = 0; i < mid; i++)
            node = node.Next!;

        // Once we hold the node reference, each AddAfter is O(1) — just
        // pointer surgery, no element shifting required.
        for (int i = 0; i < 100; i++)
            node = _linkedListCopy.AddAfter(node, i);
    }
}
