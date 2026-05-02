// ============================================================================
// Exercise 06 — Memory Model: Store-Load Reordering and Memory Barriers
// ============================================================================
//
// Modern CPUs do not execute memory operations in strict program order. To
// maximize throughput, the processor may reorder loads and stores as long as
// the reordering is invisible to the *current* thread. However, when multiple
// threads share data without synchronization, these reorderings become visible
// and can produce results that no sequential interleaving of instructions
// could explain.
//
// WHAT IS STORE-LOAD REORDERING?
// A store-load reorder occurs when the CPU executes a later load *before* an
// earlier store has been committed to the cache/memory. The store sits in the
// CPU's store buffer waiting to be flushed, while the load bypasses it and
// reads a stale value from the cache. From the perspective of other threads,
// the load appears to have happened before the store.
//
// THE x86 MEMORY MODEL (Total Store Order — TSO):
// Intel and AMD processors implement TSO, which is one of the strongest
// hardware memory models. Under TSO:
//   - Loads are NOT reordered with other loads.
//   - Stores are NOT reordered with other stores.
//   - Loads are NOT reordered with earlier stores to the SAME location.
//   - BUT: Loads CAN be reordered with earlier stores to DIFFERENT locations.
// This last point — store-load reordering — is the ONLY reordering permitted
// on x86. Even so, it is relatively rare in practice because the store buffer
// is small and drains quickly. You may need millions of iterations to observe
// even a handful of reorderings, and on some x86 CPUs you may observe zero.
//
// ARM AND OTHER WEAKLY-ORDERED ARCHITECTURES:
// ARM processors have a much weaker memory model. They permit all four types
// of reordering (load-load, load-store, store-load, store-store). On ARM,
// this experiment would detect reorderings far more frequently — potentially
// thousands or tens of thousands per million iterations.
//
// WHAT Thread.MemoryBarrier() DOES:
// Thread.MemoryBarrier() emits a full memory fence (MFENCE on x86, DMB on
// ARM). A full fence guarantees that all loads and stores issued before the
// barrier are completed before any loads or stores issued after the barrier
// begin. It prevents ALL types of reordering across the barrier point.
// On x86, this specifically drains the store buffer, ensuring that the store
// is visible to other cores before the subsequent load executes.
//
// VOLATILE vs. LOCK vs. MEMORY BARRIER:
//   - volatile (C# keyword): Provides acquire semantics on reads (no later
//     memory operation can be moved before the read) and release semantics on
//     writes (no earlier memory operation can be moved after the write). This
//     is weaker than a full fence — it does NOT prevent store-load reordering.
//     A volatile write followed by a volatile read can still be reordered.
//   - Thread.MemoryBarrier(): A full fence that prevents all reordering. It
//     is the minimum tool needed to prevent store-load reordering.
//   - lock (Monitor): Provides full mutual exclusion AND a full fence on both
//     entry and exit. It is the strongest (and most expensive) synchronization
//     primitive. It prevents all reordering and also ensures atomicity of the
//     critical section.
//
// THE EXPERIMENT:
// We use Dekker's classic litmus test. Two threads execute simultaneously:
//   Thread 1: x = 1;  r1 = y;
//   Thread 2: y = 1;  r2 = x;
//
// Under sequential consistency, at least one thread must see the other's
// store, so (r1 == 0 AND r2 == 0) is impossible. But if both CPUs reorder
// their load before their store (store-load reorder), both threads read the
// stale value 0, producing the "impossible" result.
//
// NOTE: Even if you observe zero reorderings on your x86 machine, the
// experiment is still valuable. The .NET memory model does NOT guarantee
// sequential consistency, so a conforming runtime on ARM or a future x86
// microarchitecture could produce reorderings. Writing correct lock-free
// code requires barriers regardless of what you observe empirically.
// ============================================================================

using System.Diagnostics;

// ============================================================================
// Main — Run Both Experiments
// ============================================================================

const int iterations = 1_000_000;

Console.WriteLine("============================================================================");
Console.WriteLine(" Exercise 06 — Memory Model: Store-Load Reordering Detection");
Console.WriteLine("============================================================================");
Console.WriteLine();
Console.WriteLine($"Running Dekker's litmus test for {iterations:N0} iterations...");
Console.WriteLine("(This may take a while — each iteration spawns two threads.)\n");

// --- Without barrier ---
Console.WriteLine("--- Without Memory Barrier ---");
var sw = Stopwatch.StartNew();
int reordersNoBarrier = ReorderingDetector.RunExperiment(iterations, useBarrier: false);
sw.Stop();
Console.WriteLine($"  Reorderings detected: {reordersNoBarrier:N0} / {iterations:N0}");
Console.WriteLine($"  Time elapsed:         {sw.Elapsed.TotalSeconds:F1} seconds");
Console.WriteLine();

