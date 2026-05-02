// ============================================================================
// Exercise 04 — Channel-Based Log Aggregator with Backpressure
// ============================================================================
// Creates a bounded multi-producer/single-consumer logging pipeline and measures
// the effect of buffer size and full-mode policy on throughput and batching.
// ============================================================================

using System.Text;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ChannelAggregatorBenchmarks>();
    return;
}

await using (ChannelLogAggregator aggregator = new(capacity: 128, fullMode: BoundedChannelFullMode.Wait, batchSize: 16))
{
    await aggregator.LogAsync("System started.");
    await aggregator.LogAsync("Worker connected.");
    await aggregator.FlushAndStopAsync();
    Console.WriteLine($"Buffered bytes written: {aggregator.TotalCharactersWritten}");
}

// Core implementation used by the demo and benchmark code below.
public sealed class ChannelLogAggregator : IAsyncDisposable
{
    private readonly Channel<string> _channel;
    private readonly int _batchSize;
    private readonly StringBuilder _sink = new();
    private readonly Task _consumerTask;
    private long _totalCharactersWritten;

    public ChannelLogAggregator(int capacity, BoundedChannelFullMode fullMode, int batchSize)
    {
        _batchSize = batchSize;
        // A bounded channel lets downstream slowdown push back on producers.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = fullMode
        });
        _consumerTask = Task.Run(ConsumeAsync);
    }

    // Read the shared state with visibility guarantees before making decisions.
    public long TotalCharactersWritten => Volatile.Read(ref _totalCharactersWritten);

    public ValueTask LogAsync(string message, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public async Task FlushAndStopAsync()
    {
        _channel.Writer.TryComplete();
        await _consumerTask;
    }

    private async Task ConsumeAsync()
    {
        List<string> batch = new(_batchSize);
        await foreach (string message in _channel.Reader.ReadAllAsync())
        {
            batch.Add(message);
            if (batch.Count >= _batchSize)
            {
                FlushBatch(batch);
            }
        }

        if (batch.Count > 0)
        {
            FlushBatch(batch);
        }
    }

    private void FlushBatch(List<string> batch)
    {
        foreach (string message in batch)
        {
            _sink.AppendLine(message);
            _totalCharactersWritten += message.Length;
        }

        batch.Clear();
    }

    // Drain outstanding work and release every retained resource during shutdown.
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumerTask;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class ChannelAggregatorBenchmarks
{
    [Params(64, 1024)]
    public int Capacity;

    [Params(BoundedChannelFullMode.Wait, BoundedChannelFullMode.DropWrite)]
    public BoundedChannelFullMode FullMode;

    [Params(32)]
    public int ProducerCount;

    [Params(2_000)]
    public int MessagesPerProducer;

    [Benchmark]
    public async Task<long> AggregateAsync()
    {
        await using ChannelLogAggregator aggregator = new(Capacity, FullMode, batchSize: 64);

        Task[] producers = Enumerable.Range(0, ProducerCount)
            .Select(producer => Task.Run(async () =>
            {
                for (int i = 0; i < MessagesPerProducer; i++)
                {
                    await aggregator.LogAsync($"producer={producer}; index={i}; timestamp={DateTime.UtcNow.Ticks}");
                }
            }))
            .ToArray();

        await Task.WhenAll(producers);
        await aggregator.FlushAndStopAsync();
        return aggregator.TotalCharactersWritten;
    }
}
