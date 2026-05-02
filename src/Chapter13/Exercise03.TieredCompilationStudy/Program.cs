// ============================================================================
// Exercise 03 — Explore Tiered Compilation Effects
// ============================================================================
// Executes the same hot method repeatedly and prints timing for successive
// rounds. Later rounds often become faster because tiered compilation can swap
// in a more optimized version after the method becomes hot.
// ============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<TieredCompilationBenchmarks>();
    return;
}

Console.WriteLine("=== Tiered Compilation Demo ===");
for (int round = 1; round <= 5; round++)
{
    // Later rounds may run faster once tiered compilation promotes the hot method.
    long elapsed = MeasureRound();
    Console.WriteLine($"Round {round}: {elapsed} ms");
}

static long MeasureRound()
{
    var stopwatch = Stopwatch.StartNew();
    long total = 0;

    for (int i = 0; i < 2_000_000; i++)
    {
        // Keep hammering the same method so the runtime has a reason to recompile it.
        total += TieredWorkload.HotMethod(i);
    }

    stopwatch.Stop();
    GC.KeepAlive(total);
    return stopwatch.ElapsedMilliseconds;
}

// Core implementation used by the demo and benchmark code below.
public static class TieredWorkload
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int HotMethod(int value)
    {
        int x = value;
        // A few cheap integer operations are enough to create a realistic hot kernel.
        x ^= x << 7;
        x += 31;
        x *= 9;
        x ^= x >> 11;
        return x & 1023;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class TieredCompilationBenchmarks
{
    [Benchmark]
    public long MeasureHotMethod()
    {
        long total = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            total += TieredWorkload.HotMethod(i);
        }

        return total;
    }
}
