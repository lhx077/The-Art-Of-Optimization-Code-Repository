// ============================================================================
// Exercise 06 — Struct Optimization Analysis
// ============================================================================
// Part 1: Compares passing a large readonly struct by value versus by in
//         reference, demonstrating the copy savings from in.
// Part 2: Introduces a non-readonly struct with manually-implemented property
//         getters to expose the defensive copy trap. When a non-readonly struct
//         is passed as 'in', the compiler inserts a hidden copy before every
//         property access because it cannot prove the getter won't mutate this.
//         Readers will observe that misusing 'in' with mutable structs can be
//         SLOWER than plain by-value passing.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<StructOptimizationBenchmarks>();
    return;
}

var sample = new SensorReading(10, 20, 30, 40, 50, 60, 70, 80);
var mutable = new MutableSensorReading(10, 20, 30, 40, 50, 60, 70, 80);

Console.WriteLine("=== Part 1: readonly struct (SensorReading) ===");
Console.WriteLine($"By value:  {StructConsumers.ConsumeByValue(sample)}");
Console.WriteLine($"By in-ref: {StructConsumers.ConsumeByIn(sample)}");

Console.WriteLine();
Console.WriteLine("=== Part 2: Mutable struct — defensive copy trap ===");
Console.WriteLine($"By value:  {StructConsumers.ConsumeMutableByValue(mutable)}");
Console.WriteLine($"By in-ref: {StructConsumers.ConsumeMutableByIn(mutable)}");

// ---- Part 1: readonly struct — safe and efficient with in ----
public readonly struct SensorReading
{
    public SensorReading(long a, long b, long c, long d, long e, long f, long g, long h)
    {
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
        G = g;
        H = h;
    }

    public long A { get; }
    public long B { get; }
    public long C { get; }
    public long D { get; }
    public long E { get; }
    public long F { get; }
    public long G { get; }
    public long H { get; }
}

// ---- Part 2: non-readonly struct with manual getters ----
// Manually-implemented property getters are NOT implicitly readonly in C# 8+
// (unlike auto-properties). When this struct is passed via 'in', the compiler
// inserts a defensive copy of the entire 64-byte struct before each getter call.
public struct MutableSensorReading
{
    private long _a, _b, _c, _d, _e, _f, _g, _h;

    public MutableSensorReading(long a, long b, long c, long d, long e, long f, long g, long h)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
        _e = e;
        _f = f;
        _g = g;
        _h = h;
    }

    public long A { get { return _a; } }
    public long B { get { return _b; } }
    public long C { get { return _c; } }
    public long D { get { return _d; } }
    public long E { get { return _e; } }
    public long F { get { return _f; } }
    public long G { get { return _g; } }
    public long H { get { return _h; } }
}

public static class StructConsumers
{
    public static long ConsumeByValue(SensorReading reading)
    {
        return reading.A + reading.B + reading.C + reading.D
             + reading.E + reading.F + reading.G + reading.H;
    }

    public static long ConsumeByIn(in SensorReading reading)
    {
        return reading.A + reading.B + reading.C + reading.D
             + reading.E + reading.F + reading.G + reading.H;
    }

    public static long ConsumeMutableByValue(MutableSensorReading reading)
    {
        return reading.A + reading.B + reading.C + reading.D
             + reading.E + reading.F + reading.G + reading.H;
    }

    public static long ConsumeMutableByIn(in MutableSensorReading reading)
    {
        return reading.A + reading.B + reading.C + reading.D
             + reading.E + reading.F + reading.G + reading.H;
    }
}

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class StructOptimizationBenchmarks
{
    private SensorReading _reading;
    private MutableSensorReading _mutableReading;

    [GlobalSetup]
    public void Setup()
    {
        _reading = new SensorReading(10, 20, 30, 40, 50, 60, 70, 80);
        _mutableReading = new MutableSensorReading(10, 20, 30, 40, 50, 60, 70, 80);
    }

    // ---- Part 1: readonly struct ----

    [Benchmark(Baseline = true)]
    public long ReadonlyStruct_ByValue()
    {
        long total = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            total += StructConsumers.ConsumeByValue(_reading);
        }

        return total;
    }

    [Benchmark]
    public long ReadonlyStruct_ByIn()
    {
        long total = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            total += StructConsumers.ConsumeByIn(_reading);
        }

        return total;
    }

    // ---- Part 2: mutable struct — defensive copy ----

    [Benchmark]
    public long MutableStruct_ByValue()
    {
        long total = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            total += StructConsumers.ConsumeMutableByValue(_mutableReading);
        }

        return total;
    }

    [Benchmark]
    public long MutableStruct_ByIn()
    {
        long total = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            total += StructConsumers.ConsumeMutableByIn(_mutableReading);
        }

        return total;
    }
}
