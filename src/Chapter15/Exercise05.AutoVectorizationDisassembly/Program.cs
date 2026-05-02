// ============================================================================
// Exercise 05 — Auto-Vectorization Disassembly Study
// ============================================================================
// Provides a simple arithmetic loop plus several anti-pattern variants. Run with
// --benchmark to emit disassembly and inspect how branches, dependencies, and
// virtual calls interfere with JIT vectorization opportunities.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<AutoVectorizationBenchmarks>();
    return;
}

int[] data = Enumerable.Range(0, 64).ToArray();
Console.WriteLine("=== Auto-Vectorization Disassembly Demo ===");
Console.WriteLine($"Simple loop: {VectorizationKernels.SimpleMultiplyAdd(data)}");
Console.WriteLine($"Branchy loop: {VectorizationKernels.BranchyMultiplyAdd(data)}");
Console.WriteLine($"Prefix dependency: {VectorizationKernels.PrefixStyle(data)}");
Console.WriteLine($"Virtual call loop: {VectorizationKernels.VirtualDispatch(data)}");
Console.WriteLine("Run with --benchmark to inspect generated assembly.");

// Core implementation used by the demo and benchmark code below.
public static class VectorizationKernels
{
    public static int SimpleMultiplyAdd(ReadOnlySpan<int> values)
    {
        int total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // This regular arithmetic loop is the shape most likely to auto-vectorize.
            total += (values[i] * 3) + 1;
        }

        return total;
    }

    public static int BranchyMultiplyAdd(ReadOnlySpan<int> values)
    {
        int total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int value = values[i];
            // The data-dependent branch makes SIMD packing much harder for the JIT.
            if ((value & 1) == 0)
            {
                total += (value * 3) + 1;
            }
            else
            {
                total -= value;
            }
        }

        return total;
    }

    public static int PrefixStyle(ReadOnlySpan<int> values)
    {
        int running = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // Each iteration depends on the previous result, limiting parallel reordering.
            running += values[i];
        }

        return running;
    }

    public static int VirtualDispatch(ReadOnlySpan<int> values)
    {
        ITransformer transformer = new MultiplyAddTransformer();
        int total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            // The virtual call hides the exact callee, which also works against vectorization.
            total += transformer.Transform(values[i]);
        }

        return total;
    }
}

public interface ITransformer
{
    int Transform(int value);
}

// Core implementation used by the demo and benchmark code below.
public sealed class MultiplyAddTransformer : ITransformer
{
    public int Transform(int value) => (value * 3) + 1;
}

[DisassemblyDiagnoser(maxDepth: 2, exportCombinedDisassemblyReport: true)]
[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class AutoVectorizationBenchmarks
{
    private int[] _values = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = Enumerable.Range(0, 4096).ToArray();
    }

    [Benchmark(Baseline = true)]
    public int SimpleLoop()
    {
        return VectorizationKernels.SimpleMultiplyAdd(_values);
    }

    [Benchmark]
    public int BranchyLoop()
    {
        return VectorizationKernels.BranchyMultiplyAdd(_values);
    }

    [Benchmark]
    public int PrefixDependencyLoop()
    {
        return VectorizationKernels.PrefixStyle(_values);
    }

    [Benchmark]
    public int VirtualDispatchLoop()
    {
        return VectorizationKernels.VirtualDispatch(_values);
    }
}
