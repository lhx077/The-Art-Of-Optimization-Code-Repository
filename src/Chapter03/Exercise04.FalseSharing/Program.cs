using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<FalseSharingBenchmarks>();

// =============================================================================
// Exercise 04: False Sharing — The Hidden Performance Killer
// =============================================================================
//
// What is false sharing?
// ----------------------
// False sharing occurs when multiple threads write to different variables that
// happen to reside on the SAME CPU cache line. Even though the threads are
// logically independent (no shared data), the hardware treats the entire cache
// line as a single unit of coherence.
//
// Why does it hurt performance?
// -----------------------------
// Modern x86 CPUs use the MESI protocol (Modified, Exclusive, Shared, Invalid)
// to keep caches consistent across cores. When one core writes to a cache line,
// ALL other cores holding a copy of that line must invalidate it. On the next
// access, the line must be transferred from the modifying core's cache.
//
// This means: if Thread A on Core 0 writes to counters[0] and Thread B on
// Core 1 writes to counters[1], but both values sit on the same 64-byte cache
// line, every single write forces a cross-core cache line transfer — even though
// the threads never touch each other's data. This "ping-pong" effect can slow
// down multi-threaded code by 2-10x or more.
//
// Cache line size:
// ----------------
// On modern x86/x64 CPUs (Intel and AMD), the cache line size is 64 bytes.
// A long (8 bytes) means 8 longs fit in one cache line. So a tightly packed
// long[] of 8 elements shares a single cache line.
//
// What this benchmark demonstrates:
// ----------------------------------
// - PackedCounters: All thread counters are adjacent in memory (false sharing).
// - PaddedCounters: Each counter is separated by 64 bytes of padding, placing
//   each on its own cache line (no false sharing).
//
// Expected results:
// -----------------
// PaddedCounters should be significantly faster than PackedCounters, especially
// as ThreadCount increases. The gap widens with more threads because more cores
// are contending for the same cache line.
//
// NOTE: This is an introductory demonstration of the problem. Chapter 21 covers
// production-grade solutions in depth, including [StructLayout], FieldOffset,
// and dedicated cache-line padding structures.
// =============================================================================

[MemoryDiagnoser]
public class FalseSharingBenchmarks
{
    [Params(2, 4, 8)]
    public int ThreadCount;

    private const int IterationsPerThread = 10_000_000;

    /// <summary>
    /// Tightly packed counters — adjacent longs share the same cache line.
    /// Each thread increments its own counter, but the MESI protocol forces
    /// cache line invalidations on every write because all counters sit
    /// within the same 64-byte region.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void PackedCounters()
    {
        // All counters are adjacent: counters[0], counters[1], ... counters[N-1]
        // With 8-byte longs, up to 8 counters fit in a single 64-byte cache line.
        var counters = new long[ThreadCount];

        var threads = new Thread[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int index = i; // capture for closure
            threads[i] = new Thread(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    counters[index]++;
                }
            });
        }

        for (int i = 0; i < ThreadCount; i++)
            threads[i].Start();

        for (int i = 0; i < ThreadCount; i++)
            threads[i].Join();
    }

    /// <summary>
    /// Padded counters — each counter is separated by 8 longs (64 bytes) of padding.
    /// This ensures each thread's counter lives on its own cache line, eliminating
    /// false sharing entirely. No cross-core invalidations occur.
    /// </summary>
    [Benchmark]
    public void PaddedCounters()
    {
        // Stride of 8 longs = 8 * 8 bytes = 64 bytes = one full cache line.
        // counters[0], counters[8], counters[16], ... are each on separate cache lines.
        const int cacheLineSize = 64;
        const int longsPerCacheLine = cacheLineSize / sizeof(long); // 8

        var counters = new long[ThreadCount * longsPerCacheLine];

        var threads = new Thread[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int index = i * longsPerCacheLine; // each counter is 64 bytes apart
            threads[i] = new Thread(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    counters[index]++;
                }
            });
        }

        for (int i = 0; i < ThreadCount; i++)
            threads[i].Start();

        for (int i = 0; i < ThreadCount; i++)
            threads[i].Join();
    }
}
