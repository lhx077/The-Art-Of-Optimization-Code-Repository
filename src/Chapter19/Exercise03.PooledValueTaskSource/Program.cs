// ============================================================================
// Exercise 03 — Pooled IValueTaskSource<T>
// ============================================================================
// Implements a reusable IValueTaskSource<T> backed by ManualResetValueTaskSourceCore<T>
// with a small object pool and simple self-tests for result, exception, and cancel.
// ============================================================================

using System.Threading.Tasks.Sources;

await SelfTest.RunAsync();
Console.WriteLine("Pooled value-task-source self-tests completed.");

// Core implementation used by the demo and benchmark code below.
public sealed class PooledOperationSource<T> : IValueTaskSource<T>
{
    private static readonly ObjectPool<PooledOperationSource<T>> s_pool = new(() => new PooledOperationSource<T>());
    private ManualResetValueTaskSourceCore<T> _core;
    private int _inUse;

    private PooledOperationSource()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    // Prefer a reusable instance before falling back to allocation.
    public static PooledOperationSource<T> Rent()
    {
        PooledOperationSource<T> item = s_pool.Rent();
        if (Interlocked.Exchange(ref item._inUse, 1) != 0)
        {
            throw new InvalidOperationException("Source was rented while already in use.");
        }

        item._core.Reset();
        return item;
    }

    // Bind the current version token so awaiters cannot observe a recycled source.
    public ValueTask<T> CreateTask() => new(this, _core.Version);

    public void SetResult(T result) => _core.SetResult(result);
    public void SetException(Exception exception) => _core.SetException(exception);
    public void SetCanceled(CancellationToken cancellationToken = default) => _core.SetException(new OperationCanceledException(cancellationToken));

    // Return the result and recycle the pooled source as soon as the await completes.
    public T GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            ReturnToPool();
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    private void ReturnToPool()
    {
        if (Interlocked.Exchange(ref _inUse, 0) == 1)
        {
            s_pool.Return(this);
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ObjectPool<T> where T : class
{
    private readonly Func<T> _factory;
    private readonly Stack<T> _items = new();
    private readonly object _gate = new();

    public ObjectPool(Func<T> factory)
    {
        _factory = factory;
    }

    // Prefer a reusable instance before falling back to allocation.
    public T Rent()
    {
        lock (_gate)
        {
            return _items.Count > 0 ? _items.Pop() : _factory();
        }
    }

    // Place the instance back into the pool after the caller is done with it.
    public void Return(T item)
    {
        lock (_gate)
        {
            _items.Push(item);
        }
    }
}

// Keep a lightweight self-test nearby so concurrency regressions fail fast.
public static class SelfTest
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        await TestResultAsync();
        await TestExceptionAsync();
        await TestCancellationAsync();
    }

    // Validate the happy path before comparing more advanced scenarios.
    private static async Task TestResultAsync()
    {
        PooledOperationSource<int> source = PooledOperationSource<int>.Rent();
        ValueTask<int> task = source.CreateTask();
        _ = Task.Run(() => source.SetResult(123));
        int result = await task;
        if (result != 123) throw new InvalidOperationException("Unexpected result.");
    }

    // Confirm that exceptions flow through the pooled source correctly.
    private static async Task TestExceptionAsync()
    {
        PooledOperationSource<int> source = PooledOperationSource<int>.Rent();
        ValueTask<int> task = source.CreateTask();
        _ = Task.Run(() => source.SetException(new InvalidOperationException("boom")));
        try
        {
            _ = await task;
            throw new InvalidOperationException("Expected exception was not thrown.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "boom")
        {
        }
    }

    // Confirm that cancellation still returns the source to the pool safely.
    private static async Task TestCancellationAsync()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        PooledOperationSource<int> source = PooledOperationSource<int>.Rent();
        ValueTask<int> task = source.CreateTask();
        _ = Task.Run(() => source.SetCanceled(cts.Token));
        try
        {
            _ = await task;
            throw new InvalidOperationException("Expected cancellation was not thrown.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
