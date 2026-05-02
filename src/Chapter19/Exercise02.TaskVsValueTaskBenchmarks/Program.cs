// ============================================================================
// Exercise 02 — Task versus ValueTask Benchmarks
// ============================================================================
// Compares sync and async completion paths for Task<int> and ValueTask<int>, plus
// a small ConfigureAwait(false) observation benchmark.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<TaskVsValueTaskBenchmarks>();
    return;
}

Console.WriteLine(await Demo.ValueTaskAsync(true));
Console.WriteLine(await Demo.TaskAsync(false));

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    public static async Task<int> TaskAsync(bool synchronous)
    {
        if (!synchronous)
        {
            await Task.Yield();
        }

        return 42;
    }

    public static async ValueTask<int> ValueTaskAsync(bool synchronous)
    {
        if (!synchronous)
        {
            await Task.Yield();
        }

        return 42;
    }

    public static async Task<int> ConfigureAwaitDefaultAsync()
    {
        await Task.Delay(1);
        return Environment.CurrentManagedThreadId;
    }

    public static async Task<int> ConfigureAwaitFalseAsync()
    {
        await Task.Delay(1).ConfigureAwait(false);
        return Environment.CurrentManagedThreadId;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class TaskVsValueTaskBenchmarks
{
    [Benchmark(Baseline = true)]
    public async Task<int> TaskSyncCompletion()
    {
        return await Demo.TaskAsync(synchronous: true);
    }

    [Benchmark]
    public async ValueTask<int> ValueTaskSyncCompletion()
    {
        return await Demo.ValueTaskAsync(synchronous: true);
    }

    [Benchmark]
    public async Task<int> TaskAsyncCompletion()
    {
        return await Demo.TaskAsync(synchronous: false);
    }

    [Benchmark]
    public async ValueTask<int> ValueTaskAsyncCompletion()
    {
        return await Demo.ValueTaskAsync(synchronous: false);
    }

    [Benchmark]
    public async Task<int> ConfigureAwaitDefault()
    {
        return await Demo.ConfigureAwaitDefaultAsync();
    }

    [Benchmark]
    public async Task<int> ConfigureAwaitFalse()
    {
        return await Demo.ConfigureAwaitFalseAsync();
    }
}
