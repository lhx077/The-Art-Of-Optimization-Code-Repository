using System.Diagnostics;
using System.Runtime;

// ============================================================================
// Exercise 05 — No-GC Region in Practice
// ============================================================================
// GC.TryStartNoGCRegion asks the runtime to pre-commit a chunk of memory from
// the managed heap so that subsequent allocations can be satisfied without
// triggering a garbage collection. This is the .NET equivalent of "locking out
// the GC" for a bounded window of work.
//
// How it works:
//   1. You specify a byte budget (the total SOH allocation you plan to make).
//   2. The CLR performs a full blocking GC, then reserves that much free space.
//   3. While the region is active, allocations come from the reserved space
//      with zero risk of a collection pause.
//   4. You call GC.EndNoGCRegion() when done.
//
// The budget parameter controls how much Small Object Heap (SOH) memory is
// reserved. An overload also accepts a separate LOH budget for large (>85 KB)
// allocations. If your allocations exceed the budget, the CLR silently exits
// the no-GC region and may trigger a collection — the region is "broken."
//
// Why avoid async/await inside a no-GC region:
//   - The compiler-generated async state machine allocates on every await.
//   - Continuation scheduling is unpredictable — the resumed code may run on
//     a different thread, but the no-GC region is thread-local.
//   - These hidden allocations make it impossible to predict the budget.
//
// Practical use cases:
//   - Real-time audio buffer processing (glitch-free playback)
//   - Game engine frame updates (consistent 16 ms frames)
//   - High-frequency trading tick handlers (microsecond-level latency)
//
// This is NOT a general-purpose optimization. It is strictly for latency-
// critical code paths where you can predict every allocation in advance.
// ============================================================================

Console.WriteLine("=== Exercise 05: No-GC Region in Practice ===");
Console.WriteLine();

// ── Part 1: Successful NoGCRegion ──────────────────────────────────────────
Part1_SuccessfulNoGCRegion();

Console.WriteLine();

// ── Part 2: Budget Exceeded ────────────────────────────────────────────────
Part2_BudgetExceeded();

Console.WriteLine();
// ── Part 3: Practical Latency-Sensitive Scenario ───────────────────────────
Part3_LatencySensitiveScenario();

// ============================================================================
// Part 1 — Allocate within budget, verify zero collections
// ============================================================================
// We request a 4 MB no-GC region, then allocate several small arrays whose
// combined size is well under the budget. After the region ends we confirm
// that no generation was collected during the window.
// ============================================================================

static void Part1_SuccessfulNoGCRegion()
{
    Console.WriteLine("── Part 1: Successful NoGCRegion ──");

    const long budget = 4 * 1024 * 1024; // 4 MB — generous for our small arrays

    // Snapshot GC counts before entering the region.
    int gen0Before = GC.CollectionCount(0);
    int gen1Before = GC.CollectionCount(1);
    int gen2Before = GC.CollectionCount(2);

    bool entered = GC.TryStartNoGCRegion(budget);
    if (!entered)
    {
        Console.WriteLine("  [WARN] Could not enter no-GC region (runtime refused).");
        return;
    }

    Console.WriteLine($"  Entered no-GC region with {budget / 1024 / 1024} MB budget.");

    // Perform predictable allocations — 10 arrays of 100 KB each = 1 MB total.
    // Each byte[] has a small object header (~26 bytes overhead on 64-bit),
    // but the dominant cost is the payload. 1 MB is well within our 4 MB budget.
    const int arrayCount = 10;
    const int arraySize = 100 * 1024; // 100 KB per array
    byte[][] buffers = new byte[arrayCount][];

    for (int i = 0; i < arrayCount; i++)
    {
        buffers[i] = new byte[arraySize];
        // Touch the memory so the JIT doesn't optimize away the allocation.
        buffers[i][0] = (byte)(i & 0xFF);
    }

    long totalAllocated = (long)arrayCount * arraySize;
    Console.WriteLine($"  Allocated {arrayCount} arrays x {arraySize / 1024} KB = {totalAllocated / 1024} KB total.");

    // Verify that no GC occurred while we were inside the region.
    int gen0After = GC.CollectionCount(0);
    int gen1After = GC.CollectionCount(1);
    int gen2After = GC.CollectionCount(2);

    bool noGcOccurred = gen0After == gen0Before
                     && gen1After == gen1Before
                     && gen2After == gen2Before;

    Console.WriteLine($"  GC counts — Gen0: {gen0Before}→{gen0After}, "
                    + $"Gen1: {gen1Before}→{gen1After}, "
                    + $"Gen2: {gen2Before}→{gen2After}");
    Console.WriteLine(noGcOccurred
        ? "  [OK] No garbage collection occurred inside the region."
        : "  [UNEXPECTED] A GC was triggered despite the no-GC region.");

    // Exit the no-GC region. This releases the reservation and allows the
    // GC to resume normal operation.
    GC.EndNoGCRegion();
    Console.WriteLine("  Exited no-GC region successfully.");

    // Keep buffers alive until after EndNoGCRegion to prevent the JIT from
    // optimizing away the allocations.
    GC.KeepAlive(buffers);
}

// ============================================================================
// Part 2 — Deliberately exceed the budget
// ============================================================================
// We request a tiny 1 MB region, then allocate far more than that. When the
// CLR can no longer satisfy allocations from the reserved space it silently
// breaks out of the no-GC region and may trigger a collection. Calling
// GC.EndNoGCRegion() after the region was broken throws
// InvalidOperationException — we catch and report it.
// ============================================================================

