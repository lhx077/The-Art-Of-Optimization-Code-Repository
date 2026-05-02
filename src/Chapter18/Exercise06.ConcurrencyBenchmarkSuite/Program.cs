// ============================================================================
// Exercise 06 — Concurrency Benchmark Suite
// ============================================================================
// Provides a small benchmark suite for comparing Monitor, SpinLock,
// ReaderWriterLockSlim, Channel, and BlockingCollection under different patterns.
// ============================================================================

using System.Collections.Concurrent;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    return;
}

Console.WriteLine("Run with --benchmark to execute the concurrency benchmark suite.");

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class LockLatencyBenchmarks
{
    private readonly object _monitor = new();
    private SpinLock _spinLock;
    private int _value;

    [Params(false, true)]
    public bool Contended;

    [Benchmark(Baseline = true)]
    public int MonitorLock()
    {
        if (!Contended)
        {
            lock (_monitor)
            {
                return ++_value;
            }
        }

        int total = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 4, _ =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                lock (_monitor)
                {
                    total += ++_value;
                }
            }
        });

        return total;
    }

    [Benchmark]
    public int SpinLockLock()
    {
        if (!Contended)
        {
            bool taken = false;
            try
            {
                _spinLock.Enter(ref taken);
                return ++_value;
            }
            finally
            {
                if (taken) _spinLock.Exit();
            }
        }

        int total = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 4, _ =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                bool taken = false;
                try
                {
                    _spinLock.Enter(ref taken);
                    total += ++_value;
                }
                finally
                {
                    if (taken) _spinLock.Exit();
                }
            }
        });

        return total;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ReaderWriterBenchmarks
{
    private readonly object _monitor = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private int _value;

    [Params(90, 99)]
    public int ReadPercentage;

    [Benchmark(Baseline = true)]
    public int MonitorMixed()
    {
        return RunMixed(useReaderWriter: false);
    }

    [Benchmark]
    public int ReaderWriterMixed()
    {
        return RunMixed(useReaderWriter: true);
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    private int RunMixed(bool useReaderWriter)
    {
        int total = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 4, worker =>
        {
            Random random = new(worker + 1234);
            int local = 0;
            for (int i = 0; i < 20_000; i++)
            {
                if (random.Next(100) < ReadPercentage)
                {
                    if (useReaderWriter)
                    {
                        _rwLock.EnterReadLock();
                        try { local += _value; }
                        finally { _rwLock.ExitReadLock(); }
                    }
                    else
                    {
                        lock (_monitor)
                        {
                            local += _value;
                        }
                    }
                }
                else
                {
                    if (useReaderWriter)
                    {
                        _rwLock.EnterWriteLock();
                        try { _value++; }
                        finally { _rwLock.ExitWriteLock(); }
                    }
                    else
                    {
                        lock (_monitor)
                        {
                            _value++;
                        }
                    }
                }
            }

            Interlocked.Add(ref total, local);
        });

        return total;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ProducerConsumerBenchmarks
{
    [Params(20_000)]
    public int ItemCount;

    [Benchmark(Baseline = true)]
    public int BlockingCollectionPipeline()
    {
        using BlockingCollection<int> collection = new(boundedCapacity: 1024);
        int sum = 0;

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < ItemCount; i++)
            {
                collection.Add(i);
            }
            collection.CompleteAdding();
        });

        Task consumer = Task.Run(() =>
        {
            foreach (int item in collection.GetConsumingEnumerable())
            {
                sum += item;
            }
        });

        Task.WaitAll(producer, consumer);
        return sum;
    }

    [Benchmark]
    public async Task<int> ChannelPipeline()
    {
        // A bounded channel lets downstream slowdown push back on producers.
        Channel<int> channel = Channel.CreateBounded<int>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        int sum = 0;
        Task producer = Task.Run(async () =>
        {
            for (int i = 0; i < ItemCount; i++)
            {
                await channel.Writer.WriteAsync(i);
            }
            channel.Writer.TryComplete();
        });

        Task consumer = Task.Run(async () =>
        {
            await foreach (int item in channel.Reader.ReadAllAsync())
            {
                sum += item;
            }
        });

        await Task.WhenAll(producer, consumer);
        return sum;
    }
}
