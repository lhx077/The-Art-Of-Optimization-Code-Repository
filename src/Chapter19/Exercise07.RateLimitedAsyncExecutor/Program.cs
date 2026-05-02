// ============================================================================
// Exercise 07 — Rate-Limited Async Executor
// ============================================================================
// Uses SemaphoreSlim for concurrency control and a priority-aware channel-backed
// dispatcher to apply request-per-second limits, cancellation, and timeouts.
// ============================================================================

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<RateLimitedExecutorBenchmarks>();
    return;
}

await using (RateLimitedAsyncExecutor executor = new(maxConcurrency: 4, maxRequestsPerSecond: 20))
{
    int value = await executor.ExecuteAsync(
        priority: 1,
        operation: async token =>
        {
            await Task.Delay(20, token);
            return 42;
        },
        timeout: TimeSpan.FromSeconds(2));

    Console.WriteLine(value);
}

// Core implementation used by the demo and benchmark code below.
public sealed class RateLimitedAsyncExecutor : IAsyncDisposable
{
    private readonly Channel<WorkItemBase> _channel;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly PeriodicTimer _rateTimer;
    private readonly Task _dispatcher;

    public RateLimitedAsyncExecutor(int maxConcurrency, int maxRequestsPerSecond)
    {
        // An unbounded channel keeps the sample simple while still separating producers from consumers.
        _channel = Channel.CreateUnbounded<WorkItemBase>();
        _concurrencyGate = new SemaphoreSlim(maxConcurrency);
        _rateTimer = new PeriodicTimer(TimeSpan.FromSeconds(1d / maxRequestsPerSecond));
        _dispatcher = Task.Run(DispatchAsync);
    }

    public async Task<T> ExecuteAsync<T>(int priority, Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        WorkItem<T> item = new(priority, operation, timeout, cancellationToken);
        await _channel.Writer.WriteAsync(item, cancellationToken);
        return await item.Task;
    }

    private async Task DispatchAsync()
    {
        PriorityQueue<WorkItemBase, int> queue = new();

        while (queue.Count > 0 || await _channel.Reader.WaitToReadAsync())
        {
            while (_channel.Reader.TryRead(out WorkItemBase? item))
            {
                queue.Enqueue(item, item.Priority);
            }

            if (queue.Count > 0)
            {
                await _rateTimer.WaitForNextTickAsync();
                await _concurrencyGate.WaitAsync();
                WorkItemBase next = queue.Dequeue();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await next.RunAsync();
                    }
                    finally
                    {
                        _concurrencyGate.Release();
                    }
                });
            }
        }
    }

    // Drain outstanding work and release every retained resource during shutdown.
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _dispatcher;
        _rateTimer.Dispose();
        _concurrencyGate.Dispose();
    }
}

public abstract class WorkItemBase
{
    public int Priority { get; }

    protected WorkItemBase(int priority)
    {
        Priority = priority;
    }

    // Drive the asynchronous demo workflow for the sample.
    public abstract Task RunAsync();
}

// Core implementation used by the demo and benchmark code below.
public sealed class WorkItem<T> : WorkItemBase
{
    private readonly Func<CancellationToken, Task<T>> _operation;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _callerToken;
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkItem(int priority, Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken callerToken)
        : base(priority)
    {
        _operation = operation;
        _timeout = timeout;
        _callerToken = callerToken;
    }

    public Task<T> Task => _tcs.Task;

    // Drive the asynchronous demo workflow for the sample.
    public override async Task RunAsync()
    {
        using CancellationTokenSource timeoutCts = new(_timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, timeoutCts.Token);

        try
        {
            T result = await _operation(linked.Token);
            _tcs.TrySetResult(result);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            _tcs.TrySetException(new TimeoutException("The operation timed out.", ex));
        }
        catch (OperationCanceledException ex)
        {
            _tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class RateLimitedExecutorBenchmarks
{
    [Benchmark]
    public async Task<int> ExecuteBurst()
    {
        await using RateLimitedAsyncExecutor executor = new(maxConcurrency: 8, maxRequestsPerSecond: 200);
        Task<int>[] tasks = Enumerable.Range(0, 100)
            .Select(i => executor.ExecuteAsync(i % 4, async token =>
            {
                await Task.Delay(1, token);
                return i;
            }, TimeSpan.FromSeconds(1)))
            .ToArray();

        int[] results = await Task.WhenAll(tasks);
        return results.Sum();
    }
}
