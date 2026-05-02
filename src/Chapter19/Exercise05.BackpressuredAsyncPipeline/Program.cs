// ============================================================================
// Exercise 05 — Backpressured Async Processing Pipeline
// ============================================================================
// Uses IAsyncEnumerable<T> as the source and bounded channels between stages to
// demonstrate buffering, backpressure, and configurable parallel processing.
// ============================================================================

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<PipelineBenchmarks>();
    return;
}

Pipeline<int, string> pipeline = PipelineFactory.Create(bufferSize: 32, stage1Parallelism: 2, stage2Parallelism: 2);
int count = await pipeline.RunAsync(Source(20));
Console.WriteLine($"Processed items: {count}");

static async IAsyncEnumerable<int> Source(int count)
{
    for (int i = 0; i < count; i++)
    {
        await Task.Delay(1);
        yield return i;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class Pipeline<TInput, TOutput>
{
    private readonly Func<IAsyncEnumerable<TInput>, Task<int>> _runner;

    public Pipeline(Func<IAsyncEnumerable<TInput>, Task<int>> runner)
    {
        _runner = runner;
    }

    // Drive the asynchronous demo workflow for the sample.
    public Task<int> RunAsync(IAsyncEnumerable<TInput> input) => _runner(input);
}

// Core implementation used by the demo and benchmark code below.
public static class PipelineFactory
{
    public static Pipeline<int, string> Create(int bufferSize, int stage1Parallelism, int stage2Parallelism)
    {
        return new Pipeline<int, string>(async input =>
        {
            // A bounded channel lets downstream slowdown push back on producers.
            Channel<int> first = Channel.CreateBounded<int>(bufferSize);
            // A bounded channel lets downstream slowdown push back on producers.
            Channel<int> second = Channel.CreateBounded<int>(bufferSize);
            // A bounded channel lets downstream slowdown push back on producers.
            Channel<string> output = Channel.CreateBounded<string>(bufferSize);

            Task producer = Task.Run(async () =>
            {
                await foreach (int item in input)
                {
                    await first.Writer.WriteAsync(item);
                }

                first.Writer.TryComplete();
            });

            Task[] stage1Workers = Enumerable.Range(0, stage1Parallelism).Select(_ => Task.Run(async () =>
            {
                await foreach (int item in first.Reader.ReadAllAsync())
                {
                    await second.Writer.WriteAsync(item * item);
                }
            })).ToArray();

            Task stage1Completion = Task.WhenAll(stage1Workers).ContinueWith(_ => second.Writer.TryComplete());

            Task[] stage2Workers = Enumerable.Range(0, stage2Parallelism).Select(_ => Task.Run(async () =>
            {
                await foreach (int item in second.Reader.ReadAllAsync())
                {
                    await output.Writer.WriteAsync($"value={item}");
                }
            })).ToArray();

            Task stage2Completion = Task.WhenAll(stage2Workers).ContinueWith(_ => output.Writer.TryComplete());

            int consumed = 0;
            await foreach (string _ in output.Reader.ReadAllAsync())
            {
                consumed++;
            }

            await producer;
            await stage1Completion;
            await stage2Completion;
            return consumed;
        });
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class PipelineBenchmarks
{
    [Params(64, 512)]
    public int BufferSize;

    [Params(2, 4)]
    public int Parallelism;

    [Benchmark]
    public async Task<int> RunPipeline()
    {
        Pipeline<int, string> pipeline = PipelineFactory.Create(BufferSize, Parallelism, Parallelism);
        return await pipeline.RunAsync(Generate());
    }

    private async IAsyncEnumerable<int> Generate()
    {
        for (int i = 0; i < 5_000; i++)
        {
            if ((i & 127) == 0)
            {
                await Task.Yield();
            }

            yield return i;
        }
    }
}
