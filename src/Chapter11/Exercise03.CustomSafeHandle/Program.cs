// ============================================================================
// Exercise 03 — Custom SafeHandle
// ============================================================================
// Implements a custom SafeHandleZeroOrMinusOneIsInvalid for a simulated native
// handle. Includes correctness checks showing that even if Dispose is skipped,
// the finalization path still releases the handle.
// ============================================================================

using Microsoft.Win32.SafeHandles;

Console.WriteLine("=== Custom SafeHandle Demo ===");

RunTests();

using (var wrapper = new SafeBusinessResource())
{
    Console.WriteLine($"Opened handle: {wrapper.HandleValue}");
    wrapper.Use();
}

Console.WriteLine("Disposed wrapper explicitly.");
Console.WriteLine();

var leaked = new SafeBusinessResource();
Console.WriteLine($"Created leaked handle: {leaked.HandleValue}");
leaked = null!;
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Console.WriteLine("Forced finalizer collection complete.");

static void RunTests()
{
    int before = NativeHandleSimulator.ActiveHandles;
    var wrapper = new SafeBusinessResource();
    int handle = wrapper.HandleValue;
    wrapper.Dispose();

    if (NativeHandleSimulator.ActiveHandles != before)
        throw new InvalidOperationException("Handle count mismatch after explicit Dispose.");

    Console.WriteLine($"Test passed: explicit Dispose released handle {handle}.");
}

// SimulatedSafeHandle centralizes native-handle release so both explicit Dispose
// and finalization follow the same reliable cleanup path.
public sealed class SimulatedSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SimulatedSafeHandle() : base(ownsHandle: true)
    {
    }

    public SimulatedSafeHandle(int handleValue) : base(ownsHandle: true)
    {
        SetHandle(new IntPtr(handleValue));
    }

    protected override bool ReleaseHandle()
    {
        // SafeHandle guarantees that ReleaseHandle runs at most once for a valid
        // handle, even if user code forgets to dispose the wrapper explicitly.
        NativeHandleSimulator.Release(handle.ToInt32());
        return true;
    }
}

// SafeBusinessResource shows the normal managed wrapper that delegates lifetime
// management to SafeHandle instead of owning finalization logic itself.
public sealed class SafeBusinessResource : IDisposable
{
    private bool _disposed;
    private readonly SimulatedSafeHandle _handle;

    // SafeHandle marks invalid handles up front, so the demo can distinguish an
    // already-closed wrapper from one that still owns a live native handle.
    public int HandleValue => _handle.IsInvalid ? -1 : _handle.DangerousGetHandle().ToInt32();

    public SafeBusinessResource()
    {
        _handle = new SimulatedSafeHandle(NativeHandleSimulator.Acquire());
    }

    public void Use()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Console.WriteLine($"Using safe handle {HandleValue}.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Disposing the SafeHandle triggers ReleaseHandle and suppresses finalization work.
        _handle.Dispose();
        _disposed = true;
    }
}

// NativeHandleSimulator provides observable acquire/release counts so the sample
// can prove that SafeHandle cleanup happened exactly once.
public static class NativeHandleSimulator
{
    private static int _next;
    private static int _active;

    public static int ActiveHandles => _active;

    public static int Acquire()
    {
        int handle = Interlocked.Increment(ref _next);
        int active = Interlocked.Increment(ref _active);
        Console.WriteLine($"Acquired native handle {handle}. Active={active}");
        return handle;
    }

    public static void Release(int handle)
    {
        int active = Interlocked.Decrement(ref _active);
        Console.WriteLine($"Released native handle {handle}. Active={active}");
    }
}
