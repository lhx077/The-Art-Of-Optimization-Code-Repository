// ============================================================================
// Exercise 05 — Async Resource Pool
// ============================================================================
// Implements a resource pool with async rent/return plus IAsyncDisposable.
// DisposeAsync waits for outstanding rentals, applies a timeout, aggregates
// cleanup exceptions, and ensures resources are released robustly.
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

Console.WriteLine("=== Async Resource Pool Demo ===");

await using (var pool = new AsyncResourcePool<SimulatedAsyncResource>(
    create: static async ct =>
    {
        await Task.Delay(10, ct);
        return new SimulatedAsyncResource();
    },
    disposeTimeout: TimeSpan.FromSeconds(2),
    maxSize: 4))
{
    var resource1 = await pool.RentAsync();
    var resource2 = await pool.RentAsync();

    await resource1.Resource.DoWorkAsync();
    await resource2.Resource.DoWorkAsync();

    await resource1.DisposeAsync();
    await resource2.DisposeAsync();
}

Console.WriteLine("Pool disposed.");

// AsyncResourcePool coordinates asynchronous acquisition and return while keeping
// the number of live resources bounded by the semaphore gate.
public sealed class AsyncResourcePool<T> : IAsyncDisposable where T : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<T>> _create;
    private readonly ConcurrentQueue<T> _available = new();
    private readonly ConcurrentDictionary<int, T> _leased = new();
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _disposeTimeout;
    private int _nextLeaseId;
    private bool _disposed;

    public AsyncResourcePool(Func<CancellationToken, Task<T>> create, TimeSpan disposeTimeout, int maxSize)
    {
        _create = create;
        _disposeTimeout = disposeTimeout;
        _gate = new SemaphoreSlim(maxSize, maxSize);
    }

    // Wait for capacity, then either reuse an existing instance or create a fresh one.
    // The queue holds idle resources, while the lease table tracks resources that
    // must not be disposed during shutdown until callers return them.
    public async Task<PooledResource<T>> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);

        T resource;
        if (!_available.TryDequeue(out resource!))
        {
            // Create only when the idle queue is empty; the semaphore already capped concurrency.
            resource = await _create(cancellationToken);
        }

        int leaseId = Interlocked.Increment(ref _nextLeaseId);
        _leased[leaseId] = resource;
        return new PooledResource<T>(this, leaseId, resource);
    }

    // Put the resource back into circulation unless shutdown has already started.
    // The lease record must be removed first in every path; otherwise DisposeAsync
    // can wait forever on a lease that was logically returned during shutdown.
    private async ValueTask ReturnAsync(int leaseId, T resource)
    {
        _leased.TryRemove(leaseId, out _);

        if (_disposed)
        {
            await resource.DisposeAsync();
            return;
        }

        _available.Enqueue(resource);
        _gate.Release();
    }

    // Mark the pool as shutting down, wait briefly for outstanding leases to come
    // back, then dispose both idle resources and any leases that never returned.
    // Cleanup failures are collected so callers can see the full shutdown result.
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        using var cts = new CancellationTokenSource(_disposeTimeout);
        var exceptions = new List<Exception>();
        var stopwatch = Stopwatch.StartNew();

        while (_leased.Count > 0 && !cts.IsCancellationRequested)
        {
            // Poll briefly instead of spinning so returning callers get time to finish naturally.
            await Task.Delay(25, cts.Token).ContinueWith(_ => { });
        }

        if (_leased.Count > 0)
        {
            exceptions.Add(new TimeoutException("Timed out waiting for all leased resources to be returned."));
        }

        while (_available.TryDequeue(out T? resource))
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        foreach (var pair in _leased)
        {
            try
            {
                // Any lease still present after the wait is force-cleaned by the pool itself.
                await pair.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        _gate.Dispose();

        if (exceptions.Count == 1)
            throw exceptions[0];
        if (exceptions.Count > 1)
            throw new AggregateException(exceptions);

        Console.WriteLine($"DisposeAsync completed in {stopwatch.ElapsedMilliseconds} ms.");
    }

    public readonly struct PooledResource<TResource> : IAsyncDisposable where TResource : IAsyncDisposable
    {
        private readonly AsyncResourcePool<TResource> _pool;
        private readonly int _leaseId;
        public TResource Resource { get; }

        internal PooledResource(AsyncResourcePool<TResource> pool, int leaseId, TResource resource)
        {
            _pool = pool;
            _leaseId = leaseId;
            Resource = resource;
        }

        // Returning the lease hands the resource back to the pool; the resource
        // itself is disposed only when the pool decides it should leave service.
        public ValueTask DisposeAsync() => _pool.ReturnAsync(_leaseId, Resource);
    }
}

// SimulatedAsyncResource stands in for an expensive asynchronous dependency such
// as a network session or database channel.
public sealed class SimulatedAsyncResource : IAsyncDisposable
{
    private static int _nextId;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private bool _disposed;

    // Simulate real work performed by the borrowed dependency before the caller
    // returns it to the pool.
    public async Task DoWorkAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Delay(20);
        Console.WriteLine($"Resource {_id} completed work.");
    }

    // Simulate asynchronous teardown work so the demo shows that pooled resources
    // may need their own awaited cleanup before they fully leave service.
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await Task.Delay(10);
        _disposed = true;
        Console.WriteLine($"Resource {_id} disposed asynchronously.");
    }
}
