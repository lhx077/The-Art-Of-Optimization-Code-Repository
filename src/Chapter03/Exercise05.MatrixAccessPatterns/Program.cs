// ---------------------------------------------------------------------------
// Exercise 05 — Matrix Access Patterns: How Memory Layout Decides Performance
// ---------------------------------------------------------------------------
// Matrix multiplication is the classic example of how memory access patterns
// interact with the CPU cache hierarchy. The same O(N^3) algorithm can run at
// vastly different speeds depending solely on the order in which we traverse
// the arrays. This exercise makes that difference measurable.
//
// Background: Row-Major Memory Layout
// ------------------------------------
// C# (and C/C++) store two-dimensional data in row-major order. A logical
// matrix element C[i][j] lives at the flat address C[i*N + j]. This means
// that incrementing j by 1 moves to the very next byte in memory — a
// sequential access that hardware prefetchers love. Incrementing i by 1,
// however, jumps forward by N elements (N * 8 bytes for double), potentially
// landing in a completely different cache line.
//
// A typical cache line is 64 bytes, which holds 64 / 8 = 8 doubles. When
// the CPU fetches one element, it pulls the entire 64-byte line into L1
// cache. If the next access hits the same line (sequential access), it is
// essentially free. If the next access is N elements away (strided access),
// the prefetched data is wasted and a new cache line must be fetched — a
// cache miss that costs 4-10 ns for L2 and 30-70 ns for main memory.
//
// Three Loop Orders Compared
// ---------------------------
// All three methods compute the same result: C[i,j] += A[i,k] * B[k,j].
// The difference is which index varies in the innermost loop.
//
// 1. Naive IJK (i -> j -> k):
//    Inner loop over k: A[i*N + k] is sequential (good), but B[k*N + j]
//    strides by N (bad). Every access to B jumps to a different row, causing
//    a cache miss roughly every 8 accesses (64 bytes / 8 bytes per double).
//    For large N, the B column does not fit in L1 cache, so nearly every
//    access to B is a miss.
//
// 2. Reordered IKJ (i -> k -> j):
//    Inner loop over j: C[i*N + j] is sequential (good), B[k*N + j] is
//    sequential (good), and A[i*N + k] is loop-invariant (hoisted into a
//    register). Both arrays in the inner loop enjoy perfect spatial locality.
//    This simple reordering typically yields a 2-5x speedup over IJK.
//
// 3. Blocked/Tiled IKJ (block-i -> block-k -> block-j -> i -> k -> j):
//    Even with IKJ order, when N is large the rows of B and C that we sweep
//    through may exceed the L1/L2 cache capacity, causing evictions before
//    we can reuse the data. Blocking partitions the matrices into small
//    sub-matrices (tiles) of size blockSize x blockSize. If blockSize is
//    chosen so that three tiles fit in L1 cache (e.g., 32 x 32 x 8 bytes =
//    8 KB per tile, 24 KB total — well within a typical 32-48 KB L1), then
//    all accesses within a tile are cache hits. This reduces capacity misses
//    and can provide an additional speedup for larger matrices.
//
// Expected Results
// -----------------
// - IKJ should be roughly 2-5x faster than IJK across all matrix sizes.
// - Blocked IKJ should match or slightly beat plain IKJ, with the advantage
//   growing as N increases and the working set exceeds L1/L2 cache size.
// - At N = 100 (100*100*8 = 80 KB per matrix), the entire matrix fits in L2
//   cache, so the difference between IKJ and Blocked may be small.
// - At N = 500 (500*500*8 = 2 MB per matrix), the matrices exceed L1 and
//   approach L2 capacity, making blocking more beneficial.
// ---------------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<MatrixMultiplyBenchmarks>();

// MemoryDiagnoser tracks heap allocations per benchmark invocation. For these
// benchmarks we expect zero allocations since all arrays are pre-allocated in
// GlobalSetup — any surprise allocation would indicate an unintended boxing
// or temporary array creation.
[MemoryDiagnoser]
public class MatrixMultiplyBenchmarks
{
    // -----------------------------------------------------------------------
    // Matrix sizes to benchmark. We keep the maximum at 500 to ensure the
    // full benchmark suite completes in a reasonable time. Even at N = 500,
    // each multiplication performs 500^3 = 125 million multiply-add operations,
    // which is enough to clearly reveal cache effects.
    //
    //   N = 100:  80 KB per matrix,  240 KB total — fits comfortably in L2
    //   N = 300: 720 KB per matrix, 2.16 MB total — exceeds L1, fits in L2/L3
    //   N = 500:   2 MB per matrix,    6 MB total — exceeds L2 on many CPUs
    // -----------------------------------------------------------------------
    [Params(100, 300, 500)]
    public int N;

