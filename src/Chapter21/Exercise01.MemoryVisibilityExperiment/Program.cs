// ============================================================================
// Exercise 01 — Memory Visibility Experiment
// ============================================================================
// Compares plain fields, volatile fields, and Interlocked-based signaling in a
// two-thread publication scenario.
// ============================================================================

Console.WriteLine("Plain field test:");
RunPlainFieldTest();
Console.WriteLine();
Console.WriteLine("Volatile field test:");
RunVolatileFieldTest();
Console.WriteLine();
Console.WriteLine("Interlocked test:");
RunInterlockedTest();

static void RunPlainFieldTest()
{
    PlainState state = new();
    Thread writer = new(() =>
    {
        Thread.Sleep(100);
        state.Data = 42;
        state.Ready = true;
    }) { IsBackground = true };

    Thread reader = new(() =>
    {
        // In Release mode the JIT may hoist state.Ready into a register before the
        // loop, turning this into an infinite loop that never observes the update.
        while (!state.Ready)
        {
        }

        Console.WriteLine($"Observed data: {state.Data}");
    }) { IsBackground = true };

    reader.Start();
    writer.Start();
    writer.Join();

    if (!reader.Join(TimeSpan.FromSeconds(3)))
    {
        Console.WriteLine("Reader is stuck — the JIT hoisted the plain field read out of the loop.");
        Console.WriteLine("This demonstrates the memory visibility problem with non-volatile fields.");
    }
}

static void RunVolatileFieldTest()
{
    VolatileState state = new();
    Thread writer = new(() =>
    {
        state.Data = 42;
        Volatile.Write(ref state.Ready, true);
    });

    Thread reader = new(() =>
    {
        SpinWait spin = new();
        // Read the shared state with visibility guarantees before making decisions.
        while (!Volatile.Read(ref state.Ready))
        {
            spin.SpinOnce();
        }

        Console.WriteLine($"Observed data: {state.Data}");
    });

    reader.Start();
    writer.Start();
    writer.Join();
    reader.Join();
}

static void RunInterlockedTest()
{
    InterlockedState state = new();
    Thread writer = new(() =>
    {
        state.Data = 42;
        Interlocked.Exchange(ref state.Ready, 1);
    });

    Thread reader = new(() =>
    {
        SpinWait spin = new();
        // Publish the updated state only if no other thread changed it first.
        while (Interlocked.CompareExchange(ref state.Ready, 1, 1) == 0)
        {
            spin.SpinOnce();
        }

        Console.WriteLine($"Observed data: {state.Data}");
    });

    reader.Start();
    writer.Start();
    writer.Join();
    reader.Join();
}

// Core implementation used by the demo and benchmark code below.
public sealed class PlainState
{
    public int Data;
    public bool Ready;
}

// Core implementation used by the demo and benchmark code below.
public sealed class VolatileState
{
    public int Data;
    public bool Ready;
}

// Core implementation used by the demo and benchmark code below.
public sealed class InterlockedState
{
    public int Data;
    public int Ready;
}
