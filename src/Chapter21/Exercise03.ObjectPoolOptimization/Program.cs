// ============================================================================
// Exercise 03 — Object Pool Optimization
// ============================================================================
// Compares three object pool styles: shared ConcurrentBag, pure thread-local stack,
// and a hybrid design that combines local fast paths with a shared fallback.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ObjectPoolBenchmarks>();
    return;
}

Console.WriteLine("Run with --benchmark to compare pool designs.");

// Core implementation used by the demo and benchmark code below.
public sealed class PooledBuffer
{
    public byte[] Buffer { get; } = new byte[256];
}

public interface ISimplePool<T>
{
    T Rent();
    void Return(T item);
}

// Core implementation used by the demo and benchmark code below.
public sealed class ConcurrentBagPool<T> : ISimplePool<T> where T : class, new()
{
    private readonly ConcurrentBag<T> _bag = new();
    // Prefer a reusable instance before falling back to allocation.
    public T Rent() => _bag.TryTake(out T? item) ? item : new T();
    // Place the instance back into the pool after the caller is done with it.
    public void Return(T item) => _bag.Add(item);
}

// Core implementation used by the demo and benchmark code below.
public sealed class ThreadLocalPool<T> : ISimplePool<T> where T : class, new()
{
    private readonly ThreadLocal<Stack<T>> _local = new(() => new Stack<T>());

    // Prefer a reusable instance before falling back to allocation.
    public T Rent()
    {
        Stack<T> stack = _local.Value!;
        return stack.Count > 0 ? stack.Pop() : new T();
    }

    // Place the instance back into the pool after the caller is done with it.
    public void Return(T item)
    {
        _local.Value!.Push(item);
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class HybridPool<T> : ISimplePool<T> where T : class, new()
{
    private readonly ConcurrentBag<T> _shared = new();
    private readonly ThreadLocal<T?> _local = new();

    // Prefer a reusable instance before falling back to allocation.
    public T Rent()
    {
        T? local = _local.Value;
        if (local is not null)
        {
            _local.Value = null;
            return local;
        }

        return _shared.TryTake(out T? item) ? item : new T();
    }

    // Place the instance back into the pool after the caller is done with it.
    public void Return(T item)
    {
        if (_local.Value is null)
        {
            _local.Value = item;
        }
        else
        {
            _shared.Add(item);
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ObjectPoolBenchmarks
{
    [Params(1, 8)]
    public int WorkerCount;

    [Params(50_000)]
    public int OperationsPerWorker;

    [Benchmark(Baseline = true)]
    public int ConcurrentBagPoolBenchmark() => Run(new ConcurrentBagPool<PooledBuffer>());

    [Benchmark]
    public int ThreadLocalPoolBenchmark() => Run(new ThreadLocalPool<PooledBuffer>());

    [Benchmark]
    public int HybridPoolBenchmark() => Run(new HybridPool<PooledBuffer>());

    // Drive the shared workload used by the demo or benchmark entry point.
    private int Run(ISimplePool<PooledBuffer> pool)
    {
        int checksum = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, _ =>
        {
            int local = 0;
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                PooledBuffer buffer = pool.Rent();
                buffer.Buffer[0] = (byte)i;
                local += buffer.Buffer[0];
                pool.Return(buffer);
            }
            Interlocked.Add(ref checksum, local);
        });

        return checksum;
    }
}