// --- With barrier ---
Console.WriteLine("--- With Thread.MemoryBarrier() ---");
sw.Restart();
int reordersWithBarrier = ReorderingDetector.RunExperiment(iterations, useBarrier: true);
sw.Stop();
Console.WriteLine($"  Reorderings detected: {reordersWithBarrier:N0} / {iterations:N0}");
Console.WriteLine($"  Time elapsed:         {sw.Elapsed.TotalSeconds:F1} seconds");
Console.WriteLine();

// --- Interpretation ---
Console.WriteLine("--- Interpretation ---");
if (reordersNoBarrier > 0)
{
    Console.WriteLine($"  Store-load reordering was observed {reordersNoBarrier:N0} times without a barrier.");
    Console.WriteLine("  This proves that the CPU reordered a load before an earlier store.");
}
else
{
    Console.WriteLine("  No reorderings were detected without a barrier.");
    Console.WriteLine("  On x86 (TSO), this is common — store-load reordering is permitted");
    Console.WriteLine("  but rare because the store buffer drains quickly. On ARM, you would");
    Console.WriteLine("  likely see thousands of reorderings.");
}

if (reordersWithBarrier == 0)
{
    Console.WriteLine("  With Thread.MemoryBarrier(), zero reorderings were detected, as expected.");
    Console.WriteLine("  The full fence ensures the store is visible before the load executes.");
}
else
{
    // This should never happen — if it does, something is very wrong.
    Console.WriteLine($"  WARNING: {reordersWithBarrier:N0} reorderings detected even WITH a barrier!");
    Console.WriteLine("  This is unexpected and may indicate a bug in the test harness.");
}

Console.WriteLine();
Console.WriteLine("  KEY TAKEAWAY: Even if zero reorderings are observed on your hardware,");
Console.WriteLine("  the .NET memory model does not guarantee sequential consistency.");
Console.WriteLine("  Correct lock-free code must use barriers, volatile, or locks to");
Console.WriteLine("  ensure cross-thread visibility — regardless of empirical results.");

// --- Volatile demo ---
var volatileDemo = new VolatileDemo();
volatileDemo.Run();

Console.WriteLine();
Console.WriteLine("============================================================================");
Console.WriteLine(" Experiment complete.");
Console.WriteLine("============================================================================");

// ============================================================================
// Part 1 — Store-Load Reordering Detection (Dekker's Litmus Test)
// ============================================================================

static class ReorderingDetector
{
    // Shared variables. Deliberately NOT volatile — we want the CPU and
    // compiler to be free to reorder accesses to these fields.
    private static int x;
    private static int y;

    // Result registers. Each thread writes its observed value here.
    private static int r1;
    private static int r2;