static void Part2_BudgetExceeded()
{
    Console.WriteLine("── Part 2: Budget Exceeded ──");

    const long smallBudget = 1 * 1024 * 1024; // 1 MB — intentionally tight

    int gen0Before = GC.CollectionCount(0);

    bool entered = GC.TryStartNoGCRegion(smallBudget);

    if (!entered)
    {
        Console.WriteLine("  [WARN] Could not enter no-GC region.");
        return;
    }

    Console.WriteLine($"  Entered no-GC region with {smallBudget / 1024} KB budget.");
    Console.WriteLine("  Now allocating well beyond the budget...");

    // Allocate 50 arrays of 100 KB each = 5 MB, far exceeding the 1 MB budget.
    // At some point the CLR will break out of the no-GC region.
    const int overAllocCount = 50;
    const int overAllocSize = 100 * 1024;
    List<byte[]> overflow = new(overAllocCount);

    for (int i = 0; i < overAllocCount; i++)
    {
        overflow.Add(new byte[overAllocSize]);
        overflow[i][0] = (byte)i;
    }

    long totalOver = (long)overAllocCount * overAllocSize;
    Console.WriteLine($"  Allocated {totalOver / 1024} KB (budget was {smallBudget / 1024} KB).");

    int gen0After = GC.CollectionCount(0);
    Console.WriteLine($"  Gen0 collections: {gen0Before}→{gen0After} "
                    + $"(delta: {gen0After - gen0Before})");

    // Try to end the no-GC region. If the CLR already broke out because we
    // exceeded the budget, EndNoGCRegion throws InvalidOperationException.
    try
    {
        GC.EndNoGCRegion();
        // If we reach here, the runtime somehow kept the region alive despite
        // exceeding the budget — unlikely but possible on some configurations.
        Console.WriteLine("  [INFO] EndNoGCRegion succeeded (region was not broken).");
    }
    catch (InvalidOperationException ex)
    {
        // This is the expected path: the region was broken when allocations
        // exceeded the budget, so we are no longer in a no-GC region.
        Console.WriteLine($"  [EXPECTED] EndNoGCRegion threw: {ex.Message}");
        Console.WriteLine("  The CLR exited the no-GC region when the budget was exceeded.");
    }

    GC.KeepAlive(overflow);
}

// ============================================================================
// Part 3 — Practical latency-sensitive scenario
// ============================================================================
// Simulates a real-time audio/game frame processing pipeline:
//   1. Pre-allocate reusable buffers outside the critical section.
//   2. Enter a no-GC region with a known budget.
//   3. Process N "frames," each requiring a small temporary allocation.
//   4. Exit the region and report timing.
//
// In a real audio callback (e.g., WASAPI exclusive mode at 48 kHz / 10 ms
// buffer), a single GC pause of even 5 ms causes an audible glitch. By
// entering a no-GC region for the duration of the buffer fill, we guarantee
// deterministic latency as long as our allocations stay within budget.
// ============================================================================

static void Part3_LatencySensitiveScenario()
{
    Console.WriteLine("── Part 3: Latency-Sensitive Frame Processing ──");

    const int frameCount = 100;
    const int samplesPerFrame = 480;       // 10 ms at 48 kHz
    // Each float[] of 480 elements = 480 * 4 bytes = 1,920 bytes payload.
    // Budget: 100 frames x ~2 KB ≈ 200 KB; we reserve 1 MB for safety,
    // accounting for array headers and alignment padding.
    const long budget = 1 * 1024 * 1024;

    // Pre-allocate the output buffer that accumulates all frames.
    float[] outputBuffer = new float[frameCount * samplesPerFrame];

    // Force a GC now so the upcoming region starts with a clean heap.
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();

    int gen0Before = GC.CollectionCount(0);

    bool entered = GC.TryStartNoGCRegion(budget);

    if (!entered)
    {
        Console.WriteLine("  [WARN] Could not enter no-GC region.");
        return;
    }

    Stopwatch sw = Stopwatch.StartNew();

    // Simulate processing N audio frames. Each frame creates a temporary
    // float[] to hold intermediate sample data (e.g., applying a filter),
    // then copies the result into the output buffer.
    for (int frame = 0; frame < frameCount; frame++)
    {
        // This allocation is the reason we need the no-GC region — in a
        // normal GC environment, any of these could trigger a collection.
        float[] temp = new float[samplesPerFrame];

        // Simulate DSP work: generate a simple sine wave at 440 Hz.
        for (int s = 0; s < samplesPerFrame; s++)
        {
            double t = (frame * samplesPerFrame + s) / 48000.0;
            temp[s] = (float)Math.Sin(2.0 * Math.PI * 440.0 * t);
        }

        // Copy processed samples into the output buffer.
        Array.Copy(temp, 0, outputBuffer, frame * samplesPerFrame, samplesPerFrame);
    }

    sw.Stop();

    int gen0After = GC.CollectionCount(0);

    // End the no-GC region before printing results.
    GC.EndNoGCRegion();

    bool noGc = gen0After == gen0Before;
    double totalMs = sw.Elapsed.TotalMilliseconds;
    double perFrameUs = totalMs / frameCount * 1000.0;

    Console.WriteLine($"  Processed {frameCount} frames ({samplesPerFrame} samples each).");
    Console.WriteLine($"  Total time: {totalMs:F3} ms  |  Per frame: {perFrameUs:F1} µs");
    Console.WriteLine($"  Gen0 collections during region: {gen0After - gen0Before}");
    Console.WriteLine(noGc
        ? "  [OK] Zero GC pauses — deterministic latency achieved."
        : "  [WARN] GC occurred — budget may have been too small.");
    Console.WriteLine();
    Console.WriteLine("  In a real audio pipeline, this guarantees glitch-free playback");
    Console.WriteLine("  for the duration of the buffer fill, as long as allocations");
    Console.WriteLine("  stay within the pre-committed budget.");

    GC.KeepAlive(outputBuffer);
}