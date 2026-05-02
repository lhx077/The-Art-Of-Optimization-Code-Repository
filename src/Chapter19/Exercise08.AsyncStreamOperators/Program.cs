// ============================================================================
// Exercise 08 — Async Stream Operators
// ============================================================================
// Provides simple Buffer, Throttle, Merge, and Zip operators for IAsyncEnumerable<T>
// with cancellation-aware implementations and small demonstrations.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Threading.Channels;

await Demo.RunAsync();

// Core implementation used by the demo and benchmark code below.
public static class AsyncEnumerableOperators
{
    public static async IAsyncEnumerable<IReadOnlyList<T>> Buffer<T>(this IAsyncEnumerable<T> source, int size, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<T> batch = new(size);
        await foreach (T item in source.WithCancellation(cancellationToken))
        {
            batch.Add(item);
            if (batch.Count >= size)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    public static async IAsyncEnumerable<T> Throttle<T>(this IAsyncEnumerable<T> source, TimeSpan delay, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (T item in source.WithCancellation(cancellationToken))
        {
            yield return item;
            await Task.Delay(delay, cancellationToken);
        }
    }

    public static async IAsyncEnumerable<T> Merge<T>(IAsyncEnumerable<T> first, IAsyncEnumerable<T> second, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<T> channel = Channel.CreateUnbounded<T>();

        Task PumpAsync(IAsyncEnumerable<T> source) => Task.Run(async () =>
        {
            try
            {
                await foreach (T item in source.WithCancellation(linkedCts.Token))
                {
                    await channel.Writer.WriteAsync(item, linkedCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, linkedCts.Token);

        Task left = PumpAsync(first);
        Task right = PumpAsync(second);
        _ = Task.WhenAll(left, right).ContinueWith(t => channel.Writer.TryComplete(t.Exception));

        try
        {
            await foreach (T item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            linkedCts.Cancel();
        }
    }

    public static async IAsyncEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(IAsyncEnumerable<TFirst> first, IAsyncEnumerable<TSecond> second, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using IAsyncEnumerator<TFirst> left = first.GetAsyncEnumerator(cancellationToken);
        await using IAsyncEnumerator<TSecond> right = second.GetAsyncEnumerator(cancellationToken);

        while (await left.MoveNextAsync() && await right.MoveNextAsync())
        {
            yield return (left.Current, right.Current);
        }
    }
}

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        Console.WriteLine("Buffer:");
        await foreach (IReadOnlyList<int> batch in RangeAsync(5).Buffer(2))
        {
            Console.WriteLine(string.Join(",", batch));
        }

        Console.WriteLine("Throttle:");
        await foreach (int value in RangeAsync(3).Throttle(TimeSpan.FromMilliseconds(5)))
        {
            Console.WriteLine(value);
        }

        Console.WriteLine("Merge:");
        await foreach (int value in AsyncEnumerableOperators.Merge(RangeAsync(3), RangeAsync(3, 10)))
        {
            Console.WriteLine(value);
        }

        Console.WriteLine("Zip:");
        await foreach ((int A, int B) pair in AsyncEnumerableOperators.Zip(RangeAsync(3), RangeAsync(3, 100)))
        {
            Console.WriteLine($"{pair.A}:{pair.B}");
        }
    }

    private static async IAsyncEnumerable<int> RangeAsync(int count, int start = 0)
    {
        for (int i = 0; i < count; i++)
        {
            await Task.Delay(1);
            yield return start + i;
        }
    }
}
