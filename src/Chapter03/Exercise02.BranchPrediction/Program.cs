// ---------------------------------------------------------------------------
// Exercise 02 — Branch Prediction: Sorted vs Unsorted Data & Branchless Code
// ---------------------------------------------------------------------------
// This exercise demonstrates one of the most famous performance puzzles in
// computer science: why is processing a sorted array dramatically faster than
// processing the same data unsorted, even though the algorithm is identical?
//
// The answer lies in the CPU's branch predictor. Modern processors use
// speculative execution — they guess which way a conditional branch (if/else)
// will go and begin executing instructions along the predicted path before the
// condition is actually evaluated. When the guess is correct, execution
// continues at full speed. When the guess is wrong (a "branch misprediction"),
// the processor must discard the speculatively executed work, flush the
// pipeline, and restart from the correct path. This penalty is typically
// 10-20 CPU cycles per misprediction.
//
// Branch predictors exploit patterns in branch outcomes. If a branch is taken
// 1000 times in a row, the predictor learns to always predict "taken" and
// achieves near-perfect accuracy. Sorted data creates exactly this kind of
// pattern: for a threshold comparison, all values below the threshold come
// first (branch not taken), followed by all values above (branch taken). The
// predictor only mispredicts at the single transition point.
//
// Unsorted (random) data, by contrast, produces an essentially random sequence
// of taken/not-taken outcomes. No pattern-based predictor can do better than
// ~50% accuracy on truly random branches, resulting in a misprediction on
// roughly every other iteration — a catastrophic performance penalty.
//
// The branchless technique shown here eliminates the branch entirely by using
// arithmetic bit manipulation to conditionally zero out values, achieving
// consistent performance regardless of data ordering.
// ---------------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<BranchPredictionBenchmarks>();

