using System.Diagnostics;
using System.Runtime;

// ============================================================================
// Exercise 03 — GC Mode and Latency Strategy Stress Test
// ============================================================================
//
// This program creates sustained memory pressure by rapidly allocating and
// discarding short-lived byte arrays, then measures how the garbage collector
// responds under each GCLatencyMode. The goal is to build intuition for how
// GC tuning knobs affect throughput and pause behavior.
//
// --------------------------------------------------------------------------
// Background: Workstation GC vs. Server GC
// --------------------------------------------------------------------------
//
// Workstation GC (default for console/desktop apps):
//   - Uses a SINGLE dedicated GC thread.
//   - Concurrent (background) GC is enabled by default, meaning Gen 2
//     collections can run concurrently with application threads.
//   - Optimized for responsiveness: shorter individual pauses at the cost
//     of lower overall throughput.
//   - Best for client applications where UI responsiveness matters.
//
// Server GC (default for ASP.NET / server workloads):
//   - Creates ONE GC thread per logical processor, each with its own heap.
//   - Higher throughput because collections are parallelized across cores.
//   - Longer stop-the-world pauses because all managed threads must be
//     suspended and all heaps collected simultaneously.
//   - Best for server workloads that need maximum request throughput and
//     can tolerate occasional longer pauses.
//
// To switch between them, set <ServerGarbageCollection> in the .csproj:
//   <ServerGarbageCollection>true</ServerGarbageCollection>   → Server GC
//   <ServerGarbageCollection>false</ServerGarbageCollection>  → Workstation GC
//
// --------------------------------------------------------------------------
// Background: GCLatencyMode
// --------------------------------------------------------------------------
//
// GCLatencyMode controls how aggressively the GC reclaims memory:
//
// Batch:
//   - Disables concurrent/background GC entirely.
//   - All collections are blocking (stop-the-world).
//   - Maximizes throughput because there is no overhead from concurrent
//     GC bookkeeping, but produces the longest pauses.
//   - Ideal for batch processing where latency is irrelevant.
//
// Interactive (default):
//   - Concurrent/background GC is enabled.
//   - Gen 0 and Gen 1 collections are still blocking but very fast.
//   - Gen 2 collections run in the background, allowing application
//     threads to continue executing during most of the collection.
//   - Good balance between throughput and pause times.
//
// LowLatency:
//   - Suppresses full blocking Gen 2 collections as much as possible.
//   - Gen 0 and Gen 1 collections still occur normally.
//   - Gen 2 collections only happen if the OS signals low memory.
//   - WARNING: Use only for short critical sections (e.g., an animation
//     frame or a real-time trading window). Prolonged use risks
//     OutOfMemoryException because Gen 2 objects accumulate.
//
// SustainedLowLatency:
//   - Suppresses BLOCKING Gen 2 collections, but background Gen 2
//     collections still run (if concurrent GC is enabled).
//   - Safer than LowLatency for longer durations because background
//     GC still reclaims Gen 2 memory, just without stopping the world.
//   - Suitable for scenarios like financial trading or game servers
//     where you need consistently low latency over minutes or hours.
//
// NoGCRegion (not tested here):
//   - Completely prevents GC for a specified allocation budget.
//   - Used via GC.TryStartNoGCRegion() / GC.EndNoGCRegion().
//   - Extreme measure for ultra-low-latency critical paths.
//
// ============================================================================

