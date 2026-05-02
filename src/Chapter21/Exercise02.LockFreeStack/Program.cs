// ============================================================================
// Exercise 02 — Lock-Free Stack
// ============================================================================
// Implements a Treiber stack with Push, Pop, TryPop, Count, multi-threaded tests,
// and BenchmarkDotNet comparison against ConcurrentStack<T>.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<LockFreeStackBenchmarks>();
    return;
}

SelfTest.Run();
Console.WriteLine("Lock-free stack self-test completed.");

// Core implementation used by the demo and benchmark code below.
public sealed class LockFreeStack<T>
{
    private Node? _head;
    private int _count;

    // Read the shared state with visibility guarantees before making decisions.
    public int Count => Volatile.Read(ref _count);

    // Link the new node to the observed head before attempting to publish it.
    public void Push(T value)
    {
        Node node = new(value);
        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            Node? observedHead = Volatile.Read(ref _head);
            node.Next = observedHead;
            // Publish the updated state only if no other thread changed it first.
            if (Interlocked.CompareExchange(ref _head, node, observedHead) == observedHead)
            {
                Interlocked.Increment(ref _count);
                return;
            }
        }
    }

    // Expose the throwing convenience API on top of the non-throwing fast path.
    public T Pop()
    {
        if (!TryPop(out T? value))
        {
            throw new InvalidOperationException("The stack is empty.");
        }

        return value!;
    }

    // Retry until the same head we observed is removed successfully.
    public bool TryPop(out T? value)
    {
        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            Node? observedHead = Volatile.Read(ref _head);
            if (observedHead is null)
            {
                value = default;
                return false;
            }

            Node? next = observedHead.Next;
            // Publish the updated state only if no other thread changed it first.
            if (Interlocked.CompareExchange(ref _head, next, observedHead) == observedHead)
            {
                Interlocked.Decrement(ref _count);
                value = observedHead.Value;
                return true;
            }
        }
    }

    private sealed class Node
    {
        public T Value { get; }
        public Node? Next;

        public Node(T value)
        {
            Value = value;
        }
    }
}

// Keep a lightweight self-test nearby so concurrency regressions fail fast.
public static class SelfTest
{
    // Drive the shared workload used by the demo or benchmark entry point.
    public static void Run()
    {
        LockFreeStack<int> stack = new();
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 10_000, i => stack.Push(i));

        int popCount = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, 10_000, _ =>
        {
            if (stack.TryPop(out _))
            {
                Interlocked.Increment(ref popCount);
            }
        });

        if (popCount != 10_000 || stack.Count != 0)
        {
            throw new InvalidOperationException("Stack test failed.");
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class LockFreeStackBenchmarks
{
    [Params(1, 8)]
    public int WorkerCount;

    [Params(50_000)]
    public int OperationsPerWorker;

    [Benchmark(Baseline = true)]
    public int CustomLockFreeStack()
    {
        LockFreeStack<int> stack = new();
        int popped = 0;

        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, _ =>
        {
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                stack.Push(i);
                if (stack.TryPop(out _))
                {
                    Interlocked.Increment(ref popped);
                }
            }
        });

        return popped;
    }

    [Benchmark]
    public int ConcurrentStackBenchmark()
    {
        ConcurrentStack<int> stack = new();
        int popped = 0;

        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, _ =>
        {
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                stack.Push(i);
                if (stack.TryPop(out _))
                {
                    Interlocked.Increment(ref popped);
                }
            }
        });

        return popped;
    }
}
