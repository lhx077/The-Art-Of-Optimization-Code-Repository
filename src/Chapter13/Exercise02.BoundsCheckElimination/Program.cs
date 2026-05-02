// ============================================================================
// Exercise 02 — Verify Bounds-Check Elimination
// ============================================================================
// Compares multiple array traversal patterns to demonstrate how loop structure
// affects the JIT's ability to eliminate redundant bounds checks:
//   1. Canonical for loop      — classic pattern, easiest to optimize
//   2. Offset for loop         — derived index adds proof obligations
//   3. foreach loop            — natural bounds-check elimination on arrays
//   4. Span<T> loop            — modern .NET efficient traversal
//   5. Manual bounds check     — redundant guard to test JIT deduplication
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<BoundsCheckBenchmarks>();
    return;
}

int[] sample = Enumerable.Range(1, 16).ToArray();
Console.WriteLine("=== Bounds Check Demo ===");
Console.WriteLine($"Canonical sum:      {ArraySummation.SumCanonical(sample)}");
Console.WriteLine($"Offset sum:         {ArraySummation.SumWithOffset(sample)}");
Console.WriteLine($"Foreach sum:        {ArraySummation.SumForeach(sample)}");
Console.WriteLine($"Span sum:           {ArraySummation.SumSpan(sample)}");
Console.WriteLine($"Manual check sum:   {ArraySummation.SumManualBoundsCheck(sample)}");

public static class ArraySummation
{
    public static int SumCanonical(int[] values)
    {
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    public static int SumWithOffset(int[] values)
    {
        int sum = 0;
        for (int i = 0; i < values.Length - 1; i++)
        {
            int next = i + 1;
            sum += values[i] + values[next];
        }

        return sum;
    }

    public static int SumForeach(int[] values)
    {
        int sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }

        return sum;
    }

    public static int SumSpan(ReadOnlySpan<int> values)
    {
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    public static int SumManualBoundsCheck(int[] values)
    {
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (i >= values.Length)
                throw new IndexOutOfRangeException();

            sum += values[i];
        }

        return sum;
    }
}

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class BoundsCheckBenchmarks
{
    private int[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(1, 32_768).ToArray();
    }

    [Benchmark(Baseline = true)]
    public int CanonicalLoop()
    {
        return ArraySummation.SumCanonical(_data);
    }

    [Benchmark]
    public int OffsetLoop()
    {
        return ArraySummation.SumWithOffset(_data);
    }

    [Benchmark]
    public int ForeachLoop()
    {
        return ArraySummation.SumForeach(_data);
    }

    [Benchmark]
    public int SpanLoop()
    {
        return ArraySummation.SumSpan(_data);
    }

    [Benchmark]
    public int ManualBoundsCheck()
    {
        return ArraySummation.SumManualBoundsCheck(_data);
    }
}
