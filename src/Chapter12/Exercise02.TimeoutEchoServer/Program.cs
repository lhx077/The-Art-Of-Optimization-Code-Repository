// ============================================================================
// Exercise 02 — Timeout-Aware Echo Server
// ============================================================================
// Demonstrates a length-prefixed echo server loop that enforces a complete-
// message read timeout. If the client fails to finish a message in time, the
// server closes the session and releases resources safely.
// ============================================================================

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

await Demo.RunAsync();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Timeout Echo Server Demo ===");

        await RunSuccessfulClientAsync();
        await RunTimedOutClientAsync();
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    private static async Task RunSuccessfulClientAsync()
    {
        var transport = new DuplexPipe();
        var server = new TimeoutEchoSession(transport.ServerReader, transport.ServerWriter, TimeSpan.FromSeconds(2));
        Task serverTask = server.RunAsync();

        byte[] frame = EchoFraming.Encode("hello");
        await transport.ClientWriter.WriteAsync(frame);
        await transport.ClientWriter.CompleteAsync();

        List<string> echoed = await EchoFraming.ReadAllAsync(transport.ClientReader);
        await serverTask;

        Console.WriteLine($"Successful echo: {string.Join(',', echoed)}");
    }

    // Drive the shared workload used by the demo or benchmark entry point.
    private static async Task RunTimedOutClientAsync()
    {
        var transport = new DuplexPipe();
        var server = new TimeoutEchoSession(transport.ServerReader, transport.ServerWriter, TimeSpan.FromMilliseconds(200));
        Task serverTask = server.RunAsync();

        byte[] frame = EchoFraming.Encode("this-will-time-out");
        // Send only part of the frame, then pause long enough to trigger the read timeout.
        await transport.ClientWriter.WriteAsync(frame.AsMemory(..3));
        await Task.Delay(350);
        await transport.ClientWriter.CompleteAsync();

        await serverTask;
        Console.WriteLine($"Timed out: {server.TimedOut}, ResourcesCleaned={server.ResourcesCleaned}");
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class TimeoutEchoSession
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly TimeSpan _messageTimeout;

    public bool TimedOut { get; private set; }
    public bool ResourcesCleaned { get; private set; }

    public TimeoutEchoSession(PipeReader reader, PipeWriter writer, TimeSpan messageTimeout)
    {
        _reader = reader;
        _writer = writer;
        _messageTimeout = messageTimeout;
    }

    // Drive the asynchronous demo workflow for the sample.
    public async Task RunAsync()
    {
        try
        {
            while (true)
            {
                string? message = await ReadOneMessageWithTimeoutAsync();
                if (message is null)
                {
                    break;
                }

                byte[] response = EchoFraming.Encode($"echo:{message}");
                await _writer.WriteAsync(response);
                FlushResult flush = await _writer.FlushAsync();
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            TimedOut = true;
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<string?> ReadOneMessageWithTimeoutAsync()
    {
        while (true)
        {
            using var cts = new CancellationTokenSource();
            // Each read gets its own timeout window so slow or stalled clients are cut off promptly.
            cts.CancelAfter(_messageTimeout);

            ReadResult result = await _reader.ReadAsync(cts.Token);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                if (EchoFraming.TryReadMessage(ref buffer, out string? message))
                {
                    _reader.AdvanceTo(buffer.Start, buffer.Start);
                    return message;
                }

                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                    {
                        throw new InvalidDataException("Connection closed before a full message arrived.");
                    }

                    _reader.AdvanceTo(buffer.Start, buffer.Start);
                    return null;
                }

                _reader.AdvanceTo(buffer.Start, result.Buffer.End);
            }
            catch
            {
                _reader.AdvanceTo(buffer.Start, buffer.Start);
                throw;
            }
        }
    }

    private async Task CleanupAsync()
    {
        ResourcesCleaned = true;
        await _reader.CompleteAsync();
        await _writer.CompleteAsync();
    }
}

// Core implementation used by the demo and benchmark code below.
public static class EchoFraming
{
    public static byte[] Encode(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    // Return quickly on the hot path and let the caller decide how to handle misses.
    public static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out string? message)
    {
        message = null;
        if (buffer.Length < 4)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[4];
        // Normalize the prefix into a small contiguous buffer before decoding the message length.
        buffer.Slice(0, 4).CopyTo(header);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || buffer.Length < 4 + length)
        {
            return false;
        }

        message = Encoding.UTF8.GetString(buffer.Slice(4, length).ToArray());
        buffer = buffer.Slice(4 + length);
        return true;
    }

    public static async Task<List<string>> ReadAllAsync(PipeReader reader)
    {
        var messages = new List<string>();

        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (TryReadMessage(ref buffer, out string? message))
                {
                    // Drain every echoed frame that is already available before asking for more data.
                    messages.Add(message!);
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, result.Buffer.End);
            }
        }

        await reader.CompleteAsync();
        return messages;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class DuplexPipe
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();

    public PipeReader ServerReader => _clientToServer.Reader;
    public PipeWriter ClientWriter => _clientToServer.Writer;
    public PipeWriter ServerWriter => _serverToClient.Writer;
    public PipeReader ClientReader => _serverToClient.Reader;
}