    // -----------------------------------------------------------------------
    // We use flat one-dimensional arrays with manual index calculation rather
    // than double[,] multi-dimensional arrays. This avoids the overhead of
    // .NET's multi-dimensional array bounds checking and makes the memory
    // layout explicit: element (i, j) is stored at index [i * N + j].
    //
    // Row-major layout means that elements in the same row are contiguous:
    //   Row 0: [0, 1, 2, ..., N-1]
    //   Row 1: [N, N+1, N+2, ..., 2N-1]
    //   ...
    // Accessing elements by incrementing j (column index) is sequential in
    // memory. Accessing elements by incrementing i (row index) jumps by N
    // elements — a stride that causes cache misses for large N.
    // -----------------------------------------------------------------------
    private double[] A = null!;
    private double[] B = null!;
    private double[] C = null!;

    // -----------------------------------------------------------------------
    // GlobalSetup runs once per parameter combination. We fill A and B with
    // random values so the compiler cannot optimize away the computation.
    // C is allocated here and cleared before each iteration in IterationSetup.
    // -----------------------------------------------------------------------
    [GlobalSetup]
    public void GlobalSetup()
    {
        int size = N * N;
        A = new double[size];
        B = new double[size];
        C = new double[size];

        var rng = new Random(42); // Fixed seed for reproducibility
        for (int i = 0; i < size; i++)
        {
            A[i] = rng.NextDouble();
            B[i] = rng.NextDouble();
        }
    }

    // -----------------------------------------------------------------------
    // IterationSetup runs before every single benchmark invocation. We must
    // clear C each time because the multiplication accumulates into C (+=).
    // Without clearing, C would grow unboundedly across iterations, and the
    // results of different methods would not be comparable.
    // -----------------------------------------------------------------------
    [IterationSetup]
    public void IterationSetup()
    {
        Array.Clear(C, 0, C.Length);
    }

