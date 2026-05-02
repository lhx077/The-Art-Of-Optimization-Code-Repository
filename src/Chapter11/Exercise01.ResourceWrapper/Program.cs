// ============================================================================
// Exercise 01 — Standard Resource Wrapper
// ============================================================================
// Implements the standard Dispose pattern with:
//   - IDisposable
//   - Protected virtual Dispose(bool disposing)
//   - Finalizer as the last safety net
//   - DEBUG leak warning if finalizer runs before explicit Dispose
//   - Thread-safe one-time cleanup using Interlocked
// ============================================================================

using System.Runtime.InteropServices;
using System.Threading;

Console.WriteLine("=== Resource Wrapper Demo ===");

using (var wrapper = new ResourceWrapper())
{
    Console.WriteLine($"Resource Id: {wrapper.ResourceId}");
    wrapper.Use();
}

Console.WriteLine("Disposed wrapper explicitly.");
Console.WriteLine();

var leaked = new ResourceWrapper();
leaked.Use();
Console.WriteLine("Created a leaked wrapper for finalizer demonstration.");

leaked = null!;
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

Console.WriteLine("Finalizer cycle completed.");

// ResourceWrapper models the classic case where a managed object owns a native
// resource and must support both explicit disposal and finalizer-based cleanup.
public class ResourceWrapper : IDisposable
{
    private IntPtr _nativeResource;
    private int _disposeSignaled;
    private bool _disposed;

    public int ResourceId { get; }

    public ResourceWrapper()
    {
        ResourceId = NativeResourceSimulator.Acquire();
        _nativeResource = new IntPtr(ResourceId);
    }

    ~ResourceWrapper()
    {
#if DEBUG
        if (!_disposed)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Leak Warning] ResourceWrapper {ResourceId} was finalized without explicit Dispose.");
            Console.ResetColor();
        }
#endif
        Dispose(false);
    }

    public void Use()
    {
        ThrowIfDisposed();
        Console.WriteLine($"Using native resource {ResourceId}.");
    }

    public void Dispose()
    {
        // Suppress the finalizer because explicit Dispose already released the
        // resource and running finalization again would only add overhead.
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        // Ensure cleanup runs exactly once across all threads.
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        if (disposing)
        {
            // This branch would release managed dependencies that also need Dispose.
        }

        if (_nativeResource != IntPtr.Zero)
        {
            // The simulator release stands in for the unmanaged free/close call.
            NativeResourceSimulator.Release(_nativeResource.ToInt32());
            _nativeResource = IntPtr.Zero;
        }

        _disposed = true;
    }

    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

// NativeResourceSimulator stands in for an unmanaged API so the sample can show
// ownership transfer and release without requiring real OS resources.
public static class NativeResourceSimulator
{
    private static int _nextId;
    private static int _activeCount;

    public static int Acquire()
    {
        int id = Interlocked.Increment(ref _nextId);
        Interlocked.Increment(ref _activeCount);
        Console.WriteLine($"Acquired simulated native resource {id}. Active={_activeCount}");
        return id;
    }

    public static void Release(int id)
    {
        int remaining = Interlocked.Decrement(ref _activeCount);
        Console.WriteLine($"Released simulated native resource {id}. Active={remaining}");
    }
}
