// ============================================================================
// Exercise 03 — Counting Positive Values Without Branches
// ============================================================================
// Benchmarks three ways to count positive integers under different value
// distributions: explicit branching, branchless bit manipulation, and a ternary
// expression that the JIT may lower differently depending on the workload.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<PositiveCountBenchmarks>();
    return;
}

int[] sample = [5, -3, 0, 7, -11, 14, -1, 2];
Console.WriteLine("=== Positive Count Demo ===");
Console.WriteLine($"Branching:  {PositiveCounter.CountWithIf(sample)}");
Console.WriteLine($"Branchless: {PositiveCounter.CountBranchless(sample)}");
Console.WriteLine($"Ternary:    {PositiveCounter.CountWithTernary(sample)}");

// Core implementation used by the demo and benchmark code below.
public static class PositiveCounter
{
    public static int CountWithIf(ReadOnlySpan<int> values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // This branch is easy when the data is predictable and expensive when it is not.
            if (values[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    public static int CountBranchless(ReadOnlySpan<int> values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int value = values[i];
            // Bit tricks turn the sign test into arithmetic so the loop avoids a data-dependent branch.
            count += ((~(value >> 31)) & ((value | -value) >> 31)) & 1;
        }

        return count;
    }

    public static int CountWithTernary(ReadOnlySpan<int> values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // The source looks branchy, but the JIT may lower it to conditional moves instead.
            count += values[i] > 0 ? 1 : 0;
        }

        return count;
    }
}

public enum DataDistribution
{
    AllPositive,
    AllNegative,
    Random,
    Alternating
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class PositiveCountBenchmarks
{
    private int[] _values = null!;

    [Params(1024, 65536, 1048576)]
    public int Length;

    [Params(DataDistribution.AllPositive, DataDistribution.AllNegative, DataDistribution.Random, DataDistribution.Alternating)]
    public DataDistribution Distribution;

    [GlobalSetup]
    public void Setup()
    {
        _values = new int[Length];
        Random random = new(12345);

        for (int i = 0; i < Length; i++)
        {
            _values[i] = Distribution switch
            {
                DataDistribution.AllPositive => i + 1,
                DataDistribution.AllNegative => -(i + 1),
                DataDistribution.Random => random.Next(-5000, 5001),
                DataDistribution.Alternating => (i & 1) == 0 ? i + 1 : -(i + 1),
                _ => throw new InvalidOperationException("Unexpected distribution.")
            };
        }
    }

    [Benchmark(Baseline = true)]
    public int WithIf()
    {
        return PositiveCounter.CountWithIf(_values);
    }

    [Benchmark]
    public int Branchless()
    {
        return PositiveCounter.CountBranchless(_values);
    }

    [Benchmark]
    public int WithTernary()
    {
        return PositiveCounter.CountWithTernary(_values);
    }
}
