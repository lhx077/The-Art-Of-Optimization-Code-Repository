// ============================================================================
// Exercise 04 — Memory Leak Diagnosis
// ============================================================================
// Simulates two classic managed leaks:
//   1. Event subscriptions that are never removed.
//   2. An unbounded static collection.
//
// Uses GC.GetTotalMemory and WeakReference to demonstrate retention before and
// after fixes. Also prints tool guidance for heap dump analysis.
// ============================================================================

Console.WriteLine("=== Memory Leak Diagnosis Demo ===");
Console.WriteLine();

LeakScenario.Run(leakMode: true);
Console.WriteLine();
LeakScenario.Run(leakMode: false);

// LeakScenario contrasts a leaking setup with a fixed one so the same workload
// can be observed under two different lifetime-management strategies.
public static class LeakScenario
{
    // Drive the shared workload used by the demo or benchmark entry point.
    public static void Run(bool leakMode)
    {
        Console.WriteLine(leakMode ? "--- Leak Mode ---" : "--- Fixed Mode ---");

        var publisher = new GlobalPublisher();
        var weakReferences = new List<WeakReference>(2000);

        for (int i = 0; i < 2000; i++)
        {
            var subscriber = new Subscriber(i);
            weakReferences.Add(new WeakReference(subscriber));

            publisher.Tick += subscriber.OnTick;

            // Leak mode intentionally leaves both the event subscription and static cache rooted.
            if (leakMode)
            {
                // The static list never evicts entries, so each iteration leaves
                // one more object graph reachable for the lifetime of the process.
                StaticLeakyCache.Items.Add($"cache-item-{i}");
            }
            else
            {
                // The disposable registration unsubscribes immediately, and the
                // fixed cache trims old entries so retained memory stays bounded.
                using var registration = new EventSubscription(publisher, subscriber.OnTick);
                FixedCache.Items.Add($"cache-item-{i}");
                if (FixedCache.Items.Count > 128)
                    FixedCache.Items.RemoveAt(0);
            }
        }

        ForceCollection();

        int alive = weakReferences.Count(wr => wr.IsAlive);
        long memory = GC.GetTotalMemory(forceFullCollection: true);

        Console.WriteLine($"Alive subscribers after GC: {alive}");
        Console.WriteLine($"Managed memory after GC:    {memory:N0} bytes");
        Console.WriteLine($"StaticLeakyCache size:      {StaticLeakyCache.Items.Count}");
        Console.WriteLine($"FixedCache size:            {FixedCache.Items.Count}");

        Console.WriteLine("Suggested external tools:");
        Console.WriteLine("  - dotnet-gcdump collect -p <pid>");
        Console.WriteLine("  - dotnet-dump collect -p <pid>");
        Console.WriteLine("  - Visual Studio Memory Usage snapshot diff");
    }

    private static void ForceCollection()
    {
        // Forcing a full collection makes the demo deterministic enough to show
        // which subscribers remain reachable after the workload has finished.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

// GlobalPublisher is intentionally long-lived so leaked subscribers remain rooted
// through its event field after the local loop variable goes out of scope.
public sealed class GlobalPublisher
{
    public event EventHandler? Tick;
    public void Raise() => Tick?.Invoke(this, EventArgs.Empty);
}

// Core implementation used by the demo and benchmark code below.
public sealed class Subscriber
{
    private readonly int _id;
    public Subscriber(int id) => _id = id;
    public void OnTick(object? sender, EventArgs args) { }
    public override string ToString() => $"Subscriber {_id}";
}

// EventSubscription models the explicit unsubscribe pattern that prevents the
// publisher from keeping subscribers alive longer than intended.
public sealed class EventSubscription : IDisposable
{
    private readonly GlobalPublisher _publisher;
    private readonly EventHandler _handler;
    private bool _disposed;

    public EventSubscription(GlobalPublisher publisher, EventHandler handler)
    {
        _publisher = publisher;
        _handler = handler;
        _publisher.Tick += _handler;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _publisher.Tick -= _handler;
        _disposed = true;
    }
}

// StaticLeakyCache never evicts anything, so every inserted item behaves like a
// process-wide root for as long as the application stays alive.
public static class StaticLeakyCache
{
    public static List<string> Items { get; } = [];
}

// FixedCache keeps the same basic functionality but bounds retained entries so
// the cache cannot grow without limit under steady traffic.
public static class FixedCache
{
    public static List<string> Items { get; } = [];
}
