// ============================================================================
// Exercise 04 — SynchronizationContext Deadlock Demonstration
// ============================================================================
// Demonstrates the classic deadlock pattern with a custom single-threaded
// SynchronizationContext and shows how ConfigureAwait(false) breaks the cycle.
// ============================================================================

Console.WriteLine("Deadlock demo starting.");
DeadlockDemo.RunWithoutFix();
Console.WriteLine();
DeadlockDemo.RunWithFix();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class DeadlockDemo
{
    // Drive the shared workload used by the demo or benchmark entry point.
    public static void RunWithoutFix()
    {
        using SingleThreadSynchronizationContext context = new();
        context.Post(_ =>
        {
            try
            {
                Console.WriteLine("Starting blocking wait without ConfigureAwait(false)...");
                string result = LibraryMethodAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Unexpected success: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Observed exception: {ex.Message}");
            }
            finally
            {
                context.Complete();
            }
        }, null);

        context.RunFor(TimeSpan.FromMilliseconds(200));
        Console.WriteLine("The continuation could not run because the context thread was blocked.");
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    public static void RunWithFix()
    {
        using SingleThreadSynchronizationContext context = new();
        context.Post(_ =>
        {
            try
            {
                Console.WriteLine("Starting blocking wait with ConfigureAwait(false)...");
                string result = LibraryMethodWithFixAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Completed successfully: {result}");
            }
            finally
            {
                context.Complete();
            }
        }, null);

        context.Run();
    }

    private static async Task<string> LibraryMethodAsync()
    {
        await Task.Delay(50);
        return "done";
    }

    private static async Task<string> LibraryMethodWithFixAsync()
    {
        await Task.Delay(50).ConfigureAwait(false);
        return "done";
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private bool _completed;

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_queue)
        {
            _queue.Enqueue((d, state));
        }

        _signal.Set();
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    public void Run()
    {
        SynchronizationContext.SetSynchronizationContext(this);
        while (true)
        {
            (SendOrPostCallback Callback, object? State)? work = null;
            lock (_queue)
            {
                if (_queue.Count > 0)
                {
                    work = _queue.Dequeue();
                }
                else if (_completed)
                {
                    break;
                }
            }

            if (work is { } item)
            {
                item.Callback(item.State);
            }
            else
            {
                _signal.WaitOne();
            }
        }
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    public void RunFor(TimeSpan timeout)
    {
        SynchronizationContext.SetSynchronizationContext(this);
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            (SendOrPostCallback Callback, object? State)? work = null;
            lock (_queue)
            {
                if (_queue.Count > 0)
                {
                    work = _queue.Dequeue();
                }
                else if (_completed)
                {
                    break;
                }
            }

            if (work is { } item)
            {
                item.Callback(item.State);
            }
            else
            {
                _signal.WaitOne(10);
            }
        }
    }

    public void Complete()
    {
        lock (_queue)
        {
            _completed = true;
        }

        _signal.Set();
    }

    public void Dispose() => _signal.Dispose();
}
