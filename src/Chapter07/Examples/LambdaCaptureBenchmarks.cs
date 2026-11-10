using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class LambdaCaptureBenchmarks
{
    private const int Threshold = 50_000;
    private int[] _data = null!;

    [Params(100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
        => _data = Enumerable.Range(0, Size).ToArray();

    [Benchmark]
    public long CapturingLambda()
    {
        int threshold = Threshold;

        return _data
            .Where(value => value > threshold)
            .Select(static value => (long)value)
            .Sum();
    }

    [Benchmark]
    public long StaticLambda()
        => _data
            .Where(static value => value > Threshold)
            .Select(static value => (long)value)
            .Sum();

    [Benchmark(Baseline = true)]
    public long HandwrittenLoop()
    {
        long sum = 0;

        foreach (int value in _data)
        {
            if (value > Threshold)
                sum += value;
        }

        return sum;
    }
}