// ---------------------------------------------------------------------------
// The [MemoryDiagnoser] attribute reports per-invocation heap allocations.
// For this benchmark all methods operate on pre-allocated arrays, so we
// expect zero allocations across the board — the focus here is purely on
// CPU-level effects (branch prediction and pipeline efficiency).
//
// Note: On platforms that support hardware performance counters, you can add
//   [HardwareCounters(HardwareCounter.BranchMispredictions)]
// to directly observe misprediction counts. This typically requires running
// with elevated (administrator) privileges and is only available on Windows
// with ETW support or Linux with perf_events.
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class BranchPredictionBenchmarks
{
    // -----------------------------------------------------------------------
    // We test two array sizes to observe whether the branch prediction penalty
    // scales linearly with the number of elements (it should — each element
    // is an independent branch that may or may not mispredict).
    // -----------------------------------------------------------------------
    [Params(10_000, 100_000)]
    public int ArraySize;

    private int[] sortedArray = null!;
    private int[] unsortedArray = null!;

    // -----------------------------------------------------------------------
    // The threshold is set to 128, exactly half the value range [0, 255].
    // This means roughly 50% of elements satisfy the condition (value > 128),
    // which is the worst case for branch prediction on random data — the
    // branch outcome is maximally unpredictable, like a fair coin flip.
    // -----------------------------------------------------------------------
    private const int Threshold = 128;

    // -----------------------------------------------------------------------
    // [GlobalSetup] runs once before benchmarking begins for each parameter
    // combination. We generate random values in [0, 255], store them in the
    // unsorted array, then copy and sort to create the sorted array. Using
    // the same underlying data ensures that both arrays contain identical
    // values — only the ordering differs — so any performance difference is
    // attributable solely to branch prediction behavior, not to different
    // data distributions.
    // -----------------------------------------------------------------------
    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42); // Fixed seed for reproducibility
        unsortedArray = new int[ArraySize];

        for (int i = 0; i < ArraySize; i++)
        {
            unsortedArray[i] = random.Next(256); // Values in [0, 255]
        }

        // Copy first, then sort — so unsortedArray retains the original
        // random ordering and sortedArray gets the sorted version.
        sortedArray = (int[])unsortedArray.Clone();
        Array.Sort(sortedArray);
    }

    // -----------------------------------------------------------------------
    // Benchmark 1: Branching on SORTED data
    // -----------------------------------------------------------------------
    // When the array is sorted, all values <= 128 appear first, followed by
    // all values > 128. The branch predictor sees a long run of "not taken"
    // outcomes, then a long run of "taken" outcomes. After a brief learning
    // period, it predicts correctly on virtually every iteration. The only
    // mispredictions occur at the single transition point where values cross
    // the threshold — perhaps 1-2 mispredictions total out of thousands of
    // iterations. This is branch prediction at its best.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public int SortedBranch()
    {
        int sum = 0;
        int[] array = sortedArray;

        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] > Threshold)
            {
                sum += array[i];
            }
        }

        return sum;
    }

    // -----------------------------------------------------------------------
    // Benchmark 2: Branching on UNSORTED data
    // -----------------------------------------------------------------------
    // This is the classic performance trap. The algorithm is byte-for-byte
    // identical to SortedBranch — the only difference is the data ordering.
    // Because the values appear in random order, the branch outcome at each
    // iteration is essentially a coin flip. The branch predictor cannot find
    // a pattern and mispredicts roughly 50% of the time. Each misprediction
    // costs 10-20 cycles of pipeline flush and restart, which accumulates
    // into a dramatic slowdown — often 2-6x slower than the sorted version.
    //
    // This is the method you should expect to be the SLOWEST in the results.
    // -----------------------------------------------------------------------
    [Benchmark]
    public int UnsortedBranch()
    {
        int sum = 0;
        int[] array = unsortedArray;

        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] > Threshold)
            {
                sum += array[i];
            }
        }

        return sum;
    }

    // -----------------------------------------------------------------------
    // Benchmark 3: Branchless on UNSORTED data
    // -----------------------------------------------------------------------
    // The branchless technique eliminates the conditional branch entirely by
    // replacing the if-statement with arithmetic bit manipulation. Here is
    // how the mask trick works at the bit level:
    //
    //   int diff = Threshold - array[i];
    //
    //   Case A: array[i] > Threshold (e.g., array[i] = 200, Threshold = 128)
    //     diff = 128 - 200 = -72
    //     In binary (32-bit two's complement): 11111111_11111111_11111111_10111000
    //     The sign bit (bit 31) is 1 because the result is negative.
    //
    //   Case B: array[i] <= Threshold (e.g., array[i] = 50, Threshold = 128)
    //     diff = 128 - 50 = 78
    //     In binary: 00000000_00000000_00000000_01001110
    //     The sign bit is 0 because the result is non-negative.
    //
    //   int mask = diff >> 31;
    //
    //   Arithmetic right shift by 31 positions propagates the sign bit across
    //   all 32 bits:
    //     - If diff was negative (array[i] > Threshold):
    //       mask = 11111111_11111111_11111111_11111111 = -1 (all ones)
    //     - If diff was non-negative (array[i] <= Threshold):
    //       mask = 00000000_00000000_00000000_00000000 = 0 (all zeros)
    //
    //   sum += array[i] & mask;
    //
    //   Bitwise AND with the mask:
    //     - mask = -1 (all ones): array[i] & 0xFFFFFFFF = array[i] (value kept)
    //     - mask =  0 (all zeros): array[i] & 0x00000000 = 0       (value discarded)
    //
    // The net effect is identical to the if-branch: we add array[i] to the
    // sum only when array[i] > Threshold. But because there is no conditional
    // jump instruction, the CPU never needs to predict a branch — it simply
    // executes the arithmetic and bitwise operations in sequence. The pipeline
    // flows smoothly regardless of the data pattern.
    //
    // This method should be significantly faster than UnsortedBranch and
    // roughly comparable to SortedBranch, demonstrating that branchless code
    // is immune to data-dependent performance variation.
    // -----------------------------------------------------------------------
    [Benchmark]
    public int UnsortedBranchless()
    {
        int sum = 0;
        int[] array = unsortedArray;

        for (int i = 0; i < array.Length; i++)
        {
            int mask = (Threshold - array[i]) >> 31;
            sum += array[i] & mask;
        }

        return sum;
    }

    // -----------------------------------------------------------------------
    // Benchmark 4: Branchless on SORTED data
    // -----------------------------------------------------------------------
    // This benchmark exists to demonstrate a key property of branchless code:
    // its performance is DATA-INDEPENDENT. Whether the input is sorted or
    // unsorted, the branchless version performs the same arithmetic operations
    // in the same order — there is no branch to predict or mispredict.
    //
    // In the results table, you should observe that SortedBranchless and
    // UnsortedBranchless have nearly identical execution times. Compare this
    // to the branching versions, where SortedBranch is fast but UnsortedBranch
    // is dramatically slower.
    //
    // This consistency makes branchless code attractive in scenarios where:
    //   1. Data ordering is unknown or varies at runtime
    //   2. Predictable (constant-time) performance is required
    //   3. The branch condition is inherently unpredictable (e.g., random data)
    // -----------------------------------------------------------------------
    [Benchmark]
    public int SortedBranchless()
    {
        int sum = 0;
        int[] array = sortedArray;

        for (int i = 0; i < array.Length; i++)
        {
            int mask = (Threshold - array[i]) >> 31;
            sum += array[i] & mask;
        }

        return sum;
    }
}

// ---------------------------------------------------------------------------
// Expected Results
// ---------------------------------------------------------------------------
// The benchmark results should clearly show the following pattern:
//
//   Method             | ArraySize | Mean
//   -------------------|-----------|----------
//   SortedBranch       | 10,000    | ~X us        (fast — predictor succeeds)
//   UnsortedBranch     | 10,000    | ~3-6x us     (slow — ~50% mispredictions)
//   UnsortedBranchless | 10,000    | ~X us        (fast — no branch at all)
//   SortedBranchless   | 10,000    | ~X us        (fast — same as unsorted)
//
// Key takeaways:
//
// 1. UnsortedBranch is the clear outlier — significantly slower than all
//    other methods despite executing the same logical operation. The entire
//    penalty comes from branch mispredictions.
//
// 2. SortedBranch is fast because the branch predictor can exploit the
//    pattern in sorted data (long runs of identical outcomes).
//
// 3. Both Branchless methods have nearly identical performance regardless
//    of whether the data is sorted or unsorted. This demonstrates that
//    branchless code eliminates data-dependent performance variation.
//
// 4. The branchless methods may be slightly slower than SortedBranch in
//    some cases because they always perform the arithmetic and bitwise
//    operations, whereas the branch version can skip the addition entirely
//    when the condition is false. When the predictor is accurate, branches
//    can actually be faster than branchless code — the advantage of
//    branchless is consistency, not raw peak speed.
//
// 5. All methods report zero heap allocations, confirming that the
//    performance differences are purely CPU-level phenomena.
// ---------------------------------------------------------------------------