    // -----------------------------------------------------------------------
    // Naive IJK — Standard textbook loop order
    // -----------------------------------------------------------------------
    // Loop structure:
    //   for i = 0..N-1          (row of C and A)
    //     for j = 0..N-1        (column of C and B)
    //       for k = 0..N-1      (reduction dimension)
    //         C[i*N + j] += A[i*N + k] * B[k*N + j]
    //
    // Inner loop analysis (k varies, i and j are fixed):
    //   - A[i*N + k]: k increments by 1 → sequential access (good)
    //   - B[k*N + j]: k increments by 1 → stride of N elements (bad!)
    //
    // The access to B walks down a column: B[0*N+j], B[1*N+j], B[2*N+j], ...
    // Each step jumps N*8 bytes forward. For N = 500, that is a 4000-byte
    // stride — far larger than a 64-byte cache line. The prefetcher cannot
    // help because the stride exceeds its detection window on most CPUs.
    // Result: nearly every access to B is a cache miss in the inner loop.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public void NaiveIJK()
    {
        int n = N;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double sum = 0.0;
                for (int k = 0; k < n; k++)
                {
                    // A[i*n + k]: sequential in k (stride 1) — cache-friendly
                    // B[k*n + j]: strided in k (stride N) — cache-hostile
                    sum += A[i * n + k] * B[k * n + j];
                }
                C[i * n + j] = sum;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Reordered IKJ — Cache-friendly loop order
    // -----------------------------------------------------------------------
    // Loop structure:
    //   for i = 0..N-1          (row of C and A)
    //     for k = 0..N-1        (reduction dimension)
    //       a_ik = A[i*N + k]   (hoisted — loop-invariant for inner loop)
    //       for j = 0..N-1      (column of C and B)
    //         C[i*N + j] += a_ik * B[k*N + j]
    //
    // Inner loop analysis (j varies, i and k are fixed):
    //   - C[i*N + j]: j increments by 1 → sequential access (good)
    //   - B[k*N + j]: j increments by 1 → sequential access (good)
    //   - A[i*N + k]: constant for the entire inner loop (hoisted)
    //
    // Both array accesses in the inner loop walk sequentially through memory.
    // The hardware prefetcher detects the stride-1 pattern and speculatively
    // loads upcoming cache lines before they are needed. This transforms
    // almost every memory access into an L1 cache hit.
    //
    // The only change from IJK is swapping the j and k loop levels. The
    // algorithm performs exactly the same number of floating-point operations
    // (N^3 multiply-adds), but the memory access pattern is dramatically
    // better. Expect a 2-5x speedup over NaiveIJK.
    // -----------------------------------------------------------------------
    [Benchmark]
    public void ReorderedIKJ()
    {
        int n = N;
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < n; k++)
            {
                // Hoist A[i,k] out of the inner loop — it does not depend on j.
                // This saves one array access and one index computation per
                // inner iteration, and the value likely stays in a register.
                double a_ik = A[i * n + k];

                for (int j = 0; j < n; j++)
                {
                    // Both C and B are accessed with stride 1 in j — sequential.
                    // The CPU prefetcher keeps both streams in L1 cache.
                    C[i * n + j] += a_ik * B[k * n + j];
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Blocked (Tiled) IKJ — Optimized for cache capacity
    // -----------------------------------------------------------------------
    // Even with the IKJ loop order, when N is large the inner loop sweeps
    // through entire rows of C and B (N * 8 bytes each). For N = 500, each
    // row is 4 KB, and we access N = 500 different rows of B across the k
    // loop — that is 2 MB of B data, which may not fit in L1 or even L2.
    //
    // Blocking (also called tiling) partitions the matrix into small
    // sub-blocks of size blockSize x blockSize. Instead of processing an
    // entire row at once, we process one block at a time. The key insight:
    // if blockSize is small enough that three blocks (from A, B, and C) fit
    // in L1 cache simultaneously, then all accesses within the block are
    // cache hits.
    //
    // Memory footprint per block:
    //   blockSize = 32 → 32 * 32 * 8 = 8 KB per block
    //   Three blocks (A, B, C) → 24 KB total
    //   Typical L1 data cache: 32-48 KB → fits comfortably
    //
    // The outer loops iterate over block boundaries (bi, bk, bj), and the
    // inner loops iterate within each block. The inner loop order is still
    // i-k-j for sequential access within each block.
    //
    // For small N (e.g., 100), the entire matrix already fits in cache, so
    // blocking provides little benefit. For larger N (e.g., 500), blocking
    // reduces capacity misses and should provide a measurable improvement
    // over plain IKJ.
    // -----------------------------------------------------------------------
    private const int BlockSize = 32;

    [Benchmark]
    public void BlockedIKJ()
    {
        int n = N;
        int bs = BlockSize;

        // Outer loops: iterate over block origins.
        // The order is bi -> bk -> bj, mirroring the i-k-j order at the
        // block level to maintain the same cache-friendly access pattern.
        for (int bi = 0; bi < n; bi += bs)
        {
            int iMax = Math.Min(bi + bs, n);

            for (int bk = 0; bk < n; bk += bs)
            {
                int kMax = Math.Min(bk + bs, n);

                for (int bj = 0; bj < n; bj += bs)
                {
                    int jMax = Math.Min(bj + bs, n);

                    // Inner loops: multiply within the current block.
                    // At this point, the working set is at most three
                    // blockSize x blockSize tiles — small enough to reside
                    // entirely in L1 cache. Every access is a cache hit.
                    for (int i = bi; i < iMax; i++)
                    {
                        for (int k = bk; k < kMax; k++)
                        {
                            double a_ik = A[i * n + k];

                            for (int j = bj; j < jMax; j++)
                            {
                                C[i * n + j] += a_ik * B[k * n + j];
                            }
                        }
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Expected Results Analysis
// ---------------------------------------------------------------------------
// The benchmark results should clearly demonstrate the impact of memory
// access patterns on performance, even though all three methods perform
// exactly the same number of arithmetic operations (N^3 multiply-adds):
//
// 1. NaiveIJK (baseline):
//    The slowest method due to strided access to B in the inner loop.
//    Each access to B[k*N + j] jumps N elements forward, causing a cache
//    miss approximately every 8 accesses (64-byte cache line / 8-byte
//    double). For N = 500, the inner loop touches 500 different cache lines
//    of B, far exceeding L1 capacity.
//
// 2. ReorderedIKJ:
//    Expected to be 2-5x faster than NaiveIJK. The simple act of swapping
//    the j and k loops transforms both inner-loop array accesses from
//    strided to sequential. The hardware prefetcher can now predict and
//    pre-load upcoming cache lines, turning potential cache misses into
//    hits. This is arguably the single most impactful optimization one can
//    make to naive matrix multiplication.
//
// 3. BlockedIKJ:
//    Expected to match or slightly outperform ReorderedIKJ. The advantage
//    grows with matrix size:
//    - At N = 100: minimal difference (data already fits in cache)
//    - At N = 300: modest improvement (working set approaches L2 capacity)
//    - At N = 500: more noticeable improvement (working set exceeds L2)
//    The blocking strategy ensures that data is reused while still in L1
//    cache, reducing capacity misses that even the IKJ order cannot avoid
//    when rows are too long.
//
// Key takeaway: algorithmic complexity (all O(N^3)) tells only part of the
// story. The constant factor hidden in big-O notation is dominated by memory
// access patterns on modern hardware, where a cache miss costs 10-100x more
// than an arithmetic operation.
// ---------------------------------------------------------------------------
