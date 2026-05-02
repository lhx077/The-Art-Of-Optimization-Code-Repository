// ============================================================================
// Exercise 02 — False Sharing and Cache-Line Isolation
// ============================================================================
// Benchmarks a compact counter layout against a cache-line-padded layout under
// multi-threaded write-heavy contention.
// ============================================================================

using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<FalseSharingBenchmarks>();
    return;
}

Console.WriteLine("Run with --benchmark to compare compact and padded counters.");

[StructLayout(LayoutKind.Sequential)]
public struct CompactCounters
{
    public long A;
    public long B;
    public long C;
    public long D;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedLong
{
    [FieldOffset(0)]
    public long Value;
}

// Core implementation used by the demo and benchmark code below.
public sealed class PaddedCounters
{
    public PaddedLong A;
    public PaddedLong B;
    public PaddedLong C;
    public PaddedLong D;
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class FalseSharingBenchmarks
{
    [Params(200_000)]
    public int IterationsPerThread;

    [Benchmark(Baseline = true)]
    public long CompactLayout()
    {
        CompactCounters counters = default;

        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () => Increment(ref counters.A, IterationsPerThread),
            () => Increment(ref counters.B, IterationsPerThread),
            () => Increment(ref counters.C, IterationsPerThread),
            () => Increment(ref counters.D, IterationsPerThread));

        return counters.A + counters.B + counters.C + counters.D;
    }

    [Benchmark]
    public long PaddedLayout()
    {
        PaddedCounters counters = new();

        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () => Increment(ref counters.A.Value, IterationsPerThread),
            () => Increment(ref counters.B.Value, IterationsPerThread),
            () => Increment(ref counters.C.Value, IterationsPerThread),
            () => Increment(ref counters.D.Value, IterationsPerThread));

        return counters.A.Value + counters.B.Value + counters.C.Value + counters.D.Value;
    }

    private static void Increment(ref long counter, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            counter++;
        }
    }
}
