// ============================================================================
// Exercise 04 — Backpressure-Aware Processing Pipeline
// ============================================================================
// Builds a three-stage pipeline: read -> transform -> write. Each stage uses
// its own Pipe. The final writer is intentionally slowed down so logs make the
// propagation of backpressure visible.
// ============================================================================

using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;

await BackpressureDemo.RunAsync();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class BackpressureDemo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Backpressure Pipeline Demo ===");

        var stage1 = new Pipe(new PipeOptions(pauseWriterThreshold: 8 * 1024, resumeWriterThreshold: 4 * 1024));
        var stage2 = new Pipe(new PipeOptions(pauseWriterThreshold: 8 * 1024, resumeWriterThreshold: 4 * 1024));

        using var cts = new CancellationTokenSource();

        Task producer = ProduceAsync(stage1.Writer, cts.Token);
        Task transformer = TransformAsync(stage1.Reader, stage2.Writer, cts.Token);
        Task consumer = ConsumeSlowlyAsync(stage2.Reader, cts.Token);

        await Task.WhenAll(producer, transformer, consumer);
        Console.WriteLine("Pipeline completed.");
    }

    private static async Task ProduceAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytes = 0;

        for (int i = 0; i < 24; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"record-{i:D2}-" + new string('x', 2048));
            await writer.WriteAsync(payload, cancellationToken);
            totalBytes += payload.Length;
            // Once downstream slows down, this producer eventually feels the pipe thresholds too.
            Console.WriteLine($"[READ ] t={stopwatch.ElapsedMilliseconds,4}ms produced={totalBytes,6} bytes");
        }

        await writer.CompleteAsync();
    }

    private static async Task TransformAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytes = 0;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    byte[] transformed = Transform(segment.Span);
                    await writer.WriteAsync(transformed, cancellationToken);
                    totalBytes += transformed.Length;
                    // Forwarding can also stall here because stage2 has its own pressure limits.
                    Console.WriteLine($"[XFORM] t={stopwatch.ElapsedMilliseconds,4}ms forwarded={totalBytes,6} bytes");
                }

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
            catch
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                throw;
            }
        }

        await reader.CompleteAsync();
        await writer.CompleteAsync();
    }

    private static async Task ConsumeSlowlyAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytes = 0;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    totalBytes += segment.Length;
                    Console.WriteLine($"[WRITE] t={stopwatch.ElapsedMilliseconds,4}ms consumed={totalBytes,6} bytes");
                    // The artificial delay is what makes backpressure propagate up the pipeline.
                    await Task.Delay(80, cancellationToken);
                }

                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
            catch
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                throw;
            }
        }

        await reader.CompleteAsync();
    }

    private static byte[] Transform(ReadOnlySpan<byte> input)
    {
        byte[] hash = SHA256.HashData(input);
        byte[] output = new byte[input.Length + hash.Length];
        input.CopyTo(output);
        hash.CopyTo(output.AsSpan(input.Length));
        return output;
    }
}
