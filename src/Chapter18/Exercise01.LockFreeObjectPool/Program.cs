// ============================================================================
// Exercise 01 — Lock-Free-Style Object Pool with Thread-Local Fast Path
// ============================================================================
// Uses a [ThreadStatic] single-item cache plus a shared concurrent stack to reduce
// cross-thread synchronization. Benchmarks compare it with DefaultObjectPool.
// ============================================================================

using System.Collections.Concurrent;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.ObjectPool;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ObjectPoolBenchmarks>();
    return;
}

var pool = new ThreadLocalObjectPool<PooledBuffer>(() => new PooledBuffer(256), buffer => buffer.Reset(), maxSharedRetained: 1024);
PooledBuffer rented = pool.Rent();
rented.Append("hello");
Console.WriteLine($"Rented payload length: {rented.Length}");
pool.Return(rented);

// Core implementation used by the demo and benchmark code below.
public sealed class ThreadLocalObjectPool<T> where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly ConcurrentStack<T> _shared = new();
    private readonly int _maxSharedRetained;
    private int _sharedCount;

    private readonly ThreadLocal<T?> _local = new();

    public ThreadLocalObjectPool(Func<T> factory, Action<T>? reset = null, int maxSharedRetained = 256)
    {
        _factory = factory;
        _reset = reset;
        _maxSharedRetained = maxSharedRetained;
    }

    // Prefer a reusable instance before falling back to allocation.
    public T Rent()
    {
        T? local = _local.Value;
        if (local is not null)
        {
            _local.Value = null;
            return local;
        }

        if (_shared.TryPop(out T? item))
        {
            Interlocked.Decrement(ref _sharedCount);
            return item;
        }

        return _factory();
    }

    // Place the instance back into the pool after the caller is done with it.
    public void Return(T item)
    {
        _reset?.Invoke(item);

        if (_local.Value is null)
        {
            _local.Value = item;
            return;
        }

        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            int observed = Volatile.Read(ref _sharedCount);
            if (observed >= _maxSharedRetained)
            {
                return;
            }

            // Publish the updated state only if no other thread changed it first.
            if (Interlocked.CompareExchange(ref _sharedCount, observed + 1, observed) == observed)
            {
                _shared.Push(item);
                return;
            }
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class PooledBuffer
{
    private readonly char[] _buffer;
    private int _length;

    public PooledBuffer(int size)
    {
        _buffer = new char[size];
    }

    public int Length => _length;

    public void Append(string value)
    {
        int copyLength = Math.Min(value.Length, _buffer.Length - _length);
        value.AsSpan(0, copyLength).CopyTo(_buffer.AsSpan(_length));
        _length += copyLength;
    }

    public void Reset() => _length = 0;
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ObjectPoolBenchmarks
{
    private ThreadLocalObjectPool<PooledBuffer> _threadLocalPool = null!;
    private ObjectPool<PooledBuffer> _defaultPool = null!;

    [Params(50_000)]
    public int OperationsPerWorker;

    [Params(1, 4, 8)]
    public int WorkerCount;

    [GlobalSetup]
    public void Setup()
    {
        _threadLocalPool = new ThreadLocalObjectPool<PooledBuffer>(() => new PooledBuffer(256), buffer => buffer.Reset(), maxSharedRetained: 1024);
        DefaultObjectPoolProvider provider = new();
        _defaultPool = provider.Create(new PooledBufferPolicy());
    }

    [Benchmark(Baseline = true)]
    public int ThreadLocalPool()
    {
        return RunWorkers(poolType: 0);
    }

    [Benchmark]
    public int MicrosoftPool()
    {
        return RunWorkers(poolType: 1);
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    private int RunWorkers(int poolType)
    {
        int total = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, _ =>
        {
            int local = 0;
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                PooledBuffer buffer = poolType == 0 ? _threadLocalPool.Rent() : _defaultPool.Get();
                buffer.Append("benchmark");
                local += buffer.Length;
                if (poolType == 0)
                {
                    _threadLocalPool.Return(buffer);
                }
                else
                {
                    _defaultPool.Return(buffer);
                }
            }

            Interlocked.Add(ref total, local);
        });

        return total;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class PooledBufferPolicy : PooledObjectPolicy<PooledBuffer>
{
    public override PooledBuffer Create() => new(256);

    // Place the instance back into the pool after the caller is done with it.
    public override bool Return(PooledBuffer obj)
    {
        obj.Reset();
        return true;
    }
}
