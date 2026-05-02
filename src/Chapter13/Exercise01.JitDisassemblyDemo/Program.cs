// ============================================================================
// Exercise 01 — Observe JIT-Generated Code
// ============================================================================
// Demonstrates a small arithmetic kernel and a virtual dispatch scenario. Run
// with --benchmark to emit BenchmarkDotNet disassembly output and inspect what
// the JIT generates for each benchmarked method.
// ============================================================================

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<JitDisassemblyBenchmarks>();
    return;
}

Console.WriteLine("=== JIT Disassembly Demo ===");
Console.WriteLine($"Folded arithmetic result: {ArithmeticKernel.Compute(123, 456)}");
Console.WriteLine($"Virtual dispatch result: {PolymorphismDemo.Run(100)}");
Console.WriteLine("Run with --benchmark to generate disassembly via BenchmarkDotNet.");

// Core implementation used by the demo and benchmark code below.
public static class ArithmeticKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compute(int x, int y)
    {
        // Keep the math compact so the generated assembly is easy to inspect by eye.
        int scaled = (x * 3) + (y * 5);
        return (scaled ^ 0x5A5A) - 17;
    }
}

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class PolymorphismDemo
{
    private static readonly IOperation _operation = new AddOperation();

    // Drive the shared workload used by the demo or benchmark entry point.
    public static int Run(int iterations)
    {
        int value = 0;
        for (int i = 0; i < iterations; i++)
        {
            // Repeated interface calls make it easier to observe dispatch behavior in the JIT output.
            value = _operation.Apply(value, i);
        }

        return value;
    }
}

public interface IOperation
{
    int Apply(int left, int right);
}

// Core implementation used by the demo and benchmark code below.
public sealed class AddOperation : IOperation
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Apply(int left, int right) => left + right;
}

[DisassemblyDiagnoser(maxDepth: 2, exportCombinedDisassemblyReport: true)]
[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class JitDisassemblyBenchmarks
{
    [Benchmark]
    public int ArithmeticKernelCall()
    {
        int total = 0;
        for (int i = 0; i < 256; i++)
        {
            total += ArithmeticKernel.Compute(i, i + 1);
        }

        return total;
    }

    [Benchmark]
    public int InterfaceDispatch()
    {
        return PolymorphismDemo.Run(256);
    }
}
