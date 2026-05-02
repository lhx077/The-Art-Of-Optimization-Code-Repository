// ============================================================================
// Exercise 04 — Test Inlining Impact
// ============================================================================
// Part 1: Compares a small helper forced inline vs forced no-inline to isolate
//         the direct impact of inlining on performance.
// Part 2: Constructs a series of methods with increasing IL body sizes (without
//         any [MethodImpl] attributes) to detect the approximate threshold where
//         RyuJIT stops auto-inlining. Use the [DisassemblyDiagnoser] output to
//         observe which methods are inlined and which generate call instructions.
// ============================================================================

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<InliningBenchmarks>();
    return;
}

Console.WriteLine("=== Part 1: Forced Inlining Comparison ===");
Console.WriteLine($"Aggressive inline result: {InliningSamples.SumInline(1000)}");
Console.WriteLine($"No inline result:         {InliningSamples.SumNoInline(1000)}");

Console.WriteLine();
Console.WriteLine("=== Part 2: IL Size Threshold Detection ===");
Console.WriteLine("Run with --benchmark and inspect [DisassemblyDiagnoser] output");
Console.WriteLine("to see which Step methods the JIT auto-inlines.");
Console.WriteLine($"StepTiny:           {InliningSamples.StepTiny(42)}");
Console.WriteLine($"StepSmall:          {InliningSamples.StepSmall(42)}");
Console.WriteLine($"StepMedium:         {InliningSamples.StepMedium(42)}");
Console.WriteLine($"StepNearThreshold:  {InliningSamples.StepNearThreshold(42)}");
Console.WriteLine($"StepOverThreshold:  {InliningSamples.StepOverThreshold(42)}");
Console.WriteLine($"StepHuge:           {InliningSamples.StepHuge(42)}");

public static class InliningSamples
{
    // ---- Part 1: Forced comparison ----

    public static int SumInline(int count)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += InlineStep(i);
        }

        return total;
    }

    public static int SumNoInline(int count)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += NoInlineStep(i);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InlineStep(int value) => ((value * 3) + 5) ^ 17;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int NoInlineStep(int value) => ((value * 3) + 5) ^ 17;

    // ---- Part 2: IL size threshold detection ----
    // No [MethodImpl] attributes — let RyuJIT decide based on IL body size.
    // Examine the [DisassemblyDiagnoser] output to see which methods are
    // inlined (code merged into caller) vs called (a call instruction remains).

    // ~4 IL bytes
    public static int StepTiny(int v) => v + 1;

    // ~10 IL bytes
    public static int StepSmall(int v) => ((v * 3) + 7) ^ 31;

    // ~30 IL bytes
    public static int StepMedium(int v)
    {
        int a = v * 3 + 7;
        int b = a ^ 31;
        int c = b + v * 2;
        int d = c - a + 13;
        return d ^ b;
    }

    // ~55 IL bytes
    public static int StepNearThreshold(int v)
    {
        int a = v * 3 + 7;
        int b = a ^ 31;
        int c = b + v * 2;
        int d = c - a + 13;
        int e = d ^ (b + c);
        int f = e * 5 + d;
        int g = f ^ (a - e);
        int h = g + f - c;
        return h ^ (d + a);
    }

    // ~80 IL bytes
    public static int StepOverThreshold(int v)
    {
        int a = v * 3 + 7;
        int b = a ^ 31;
        int c = b + v * 2;
        int d = c - a + 13;
        int e = d ^ (b + c);
        int f = e * 5 + d;
        int g = f ^ (a - e);
        int h = g + f - c;
        int i = h * 7 + g;
        int j = i ^ (h + f);
        int k = j - i + e;
        int l = k ^ (j * 3);
        return l + k - j;
    }

    // ~120 IL bytes
    public static int StepHuge(int v)
    {
        int a = v * 3 + 7;
        int b = a ^ 31;
        int c = b + v * 2;
        int d = c - a + 13;
        int e = d ^ (b + c);
        int f = e * 5 + d;
        int g = f ^ (a - e);
        int h = g + f - c;
        int i = h * 7 + g;
        int j = i ^ (h + f);
        int k = j - i + e;
        int l = k ^ (j * 3);
        int m = l + k - j;
        int n = m * 11 + l;
        int o = n ^ (m + k);
        int p = o - n + i;
        int q = p ^ (o * 7);
        return q + p - o;
    }
}

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class InliningBenchmarks
{
    private const int Iterations = 100_000;

    // ---- Part 1: Forced comparison ----

    [Benchmark(Baseline = true)]
    public int AggressiveInline()
    {
        return InliningSamples.SumInline(Iterations);
    }

    [Benchmark]
    public int ForcedNoInline()
    {
        return InliningSamples.SumNoInline(Iterations);
    }

    // ---- Part 2: IL size threshold detection ----

    [Benchmark]
    public int CallTiny()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepTiny(i);
        return total;
    }

    [Benchmark]
    public int CallSmall()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepSmall(i);
        return total;
    }

    [Benchmark]
    public int CallMedium()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepMedium(i);
        return total;
    }

    [Benchmark]
    public int CallNearThreshold()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepNearThreshold(i);
        return total;
    }

    [Benchmark]
    public int CallOverThreshold()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepOverThreshold(i);
        return total;
    }

    [Benchmark]
    public int CallHuge()
    {
        int total = 0;
        for (int i = 0; i < Iterations; i++)
            total += InliningSamples.StepHuge(i);
        return total;
    }
}
