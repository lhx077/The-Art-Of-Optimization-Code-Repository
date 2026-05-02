// ============================================================================
// Exercise 04 — Integer Square Root
// ============================================================================
// Implements an integer square-root routine using a bit-by-bit method that avoids
// floating-point work. Benchmarks compare it with Math.Sqrt plus flooring.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<IntegerSquareRootBenchmarks>();
    return;
}

int[] samples = [0, 1, 2, 3, 4, 15, 16, 17, 255, 65535, int.MaxValue];
Console.WriteLine("=== Integer Square Root Demo ===");
foreach (int value in samples)
{
    Console.WriteLine($"{value,10} -> {IntegerSquareRoot.FloorSqrt(value)}");
}

// Core implementation used by the demo and benchmark code below.
public static class IntegerSquareRoot
{
    public static int FloorSqrt(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        uint remainder = (uint)value;
        uint result = 0;
        // Start from the highest power of four that can fit in a 32-bit integer.
        uint bit = 1u << 30;

        while (bit > remainder)
        {
            // Drop down until the trial bit is no larger than the remaining input.
            bit >>= 2;
        }

        while (bit != 0)
        {
            if (remainder >= result + bit)
            {
                // Accept this bit into the root and subtract the corresponding contribution.
                remainder -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                // Reject this bit and shift the partial result into the next trial position.
                result >>= 1;
            }

            bit >>= 2;
        }

        return (int)result;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class IntegerSquareRootBenchmarks
{
    private int[] _values = null!;

    [Params(1024, 65536, 1048576)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        _values = new int[Length];
        for (int i = 0; i < Length; i++)
        {
            _values[i] = (int)((long)i * i % int.MaxValue);
        }
    }

    [Benchmark(Baseline = true)]
    public int CustomIntegerSqrt()
    {
        int total = 0;
        for (int i = 0; i < _values.Length; i++)
        {
            total += IntegerSquareRoot.FloorSqrt(_values[i]);
        }

        return total;
    }

    [Benchmark]
    public int MathSqrtFloor()
    {
        int total = 0;
        for (int i = 0; i < _values.Length; i++)
        {
            total += (int)Math.Sqrt(_values[i]);
        }

        return total;
    }
}
