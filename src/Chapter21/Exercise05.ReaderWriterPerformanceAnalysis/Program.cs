// ============================================================================
// Exercise 05 — Reader/Writer Performance Analysis
// ============================================================================
// Compares lock, ReaderWriterLockSlim, ConcurrentDictionary, and immutable-snapshot
// publication for a read-mostly configuration cache workload.
// ============================================================================

using System.Collections.Concurrent;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ReaderWriterBenchmarks>();
    return;
}

Console.WriteLine("Run with --benchmark to compare cache synchronization strategies.");

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ReaderWriterBenchmarks
{
    private LockCache _lockCache = null!;
    private ReaderWriterSlimCache _rwCache = null!;
    private ConcurrentDictionary<string, string> _concurrent = null!;
    private ImmutableSnapshotCache _immutable = null!;

    [Params(90, 99)]
    public int ReadPercentage;

    [GlobalSetup]
    public void Setup()
    {
        _lockCache = new LockCache();
        _rwCache = new ReaderWriterSlimCache();
        _concurrent = new ConcurrentDictionary<string, string>();
        _immutable = new ImmutableSnapshotCache();

        Seed(_lockCache.Set, _rwCache.Set, (k, v) => _concurrent[k] = v, _immutable.Set);
    }

    [Benchmark(Baseline = true)]
    public int LockCacheBenchmark() => RunMixed(
        read: key => _lockCache.Get(key),
        write: (key, value) => _lockCache.Set(key, value));

    [Benchmark]
    public int ReaderWriterLockBenchmark() => RunMixed(
        read: key => _rwCache.Get(key),
        write: (key, value) => _rwCache.Set(key, value));

    [Benchmark]
    public int ConcurrentDictionaryBenchmark() => RunMixed(
        read: key => _concurrent[key],
        write: (key, value) => _concurrent[key] = value);

    [Benchmark]
    public int ImmutableSnapshotBenchmark() => RunMixed(
        read: key => _immutable.Get(key),
        write: (key, value) => _immutable.Set(key, value));

    // Drive the shared workload used by the demo or benchmark entry point.
    private int RunMixed(Func<string, string> read, Action<string, string> write)
    {
        int checksum = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 4, worker =>
        {
            Random random = new(worker + 1234);
            int local = 0;
            for (int i = 0; i < 20_000; i++)
            {
                string key = "key" + (i % 64);
                if (random.Next(100) < ReadPercentage)
                {
                    local += read(key).Length;
                }
                else
                {
                    write(key, "value" + i);
                }
            }
            Interlocked.Add(ref checksum, local);
        });

        return checksum;
    }

    private static void Seed(Action<string, string> a, Action<string, string> b, Action<string, string> c, Action<string, string> d)
    {
        for (int i = 0; i < 64; i++)
        {
            string key = "key" + i;
            string value = "value" + i;
            a(key, value);
            b(key, value);
            c(key, value);
            d(key, value);
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class LockCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _map = new();

    // Serve reads from the current state without unnecessary extra work.
    public string Get(string key)
    {
        lock (_gate)
        {
            return _map[key];
        }
    }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(string key, string value)
    {
        lock (_gate)
        {
            _map[key] = value;
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ReaderWriterSlimCache
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, string> _map = new();

    // Serve reads from the current state without unnecessary extra work.
    public string Get(string key)
    {
        _lock.EnterReadLock();
        try { return _map[key]; }
        finally { _lock.ExitReadLock(); }
    }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(string key, string value)
    {
        _lock.EnterWriteLock();
        try { _map[key] = value; }
        finally { _lock.ExitWriteLock(); }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ImmutableSnapshotCache
{
    private ImmutableDictionary<string, string> _map = ImmutableDictionary<string, string>.Empty;

    // Serve reads from the current state without unnecessary extra work.
    public string Get(string key) => Volatile.Read(ref _map)[key];

    // Publish updates while keeping the shared state internally consistent.
    public void Set(string key, string value)
    {
        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            ImmutableDictionary<string, string> observed = Volatile.Read(ref _map);
            ImmutableDictionary<string, string> updated = observed.SetItem(key, value);
            // Publish the updated state only if no other thread changed it first.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _map, updated, observed), observed))
            {
                return;
            }
        }
    }
}
