// ============================================================================
// Exercise 03 — Numerical Integrator with Function Pointers
// ============================================================================
// Implements the trapezoidal rule twice: once with a function pointer and once
// with a traditional delegate. BenchmarkDotNet compares dispatch overhead under
// different segment counts.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<IntegratorBenchmarks>();
    return;
}

unsafe
{
    Console.WriteLine("=== Function Pointer Integrator Demo ===");
    Console.WriteLine($"Function pointer result: {NumericalIntegrator.Integrate(&Functions.Quadratic, 0, 10, 1000):F6}");
}
Console.WriteLine($"Delegate result: {NumericalIntegrator.Integrate(Functions.QuadraticDelegate, 0, 10, 1000):F6}");

public static unsafe class NumericalIntegrator
{
    public static double Integrate(delegate*<double, double> function, double start, double end, int segments)
    {
        double width = (end - start) / segments;
        // Trapezoidal rule counts the two endpoints with half weight.
        double total = (function(start) + function(end)) * 0.5;

        for (int i = 1; i < segments; i++)
        {
            total += function(start + i * width);
        }

        return total * width;
    }

    public static double Integrate(Func<double, double> function, double start, double end, int segments)
    {
        double width = (end - start) / segments;
        // This overload keeps the same math so the benchmark isolates call mechanics.
        double total = (function(start) + function(end)) * 0.5;

        for (int i = 1; i < segments; i++)
        {
            total += function(start + i * width);
        }

        return total * width;
    }
}

// Core implementation used by the demo and benchmark code below.
public static class Functions
{
    public static double Quadratic(double x) => (x * x) + (2 * x) + 1;

    public static Func<double, double> QuadraticDelegate { get; } = Quadratic;
}

[MemoryDiagnoser]
public unsafe class IntegratorBenchmarks
{
    [Params(128, 2048, 32768)]
    public int Segments;

    [Benchmark(Baseline = true)]
    public double DelegateIntegrator()
    {
        return NumericalIntegrator.Integrate(Functions.QuadraticDelegate, 0, 10, Segments);
    }

    [Benchmark]
    public double FunctionPointerIntegrator()
    {
        return NumericalIntegrator.Integrate(&Functions.Quadratic, 0, 10, Segments);
    }
}
