// ============================================================================
// Exercise 01 — Vectorized Array Sum
// ============================================================================
// Sums integer arrays with both scalar code and Vector<int>-based SIMD code.
// BenchmarkDotNet compares how the benefit changes from tiny inputs to very
// large inputs where memory bandwidth starts to dominate total throughput.
// ============================================================================

using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<VectorizedArraySumBenchmarks>();
    return;
}

int[] sample = Enumerable.Range(1, 32).ToArray();
Console.WriteLine("=== Vectorized Array Sum Demo ===");
Console.WriteLine($"Scalar sum: {ArraySummation.SumScalar(sample)}");
Console.WriteLine($"SIMD sum:   {ArraySummation.SumVector(sample)}");
// The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
Console.WriteLine($"Vector<int>.Count on this machine: {Vector<int>.Count}");

// Core implementation used by the demo and benchmark code below.
public static class ArraySummation
{
    public static long SumScalar(ReadOnlySpan<int> values)
    {
        long total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // This straightforward loop is the scalar baseline for the SIMD version below.
            total += values[i];
        }

        return total;
    }

    public static long SumVector(ReadOnlySpan<int> values)
    {
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        if (values.Length < Vector<int>.Count)
        {
            return SumScalar(values);
        }

        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<long> accumulatorLow = Vector<long>.Zero;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<long> accumulatorHigh = Vector<long>.Zero;
        int i = 0;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int lastVectorStart = values.Length - Vector<int>.Count;

        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        for (; i <= lastVectorStart; i += Vector<int>.Count)
        {
            // Load one SIMD-width chunk, then widen to long so partial sums cannot overflow int lanes.
            Vector<int> chunk = new(values.Slice(i, Vector<int>.Count));
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector.Widen(chunk, out Vector<long> low, out Vector<long> high);
            accumulatorLow += low;
            accumulatorHigh += high;
        }

        long total = Vector.Sum(accumulatorLow) + Vector.Sum(accumulatorHigh);
        for (; i < values.Length; i++)
        {
            // Finish the tail elements that did not fill a complete vector.
            total += values[i];
        }

        return total;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class VectorizedArraySumBenchmarks
{
    private int[] _values = null!;

    [Params(100, 1_000, 10_000, 100_000, 1_000_000)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        _values = new int[Length];
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = (i % 17) - 8;
        }
    }

    [Benchmark(Baseline = true)]
    public long Scalar()
    {
        return ArraySummation.SumScalar(_values);
    }

    [Benchmark]
    public long Vectorized()
    {
        return ArraySummation.SumVector(_values);
    }
}
