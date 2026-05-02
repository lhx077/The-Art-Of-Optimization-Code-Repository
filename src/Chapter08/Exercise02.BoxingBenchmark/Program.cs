// =============================================================================
// Exercise 02: Boxing Memory Allocation Benchmark
// Chapter 8 — "The Art of Optimization: Deep Dive into .NET Performance"
// =============================================================================
//
// This exercise demonstrates the dramatic memory cost of boxing in .NET.
//
// BOXING EXPLAINED:
//   When a value type (e.g., int) is stored in a variable of type System.Object,
//   the CLR must "box" it — allocating a new object on the managed heap that
//   wraps the value. Each boxed int occupies:
//     - 8 bytes for the object header (sync block index + method table pointer)
//     - 4 bytes for the actual int value
//     - 4 bytes of padding (objects are aligned to 8-byte boundaries)
//     = 16 bytes total per boxed int on a 64-bit runtime
//
//   Additionally, the ArrayList stores an object reference (8 bytes on x64) for
//   each element, pointing to the boxed int on the heap.
//
// ARRAYLIST vs LIST<int>:
//   - ArrayList stores object[] internally. Every int added is boxed to object,
//     producing a separate heap allocation. Storing 1 million ints creates
//     1 million small objects (~16 MB) plus the reference array (~8 MB).
//   - List<int> stores int[] internally. Ints are laid out inline in a single
//     contiguous array with zero boxing. 1 million ints = ~4 MB flat.
//
// GC PRESSURE:
//   1 million boxed ints means 1 million short-lived (or long-lived) objects
//   that the garbage collector must track. This floods Gen0, triggers frequent
//   collections, and can promote objects into Gen1/Gen2 — all of which stall
//   application threads. List<int> produces exactly ONE array allocation.
//
// UNBOXING COST:
//   Reading a value back from ArrayList requires unboxing: the CLR performs a
//   runtime type check to verify the object is indeed a boxed int, then copies
//   the 4-byte value out of the heap object. This adds per-element overhead
//   that List<int> avoids entirely — its indexer returns the int directly.
// =============================================================================

using System.Collections;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ---------------------------------------------------------------------------
// Top-level statement: launch BenchmarkDotNet runner
// ---------------------------------------------------------------------------
BenchmarkRunner.Run<BoxingBenchmarks>();

// =============================================================================
// Benchmark class: compares boxing (ArrayList) vs no-boxing (List<int>)
// =============================================================================

/// <summary>
/// [MemoryDiagnoser] — reports allocated bytes and GC collection counts per
/// generation (Gen0, Gen1, Gen2) for each benchmark invocation.
/// [GcForce(true)] — forces a full GC between iterations so that allocation
/// measurements are not polluted by leftover objects from previous runs.
/// </summary>
[MemoryDiagnoser]
[GcForce(true)]
public class BoxingBenchmarks
{
    /// <summary>
    /// Number of integers to store. One million elements makes the boxing
    /// overhead clearly visible in both time and memory metrics.
    /// </summary>
    private const int N = 1_000_000;

    // Pre-filled collections used by the summation benchmarks.
    // GlobalSetup runs once before all benchmark iterations.
    private ArrayList _arrayList = null!;
    private List<int> _genericList = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Pre-populate collections so the Sum benchmarks measure only
        // read/unbox cost, not allocation cost.
        _arrayList = new ArrayList(N);
        _genericList = new List<int>(N);

        for (int i = 0; i < N; i++)
        {
            _arrayList.Add(i);   // boxing happens here during setup
            _genericList.Add(i);
        }
    }

    // -----------------------------------------------------------------------
    // Benchmark 1: ArrayList — every Add(int) boxes the int to object.
    //
    // For each of the 1M ints the CLR allocates a 16-byte boxed object on
    // the managed heap, then stores an 8-byte reference in the internal
    // object[] array. Expected heap allocation ≈ 16 MB (boxed ints) +
    // ~8 MB (object[] backing array) ≈ 24 MB total.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public ArrayList ArrayList_Boxing()
    {
        var list = new ArrayList(N);
        for (int i = 0; i < N; i++)
        {
            // Implicit boxing: int → object. The compiler emits a 'box'
            // IL instruction, which allocates a new System.Object wrapper
            // on the heap containing a copy of the int value.
            list.Add(i);
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // Benchmark 2: List<int> — ints stored inline, zero boxing.
    //
    // List<int> maintains an int[] internally. Adding an int simply copies
    // the 4-byte value into the next slot of the array. No heap allocation
    // per element. Expected allocation ≈ 4 MB (one int[] array).
    // -----------------------------------------------------------------------
    [Benchmark]
    public List<int> GenericList_NoBoxing()
    {
        var list = new List<int>(N);
        for (int i = 0; i < N; i++)
        {
            // No boxing — the int is stored directly in the backing int[].
            // The JIT compiles this to a simple array store instruction.
            list.Add(i);
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // Benchmark 3: Summing an ArrayList — unboxing on every element access.
    //
    // Each (int)_arrayList[i] triggers an unbox operation:
    //   1. The CLR checks that the object's method table matches System.Int32.
    //   2. If the check passes, it copies the 4-byte value from the heap
    //      object into a local/register.
    // This per-element type check + memory indirection is the "unboxing cost."
    // -----------------------------------------------------------------------
    [Benchmark]
    public int ArrayList_Sum_Unboxing()
    {
        int sum = 0;
        for (int i = 0; i < N; i++)
        {
            // Unboxing: object → int. The CLR verifies the runtime type,
            // then copies the value out of the boxed object on the heap.
            // Each access is a pointer dereference to a potentially
            // non-contiguous heap location — poor cache locality.
            sum += (int)_arrayList[i]!;
        }
        return sum;
    }

    // -----------------------------------------------------------------------
    // Benchmark 4: Summing a List<int> — direct value access, no unboxing.
    //
    // The indexer returns the int directly from the contiguous int[] array.
    // No type check, no heap indirection. The CPU prefetcher can efficiently
    // stream sequential memory, yielding excellent cache performance.
    // -----------------------------------------------------------------------
    [Benchmark]
    public int GenericList_Sum()
    {
        int sum = 0;
        for (int i = 0; i < N; i++)
        {
            // Direct array access — the int is read straight from the
            // backing int[] with no boxing, unboxing, or indirection.
            sum += _genericList[i];
        }
        return sum;
    }
}