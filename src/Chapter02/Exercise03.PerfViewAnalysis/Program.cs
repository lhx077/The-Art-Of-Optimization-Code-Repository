// ============================================================================
// Exercise 03 — PerfView Deep Analysis: Memory Pressure Patterns
// ============================================================================
//
// Purpose:
//   This exercise demonstrates three distinct memory pressure patterns that
//   produce different GC behaviors.  By running this program under PerfView
//   you will learn to correlate allocation patterns with GC generation
//   activity, pause times, and heap growth.
//
//   Each pattern is first shown in its "pressure-inducing" form, then in an
//   optimized form that eliminates or drastically reduces GC activity.
//   The console output prints GC collection counts so you can see the
//   difference immediately, but PerfView's GCStats view gives the full
//   picture: pause durations, promotion rates, and allocation call stacks.
//
// Patterns demonstrated:
//   1. Frequent Gen0 collections   — rapid small-object churn
//   2. Gen1 promotion pressure     — objects that survive just long enough
//   3. LOH (Large Object Heap)     — allocations >= 85,000 bytes
//
// ============================================================================

using System.Buffers;
using System.Diagnostics;
using System.Runtime;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static (int g0, int g1, int g2) GetGcCounts() =>
    (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

static void PrintGcDelta(string label, (int g0, int g1, int g2) before, (int g0, int g1, int g2) after)
{
    Console.WriteLine($"  {label}");
    Console.WriteLine($"    Gen0 collections : {after.g0 - before.g0}");
    Console.WriteLine($"    Gen1 collections : {after.g1 - before.g1}");
    Console.WriteLine($"    Gen2 collections : {after.g2 - before.g2}");
}

// ---------------------------------------------------------------------------
// Configuration — tune these if the exercise runs too fast or too slow
// on your hardware.  The goal is several seconds per pattern so PerfView
// captures enough ETW events for meaningful analysis.
// ---------------------------------------------------------------------------

Console.WriteLine("============================================================");
Console.WriteLine(" Memory Pressure Patterns — Unoptimized vs. Optimized");
Console.WriteLine("============================================================");
Console.WriteLine();

var timer = new Stopwatch();

// ======================= Pattern 1: Gen0 Pressure ========================

Console.WriteLine("[Pattern 1] Frequent Gen0 collections (small transient arrays)");
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var before = GetGcCounts();
timer.Restart();
MemoryPressureSimulator.SimulateGen0Pressure();
timer.Stop();
var after = GetGcCounts();
PrintGcDelta("Unoptimized:", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GetGcCounts();
timer.Restart();
OptimizedVersion.OptimizedGen0();
timer.Stop();
after = GetGcCounts();
PrintGcDelta("Optimized (stackalloc / ArrayPool):", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

// ======================= Pattern 2: Gen1 Promotion =======================

Console.WriteLine("[Pattern 2] Gen1 promotion pressure (mid-lived objects)");
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GetGcCounts();
timer.Restart();
MemoryPressureSimulator.SimulateGen1Promotion();
timer.Stop();
after = GetGcCounts();
PrintGcDelta("Unoptimized:", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GetGcCounts();
timer.Restart();
OptimizedVersion.OptimizedGen1();
timer.Stop();
after = GetGcCounts();
PrintGcDelta("Optimized (pooled buffers, reuse list):", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

// ======================= Pattern 3: LOH Pressure =========================

Console.WriteLine("[Pattern 3] LOH allocation (arrays >= 85,000 bytes)");
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GetGcCounts();
timer.Restart();
MemoryPressureSimulator.SimulateLOHPressure();
timer.Stop();
after = GetGcCounts();
PrintGcDelta("Unoptimized:", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GetGcCounts();
timer.Restart();
OptimizedVersion.OptimizedLOH();
timer.Stop();
after = GetGcCounts();
PrintGcDelta("Optimized (ArrayPool):", before, after);
Console.WriteLine($"    Elapsed          : {timer.ElapsedMilliseconds:N0} ms");
Console.WriteLine();

Console.WriteLine("============================================================");
Console.WriteLine("Done.  Compare the GC counts above, then re-run under");
Console.WriteLine("PerfView to inspect GCStats, allocation stacks, and pause");
Console.WriteLine("time distributions.  See the analysis guide at the bottom");
Console.WriteLine("of this source file for step-by-step instructions.");
Console.WriteLine("============================================================");

// ============================================================================
// Configuration constants — placed in a static class so they are accessible
// from both the top-level statements and the static helper classes below.
// In C#, top-level local variables cannot be referenced by types defined
// in the same file, so we use a dedicated configuration holder instead.
// ============================================================================

static class Config
{
    public const int Gen0Iterations = 5_000_000;
    public const int Gen1Iterations = 2_000_000;
    public const int Gen1HoldCount  = 500;
    public const int LOHIterations  = 5_000;
    public const int LOHArraySize   = 100_000;   // > 85,000 byte threshold
}

// ============================================================================
// Section 1 — The Pressure-Inducing Implementations
// ============================================================================

static class MemoryPressureSimulator
{
    // --------------------------------------------------------------------
    // Pattern 1: Frequent Gen0 collections
    // --------------------------------------------------------------------
    // We allocate a small byte array (100 bytes) on every iteration and
    // immediately discard it.  These objects are "ephemeral" — they die
    // in Gen0 before the next collection.  The sheer volume of
    // allocations forces the Gen0 budget to be exhausted repeatedly,
    // triggering many Gen0 collections.
    //
    // Gen0 collections are the cheapest kind (they only scan the small
    // Gen0 region), but at high frequency they still add up: each one
    // pauses all managed threads briefly and pollutes CPU caches.
    // --------------------------------------------------------------------
    public static void SimulateGen0Pressure()
    {
        long sum = 0;
        for (int i = 0; i < Config.Gen0Iterations; i++)
        {
            byte[] buffer = new byte[100];   // Allocated on the managed heap
            buffer[0] = (byte)(i & 0xFF);
            sum += buffer[0];
        }

        // Consume the result to prevent dead-code elimination.
        GC.KeepAlive(sum);
    }

    // --------------------------------------------------------------------
    // Pattern 2: Gen1 promotion pressure
    // --------------------------------------------------------------------
    // We keep a sliding window of references in a list.  Objects are
    // added continuously, but only the most recent Gen1HoldCount entries
    // are retained — older ones are removed from the list and become
    // eligible for collection.
    //
    // Because these objects survive at least one Gen0 collection (they
    // are still referenced when Gen0 runs), the GC promotes them to
    // Gen1.  When they are finally released, a Gen1 collection is
    // required to reclaim them.  This violates the "generational
    // hypothesis" (most objects die young) and forces more expensive
    // Gen1 collections.
    // --------------------------------------------------------------------
    public static void SimulateGen1Promotion()
    {
        var holdingList = new List<byte[]>(Config.Gen1HoldCount + 1);

        for (int i = 0; i < Config.Gen1Iterations; i++)
        {
            byte[] buffer = new byte[200];
            buffer[0] = (byte)(i & 0xFF);
            holdingList.Add(buffer);

            // Evict the oldest entry once we exceed the window size.
            // The evicted object has survived long enough to be promoted
            // out of Gen0, so it now sits in Gen1 awaiting collection.
            if (holdingList.Count > Config.Gen1HoldCount)
            {
                holdingList.RemoveAt(0);
            }
        }

        GC.KeepAlive(holdingList);
    }

    // --------------------------------------------------------------------
    // Pattern 3: Large Object Heap (LOH) allocation
    // --------------------------------------------------------------------
    // Any single allocation of 85,000 bytes or more is placed directly
    // on the LOH, which is collected only during a full (Gen2) GC.
    // Full GCs are the most expensive: they scan the entire managed
    // heap — Gen0, Gen1, Gen2, and LOH.
    //
    // Historically the LOH was never compacted, leading to
    // fragmentation.  Since .NET 4.5.1 you can request compaction via
    // GCSettings.LargeObjectHeapCompactionMode, and modern .NET
    // runtimes are smarter about LOH management, but the fundamental
    // cost of full GC remains.
    // --------------------------------------------------------------------
    public static void SimulateLOHPressure()
    {
        long sum = 0;
        for (int i = 0; i < Config.LOHIterations; i++)
        {
            // 100,000 bytes > 85,000 threshold => LOH allocation
            byte[] largeBuffer = new byte[Config.LOHArraySize];
            largeBuffer[0] = (byte)(i & 0xFF);
            sum += largeBuffer[0];
        }

        GC.KeepAlive(sum);
    }
}

// ============================================================================
// Section 2 — The Optimized Implementations
// ============================================================================

static class OptimizedVersion
{
    // --------------------------------------------------------------------
    // Optimized Gen0: stackalloc for tiny buffers, ArrayPool for larger
    // --------------------------------------------------------------------
    // For buffers small enough to fit on the stack (a few hundred bytes),
    // stackalloc avoids the managed heap entirely — no GC tracking, no
    // collection, zero overhead beyond adjusting the stack pointer.
    //
    // When the buffer is too large for the stack or its size is not known
    // at compile time, ArrayPool<T>.Shared provides a thread-safe pool
    // of reusable arrays.  Renting and returning is O(1) and produces
    // zero GC allocations after the pool warms up.
    // --------------------------------------------------------------------
    public static void OptimizedGen0()
    {
        long sum = 0;
        for (int i = 0; i < Config.Gen0Iterations; i++)
        {
            // stackalloc places the buffer on the current stack frame.
            // It is "freed" automatically when the method returns (or
            // when execution leaves the enclosing scope in modern C#).
            Span<byte> buffer = stackalloc byte[100];
            buffer[0] = (byte)(i & 0xFF);
            sum += buffer[0];
        }

        GC.KeepAlive(sum);
    }

    // --------------------------------------------------------------------
    // Optimized Gen1: reuse a fixed-size ring buffer of pooled arrays
    // --------------------------------------------------------------------
    // Instead of allocating a new byte[] on every iteration and letting
    // old ones get promoted to Gen1, we rent arrays from ArrayPool and
    // return them when they leave the sliding window.  The pool recycles
    // the same physical arrays, so the GC sees almost no new allocations
    // after the initial warm-up.
    //
    // We also replace List.RemoveAt(0) — which is O(n) because it
    // shifts all elements — with a Queue, which provides O(1) enqueue
    // and dequeue.
    // --------------------------------------------------------------------
    public static void OptimizedGen1()
    {
        var pool = ArrayPool<byte>.Shared;
        var window = new Queue<byte[]>(Config.Gen1HoldCount + 1);

        for (int i = 0; i < Config.Gen1Iterations; i++)
        {
            byte[] buffer = pool.Rent(200);
            buffer[0] = (byte)(i & 0xFF);
            window.Enqueue(buffer);

            if (window.Count > Config.Gen1HoldCount)
            {
                byte[] old = window.Dequeue();
                pool.Return(old);
            }
        }

        // Drain remaining buffers back to the pool.
        while (window.Count > 0)
        {
            pool.Return(window.Dequeue());
        }
    }

    // --------------------------------------------------------------------
    // Optimized LOH: ArrayPool eliminates large allocations
    // --------------------------------------------------------------------
    // ArrayPool<byte>.Shared maintains buckets of pre-allocated arrays
    // up to a configurable maximum size.  By renting and returning the
    // same large buffer repeatedly, we avoid LOH allocations entirely
    // after the pool's internal bucket is populated.
    //
    // The rented array may be larger than requested (pools round up to
    // the next bucket size), so always use the requested length — not
    // array.Length — when processing data.
    //
    // Result: zero Gen2/LOH collections from this workload.
    // --------------------------------------------------------------------
    public static void OptimizedLOH()
    {
        var pool = ArrayPool<byte>.Shared;
        long sum = 0;

        for (int i = 0; i < Config.LOHIterations; i++)
        {
            byte[] largeBuffer = pool.Rent(Config.LOHArraySize);
            largeBuffer[0] = (byte)(i & 0xFF);
            sum += largeBuffer[0];
            pool.Return(largeBuffer);
        }

        GC.KeepAlive(sum);
    }
}

// ============================================================================
// PerfView Analysis Guide
// ============================================================================
//
// PerfView is a free, lightweight ETW-based profiler from Microsoft that
// excels at GC and allocation analysis.  Follow these steps to analyze
// the memory behavior of this exercise.
//
// 1. COLLECTING A TRACE
//    Open a command prompt as Administrator and run:
//
//      PerfView.exe /GCCollectOnly collect
//
//    This starts an ETW session that captures GC events with minimal
//    overhead (no CPU sampling, no allocation stacks — just GC stats).
//    Run this exercise program while the trace is active, then press
//    's' in PerfView to stop collection.
//
//    For allocation call stacks (heavier overhead, but more detail):
//
//      PerfView.exe /GCOnly collect
//
// 2. OPENING THE GCStats VIEW
//    In PerfView, double-click the .etl file, then open:
//      Memory Group > GCStats
//    Select the process (Exercise03.PerfViewAnalysis).
//
// 3. WHAT TO LOOK FOR IN GCStats
//
//    a) GC Generation Counts
//       - Pattern 1 (Gen0 pressure): high Gen0 count, low Gen1/Gen2.
//       - Pattern 2 (Gen1 promotion): elevated Gen1 count relative to
//         the optimized version.
//       - Pattern 3 (LOH pressure): Gen2 collections triggered by LOH
//         exhaustion.
//       - Optimized versions: near-zero collections across all gens.
//
//    b) Pause Times
//       The "Pause MSec" columns show how long each GC paused managed
//       threads.  Gen0 pauses are typically < 1 ms; Gen2/LOH pauses
//       can be 10-100+ ms depending on heap size.
//
//    c) Promoted Bytes
//       High promotion from Gen0 to Gen1 confirms Pattern 2.  The
//       optimized version should show minimal promotion because pooled
//       arrays are long-lived (they stay in Gen2 inside the pool) and
//       no new objects are created.
//
//    d) Allocation Rate (MB/sec)
//       Found in the summary table.  The unoptimized patterns will
//       show allocation rates of hundreds of MB/sec; the optimized
//       versions should be near zero.
//
// 4. GC HEAP ALLOC IGNORE FREE (ALLOCATION STACKS)
//    If you collected with /GCOnly (not /GCCollectOnly), open:
//      Memory Group > GC Heap Alloc Ignore Free (Coarse Sampling)
//    This view shows which call sites allocated the most bytes.  You
//    will see:
//      - SimulateGen0Pressure > new byte[100]
//      - SimulateGen1Promotion > new byte[200]
//      - SimulateLOHPressure > new byte[100000]
//    The optimized methods will show allocations only from ArrayPool's
//    internal bucket initialization — not from the hot loop.
//
// 5. VALIDATING THE FIX
//    Compare the GCStats summary for the unoptimized run vs. the
//    optimized run.  Key metrics to compare:
//      - Total GC pause time (should drop by 90%+)
//      - Number of Gen1 and Gen2 collections (should approach zero)
//      - Peak working set (should be lower and more stable)
//
//    The optimized version demonstrates the principle: the fastest GC
//    is the one that never runs, because the best allocation is the
//    one that never happens.
//
// ============================================================================
