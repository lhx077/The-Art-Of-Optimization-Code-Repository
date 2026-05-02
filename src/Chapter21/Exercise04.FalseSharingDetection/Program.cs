// ============================================================================
// Exercise 04 — False Sharing Detection and Repair
// ============================================================================
// Benchmarks adjacent counters against padded counters to show the impact of false
// sharing under parallel write-heavy increments.
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

// Core implementation used by the demo and benchmark code below.
public sealed class CompactAccumulator
{
    public long A;
    public long B;
    public long C;
    public long D;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedCounter
{
    [FieldOffset(0)]
    public long Value;
}

// Core implementation used by the demo and benchmark code below.
public sealed class PaddedAccumulator
{
    public PaddedCounter A;
    public PaddedCounter B;
    public PaddedCounter C;
    public PaddedCounter D;
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class FalseSharingBenchmarks
{
    [Params(500_000)]
    public int IterationsPerThread;

    [Benchmark(Baseline = true)]
    public long CompactFields()
    {
        CompactAccumulator counters = new();
        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () => Increment(ref counters.A),
            () => Increment(ref counters.B),
            () => Increment(ref counters.C),
            () => Increment(ref counters.D));
        return counters.A + counters.B + counters.C + counters.D;
    }

    [Benchmark]
    public long PaddedFields()
    {
        PaddedAccumulator counters = new();
        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () => Increment(ref counters.A.Value),
            () => Increment(ref counters.B.Value),
            () => Increment(ref counters.C.Value),
            () => Increment(ref counters.D.Value));
        return counters.A.Value + counters.B.Value + counters.C.Value + counters.D.Value;
    }

    private void Increment(ref long counter)
    {
        for (int i = 0; i < IterationsPerThread; i++)
        {
            counter++;
        }
    }
}
