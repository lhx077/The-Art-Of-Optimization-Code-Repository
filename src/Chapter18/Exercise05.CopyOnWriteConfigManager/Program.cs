// ============================================================================
// Exercise 05 — Copy-On-Write Configuration Manager
// ============================================================================
// Publishes immutable configuration snapshots through Interlocked.Exchange and
// compares read-heavy workloads with a ReaderWriterLockSlim-based manager.
// ============================================================================

using System.Collections.Immutable;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ConfigManagerBenchmarks>();
    return;
}

CopyOnWriteConfigManager manager = new(AppConfig.Empty);
manager.Update(config => config.Set("region", "apac").Set("mode", "release"));
Console.WriteLine(manager.Read().Values["region"]);

public sealed record AppConfig(ImmutableDictionary<string, string> Values)
{
    public static AppConfig Empty { get; } = new(ImmutableDictionary<string, string>.Empty);

    // Publish updates while keeping the shared state internally consistent.
    public AppConfig Set(string key, string value) => this with { Values = Values.SetItem(key, value) };
}

// Core implementation used by the demo and benchmark code below.
public sealed class CopyOnWriteConfigManager
{
    private AppConfig _current;

    public CopyOnWriteConfigManager(AppConfig initial)
    {
        _current = initial;
    }

    // Read the shared state with visibility guarantees before making decisions.
    public AppConfig Read() => Volatile.Read(ref _current);

    public void Update(Func<AppConfig, AppConfig> updater)
    {
        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            AppConfig observed = Volatile.Read(ref _current);
            AppConfig updated = updater(observed);
            // Publish the updated state only if no other thread changed it first.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, updated, observed), observed))
            {
                return;
            }
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ReaderWriterConfigManager
{
    private readonly ReaderWriterLockSlim _lock = new();
    private AppConfig _current;

    public ReaderWriterConfigManager(AppConfig initial)
    {
        _current = initial;
    }

    public AppConfig Read()
    {
        _lock.EnterReadLock();
        try
        {
            return _current;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Update(Func<AppConfig, AppConfig> updater)
    {
        _lock.EnterWriteLock();
        try
        {
            _current = updater(_current);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ConfigManagerBenchmarks
{
    private CopyOnWriteConfigManager _copyOnWrite = null!;
    private ReaderWriterConfigManager _readerWriter = null!;

    [Params(100_000)]
    public int ReadOperations;

    [GlobalSetup]
    public void Setup()
    {
        AppConfig initial = AppConfig.Empty.Set("region", "us-east").Set("mode", "debug");
        _copyOnWrite = new CopyOnWriteConfigManager(initial);
        _readerWriter = new ReaderWriterConfigManager(initial);
    }

    [Benchmark(Baseline = true)]
    public int CopyOnWriteReadMostly()
    {
        int sum = 0;
        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < ReadOperations; i++)
                {
                    sum += _copyOnWrite.Read().Values.Count;
                }
            },
            () =>
            {
                for (int i = 0; i < 1_000; i++)
                {
                    int copy = i;
                    _copyOnWrite.Update(config => config.Set("version", copy.ToString()));
                }
            });

        return sum;
    }

    [Benchmark]
    public int ReaderWriterReadMostly()
    {
        int sum = 0;
        // Run several independent writers at once to surface cache and synchronization effects.
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < ReadOperations; i++)
                {
                    sum += _readerWriter.Read().Values.Count;
                }
            },
            () =>
            {
                for (int i = 0; i < 1_000; i++)
                {
                    int copy = i;
                    _readerWriter.Update(config => config.Set("version", copy.ToString()));
                }
            });

        return sum;
    }
}