// ---------------------------------------------------------------------------
// Print GC configuration
// ---------------------------------------------------------------------------
Console.WriteLine("============================================================");
Console.WriteLine("  GC Mode and Latency Strategy Stress Test");
Console.WriteLine("============================================================");
Console.WriteLine();
Console.WriteLine($"  GC Mode          : {(GCSettings.IsServerGC ? "Server" : "Workstation")}");
Console.WriteLine($"  Logical CPUs      : {Environment.ProcessorCount}");
Console.WriteLine($"  Current Latency   : {GCSettings.LatencyMode}");
Console.WriteLine($"  64-bit Process    : {Environment.Is64BitProcess}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Run stress test under each latency mode
// ---------------------------------------------------------------------------
// We test each mode sequentially. Before each run we force a full GC to
// start from a clean baseline, then record Gen 0/1/2 collection counts
// before and after the stress period.

const int testDurationSeconds = 5;

GCLatencyMode[] modes =
[
    GCLatencyMode.Batch,
    GCLatencyMode.Interactive,
    GCLatencyMode.LowLatency,
    GCLatencyMode.SustainedLowLatency
];

var results = new List<StressTestResult>();

foreach (var mode in modes)
{
    var result = StressTest.Run(mode, testDurationSeconds);
    results.Add(result);
}

// ---------------------------------------------------------------------------
// Print comparison table
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("========================== Results ==========================");
Console.WriteLine(
    string.Format("{0,-24} {1,10} {2,7} {3,7} {4,7} {5,12} {6,10}",
        "Mode", "Allocs", "Gen0", "Gen1", "Gen2", "Allocs/s", "Elapsed"));
Console.WriteLine(new string('-', 82));

foreach (var r in results)
{
    Console.WriteLine(
        $"{r.Mode,-24} {r.TotalAllocations,10:N0} {r.Gen0Collections,7} " +
        $"{r.Gen1Collections,7} {r.Gen2Collections,7} " +
        $"{r.AllocationsPerSecond,12:N0} {r.Elapsed.TotalSeconds,9:F3}s");
}

Console.WriteLine();
Console.WriteLine("------------------------------------------------------------");
Console.WriteLine("  Observations to look for:");
Console.WriteLine("  - Batch mode typically shows highest throughput (allocs/s)");
Console.WriteLine("    because there is zero concurrent GC overhead.");
Console.WriteLine("  - LowLatency / SustainedLowLatency suppress Gen 2");
Console.WriteLine("    collections, so Gen2 count should be 0 or very low.");
Console.WriteLine("  - Interactive is the balanced default.");
Console.WriteLine("------------------------------------------------------------");

Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine("  HOW TO COMPARE SERVER vs. WORKSTATION GC");
Console.WriteLine("============================================================");
Console.WriteLine("  1. Open Exercise03.GCModeLatency.csproj");
Console.WriteLine("  2. Change <ServerGarbageCollection> to true");
Console.WriteLine("  3. Rebuild and re-run: dotnet run -c Release");
Console.WriteLine("  4. Compare the results table with the Workstation run.");
Console.WriteLine();
Console.WriteLine("  With Server GC you should see:");
Console.WriteLine("  - Higher throughput (more allocs/s) due to parallel heaps");
Console.WriteLine("  - Potentially fewer Gen 0 collections (larger budget)");
Console.WriteLine("  - Higher memory usage (one heap segment per CPU)");
Console.WriteLine("============================================================");

// ---------------------------------------------------------------------------
// Data record for stress test results
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable snapshot of a single stress test run, capturing allocation
/// counts, GC generation statistics, and timing information.
/// </summary>
record StressTestResult(
    string Mode,
    long TotalAllocations,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double AllocationsPerSecond,
    TimeSpan Elapsed);

// ---------------------------------------------------------------------------
// StressTest — the core allocation loop
// ---------------------------------------------------------------------------

/// <summary>
/// Runs a tight allocation loop under a specified <see cref="GCLatencyMode"/>
/// and returns detailed metrics about GC behavior during the test.
/// </summary>
static class StressTest
{
    /// <summary>
    /// Executes the stress test for <paramref name="durationSeconds"/> under
    /// the given <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// The method:
    ///   1. Forces a full GC (Gen 0 + 1 + 2) and waits for finalizers to
    ///      establish a clean baseline.
    ///   2. Records the current GC.CollectionCount for each generation.
    ///   3. Switches GCSettings.LatencyMode to the requested mode.
    ///   4. Allocates random-sized byte arrays (1 KB – 10 KB) in a tight
    ///      loop. These are short-lived: each array becomes garbage on the
    ///      next iteration because the local variable is overwritten.
    ///   5. After the time budget expires, restores the original latency
    ///      mode and computes deltas for all metrics.
    ///
    /// The random sizes prevent the JIT or GC from optimizing away the
    /// allocations and ensure a realistic distribution of object sizes
    /// within the Gen 0 nursery.
    /// </remarks>
    public static StressTestResult Run(GCLatencyMode mode, int durationSeconds)
    {
        Console.Write($"  Testing {mode,-24} ... ");

        // ------------------------------------------------------------------
        // Step 1: Clean baseline — force a full compacting collection.
        // GC.Collect() triggers a blocking Gen 2 collection.
        // GC.WaitForPendingFinalizers() ensures all weak references and
        // pointers are cleaned up before we start measuring.
        // ------------------------------------------------------------------
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // ------------------------------------------------------------------
        // Step 2: Snapshot baseline GC collection counts.
        // GC.CollectionCount(generation) returns the cumulative number of
        // times that generation has been collected since process start.
        // By recording before and after, we get the delta for this test.
        // ------------------------------------------------------------------
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        // ------------------------------------------------------------------
        // Step 3: Switch latency mode.
        // We save the original mode so we can restore it after the test.
        // Changing LatencyMode is thread-safe and takes effect immediately.
        // ------------------------------------------------------------------
        GCLatencyMode originalMode = GCSettings.LatencyMode;

        try
        {
            GCSettings.LatencyMode = mode;
        }
        catch (InvalidOperationException)
        {
            // Some modes (e.g., LowLatency) are not available under Server GC
            // in certain configurations. Fall back gracefully.
            Console.WriteLine("SKIPPED (mode not supported in current GC config)");
            return new StressTestResult(
                mode.ToString(), 0, 0, 0, 0, 0, TimeSpan.Zero);
        }

        // ------------------------------------------------------------------
        // Step 4: Allocation loop.
        // We allocate byte arrays between 1 KB and 10 KB. These sizes are
        // small enough to stay in Gen 0 (the nursery), which means they
        // will be collected very quickly — exactly the scenario where GC
        // latency mode differences become visible.
        //
        // The Random instance is seeded per-run for reproducibility within
        // a single process, but the exact seed doesn't matter for our
        // measurements.
        // ------------------------------------------------------------------
        var random = new Random(42);
        long totalAllocations = 0;
        var stopwatch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(durationSeconds);

        while (stopwatch.Elapsed < deadline)
        {
            // Allocate a batch of 100 arrays per outer iteration to reduce
            // the overhead of checking Stopwatch.Elapsed on every allocation.
            for (int i = 0; i < 100; i++)
            {
                // Random size between 1024 (1 KB) and 10240 (10 KB).
                int size = random.Next(1024, 10240);

                // The array is allocated on the managed heap. Because we
                // never store it beyond this iteration, it becomes garbage
                // immediately — a classic Gen 0 short-lived object.
                byte[] buffer = new byte[size];

                // Touch the first and last bytes to prevent the JIT from
                // optimizing away the allocation entirely.
                buffer[0] = 0xFF;
                buffer[^1] = 0xFE;

                totalAllocations++;
            }
        }

        stopwatch.Stop();

        // ------------------------------------------------------------------
        // Step 5: Restore original latency mode and compute deltas.
        // Always restore in a finally-like fashion to avoid leaving the
        // process in an unexpected GC configuration.
        // ------------------------------------------------------------------
        GCSettings.LatencyMode = originalMode;

        int gen0Delta = GC.CollectionCount(0) - gen0Before;
        int gen1Delta = GC.CollectionCount(1) - gen1Before;
        int gen2Delta = GC.CollectionCount(2) - gen2Before;

        double allocsPerSecond = totalAllocations / stopwatch.Elapsed.TotalSeconds;

        // ------------------------------------------------------------------
        // Step 6: Print per-generation GC memory info.
        // GC.GetGCMemoryInfo() provides rich data including pause times,
        // heap sizes, and fragmentation. We use it here to show the total
        // pause duration the GC imposed during this test window.
        // ------------------------------------------------------------------
        GCMemoryInfo memInfo = GC.GetGCMemoryInfo();

        Console.WriteLine(
            $"done  |  {totalAllocations,10:N0} allocs  |  " +
            $"Gen0={gen0Delta}  Gen1={gen1Delta}  Gen2={gen2Delta}  |  " +
            $"{allocsPerSecond:N0} allocs/s  |  " +
            $"Pause: {memInfo.PauseTimePercentage:F2}%");

        return new StressTestResult(
            mode.ToString(),
            totalAllocations,
            gen0Delta,
            gen1Delta,
            gen2Delta,
            allocsPerSecond,
            stopwatch.Elapsed);
    }
}
