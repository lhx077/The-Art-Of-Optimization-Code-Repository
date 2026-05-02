// ============================================================================
// Exercise 03 — Parallel Sum Benchmarks
// ============================================================================
// Compares lock-based, Interlocked-based, thread-local, PLINQ, and Dataflow-style
// sum implementations across the same input array.
// ============================================================================

using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ParallelSumBenchmarks>();
    return;
}

Console.WriteLine("Run with --benchmark to compare sum strategies.");

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ParallelSumBenchmarks
{
    private int[] _values = null!;

    [Params(100_000, 1_000_000)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        _values = Enumerable.Range(1, Length).ToArray();
    }

    [Benchmark(Baseline = true)]
    public long ParallelWithLock()
    {
        object gate = new();
        long sum = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, _values.Length, i =>
        {
            lock (gate)
            {
                sum += _values[i];
            }
        });
        return sum;
    }

    [Benchmark]
    public long ParallelWithInterlocked()
    {
        long sum = 0;
        Parallel.ForEach(Partitioner.Create(0, _values.Length, 1024), range =>
        {
            long local = 0;
            for (int i = range.Item1; i < range.Item2; i++)
            {
                local += _values[i];
            }
            Interlocked.Add(ref sum, local);
        });
        return sum;
    }

    [Benchmark]
    public long ParallelWithThreadLocal()
    {
        long sum = 0;
        Parallel.For<long>(0, _values.Length, () => 0, (i, _, local) => local + _values[i], local => Interlocked.Add(ref sum, local));
        return sum;
    }

    [Benchmark]
    public long PlinqSum()
    {
        return _values.AsParallel().Sum(x => (long)x);
    }

    [Benchmark]
    public async Task<long> DataflowBatchSum()
    {
        BatchBlock<int> batch = new(4096);
        long total = 0;
        // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
        ActionBlock<int[]> action = new(items =>
        {
            long local = 0;
            for (int i = 0; i < items.Length; i++)
            {
                local += items[i];
            }
            Interlocked.Add(ref total, local);
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Environment.ProcessorCount });

        batch.LinkTo(action, new DataflowLinkOptions { PropagateCompletion = true });
        for (int i = 0; i < _values.Length; i++)
        {
            await batch.SendAsync(_values[i]);
        }

        batch.Complete();
        await action.Completion;
        return total;
    }
}