    /// <summary>
    /// Runs the Dekker litmus test for the specified number of iterations.
    /// Returns the number of times store-load reordering was detected
    /// (i.e., both r1 == 0 and r2 == 0 after both threads complete).
    /// </summary>
    /// <param name="iterations">Number of trials to run.</param>
    /// <param name="useBarrier">
    /// When true, a Thread.MemoryBarrier() is inserted between the store
    /// and the load in each thread, which should eliminate reorderings.
    /// </param>
    public static int RunExperiment(int iterations, bool useBarrier)
    {
        int reorderCount = 0;

        for (int i = 0; i < iterations; i++)
        {
            // Reset shared state before each trial.
            x = 0;
            y = 0;

            // A Barrier with 2 participants ensures both threads begin their
            // critical section at the same instant, maximizing the window in
            // which a store-load reorder can be observed.
            using var barrier = new Barrier(2);

            var t1 = new Thread(() =>
            {
                barrier.SignalAndWait(); // Wait for both threads to be ready.

                // STORE x, then LOAD y.
                x = 1;

                if (useBarrier)
                {
                    // Full fence: drain the store buffer so that x = 1 is
                    // visible to all cores before we read y.
                    Thread.MemoryBarrier();
                }

                r1 = y;
            });

            var t2 = new Thread(() =>
            {
                barrier.SignalAndWait(); // Wait for both threads to be ready.

                // STORE y, then LOAD x.
                y = 1;

                if (useBarrier)
                {
                    // Full fence: drain the store buffer so that y = 1 is
                    // visible to all cores before we read x.
                    Thread.MemoryBarrier();
                }

                r2 = x;
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // If both loads returned 0, both stores were still sitting in
            // their respective store buffers when the loads executed — a
            // store-load reorder on both CPUs simultaneously.
            if (r1 == 0 && r2 == 0)
            {
                reorderCount++;
            }
        }

        return reorderCount;
    }
}

// ============================================================================
// Part 2 — Volatile Field Demonstration
// ============================================================================
//
// This class demonstrates why volatile matters for cross-thread visibility.
// Without volatile, the JIT compiler is free to hoist the field read out of
// the loop, turning it into an infinite loop. With volatile, every read goes
// through to memory (or at least respects acquire semantics), so the thread
// sees the updated value.
//
// IMPORTANT CAVEAT ON x86:
// On x86, the hardware memory model already provides strong ordering for
// loads, so the JIT's register caching is the primary source of the bug —
// not CPU reordering. In Debug builds, the JIT does not optimize aggressively,
// so the loop may terminate even without volatile. You must run in Release
// mode (dotnet run -c Release) to observe the hoisting behavior.
//
// On ARM, both the JIT hoisting AND the weaker hardware model contribute,
// making the bug much easier to reproduce.
// ============================================================================

class VolatileDemo
{
    // Non-volatile field. The JIT may cache this in a register, causing the
    // spinning thread to never see the update from another thread.
    private bool _running = true;

    // If this were declared as:
    //   private volatile bool _running = true;
    // the JIT would emit a load with acquire semantics on every iteration,
    // guaranteeing that the thread eventually sees the updated value.

    public void Run()
    {
        Console.WriteLine("\n--- Volatile Demonstration ---");
        Console.WriteLine("Starting a spin loop on a non-volatile bool field.");
        Console.WriteLine("Another thread will set the field to false after 2 seconds.");
        Console.WriteLine("In Release mode, the JIT may hoist the read out of the loop,");
        Console.WriteLine("causing the loop to spin forever. We impose a 5-second timeout.\n");

        var sw = Stopwatch.StartNew();

        var spinner = new Thread(() =>
        {
            // The JIT (in Release mode) may optimize this to:
            //   bool cached = _running;
            //   while (cached) { }
            // which never terminates because the cached value is never refreshed.
            while (_running)
            {
                // Busy-wait. No Thread.MemoryBarrier() or volatile read here.
            }
        });

        spinner.IsBackground = true; // Ensure it doesn't prevent process exit.
        spinner.Start();

        // Give the spinner time to enter its loop and for the JIT to optimize.
        Thread.Sleep(2000);

        // Update the field from this thread.
        _running = false;

        // Wait up to 5 seconds for the spinner to notice.
        bool terminated = spinner.Join(TimeSpan.FromSeconds(5));
        sw.Stop();

        if (terminated)
        {
            Console.WriteLine($"  Spinner thread terminated after {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine("  The JIT did NOT hoist the field read (common in Debug builds");
            Console.WriteLine("  or when the JIT decides not to optimize this particular loop).");
        }
        else
        {
            Console.WriteLine($"  Spinner thread did NOT terminate within 5 seconds.");
            Console.WriteLine("  The JIT hoisted the field read out of the loop — the thread");
            Console.WriteLine("  is stuck reading a stale cached value forever.");
            Console.WriteLine("  This is exactly the bug that 'volatile' prevents.");
        }

        Console.WriteLine();
        Console.WriteLine("  FIX: Declare the field as 'volatile bool _running' to ensure");
        Console.WriteLine("  every read goes through memory with acquire semantics, or use");
        Console.WriteLine("  Volatile.Read(ref _running) for a targeted volatile read.");
    }
}

// ============================================================================
// Usage Guide
// ============================================================================
//
// HOW TO RUN:
//   dotnet run -c Release
//
//   Running in Release mode is important for two reasons:
//   1. The JIT optimizes more aggressively, making the volatile demo more
//      likely to exhibit the field-hoisting bug.
//   2. The reordering experiment benefits from faster thread creation.
//
// EXPECTED RESULTS ON x86:
//   - Without barrier: 0 to a few dozen reorderings out of 1,000,000 trials.
//     Store-load reordering is the only reordering x86 permits, and it is
//     rare because the store buffer is small (typically 42-56 entries on
//     modern Intel cores) and drains within nanoseconds.
//   - With barrier: 0 reorderings. The MFENCE instruction drains the store
//     buffer before the load executes.
//   - Volatile demo: In Release mode, the spinner may hang (JIT hoists the
//     read). In Debug mode, it usually terminates normally.
//
// EXPECTED RESULTS ON ARM:
//   - Without barrier: Thousands to tens of thousands of reorderings. ARM's
//     weak memory model permits load-load, load-store, store-load, and
//     store-store reorderings.
//   - With barrier: 0 reorderings. The DMB instruction provides a full fence.
//   - Volatile demo: The spinner is very likely to hang without volatile,
//     due to both JIT hoisting and hardware reordering.
//
// PERFORMANCE NOTE:
//   This experiment spawns 2,000,000 threads (two per iteration). Thread
//   creation is expensive (~50-100 us per thread on Windows). The full run
//   may take several minutes. Reduce the iteration count if needed.
//
// FURTHER READING:
//   - "Memory Barriers: a Hardware View for Software Hackers" by Paul McKenney
//   - Intel 64 Architecture SDM, Volume 3A, Chapter 8 (Memory Ordering)
//   - ARM Architecture Reference Manual, Section B2 (Memory Model)
//   - .NET Memory Model: https://learn.microsoft.com/en-us/archive/msdn-magazine/2012/december/csharp-the-csharp-memory-model-in-theory-and-practice
// ============================================================================
