// ============================================================================
// Exercise 01 — Length-Prefixed Message Protocol Parser
// ============================================================================
// Parses messages framed as a 4-byte big-endian length prefix followed by a
// UTF-8 payload. Uses PipeReader and correctly handles fragmentation across
// multiple reads, including partial length headers.
// ============================================================================

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

await RunDemoAsync();
await RunTestsAsync();

static async Task RunDemoAsync()
{
    Console.WriteLine("=== Length-Prefixed Protocol Demo ===");

    var pipe = new Pipe();
    byte[] hello = FrameProtocol.Encode("hello pipelines");
    byte[] world = FrameProtocol.Encode("world");

    // Split the first frame across writes so the parser has to survive fragmentation.
    await pipe.Writer.WriteAsync(hello.AsMemory(..5));
    await pipe.Writer.WriteAsync(hello.AsMemory(5..));
    await pipe.Writer.WriteAsync(world);
    await pipe.Writer.CompleteAsync();

    var messages = await FrameProtocol.ReadAllMessagesAsync(pipe.Reader);
    foreach (string message in messages)
    {
        Console.WriteLine($"Parsed: {message}");
    }
}

static async Task RunTestsAsync()
{
    Console.WriteLine();
    Console.WriteLine("=== Parser Boundary Tests ===");

    await ParserTests.HeaderSplitAcrossReadsAsync();
    await ParserTests.BodySplitAcrossReadsAsync();
    await ParserTests.MultipleMessagesAcrossIrregularChunksAsync();
    await ParserTests.ZeroLengthMessageAsync();
    await ParserTests.InvalidLengthRejectedAsync();

    Console.WriteLine("All parser tests passed.");
}

// Core implementation used by the demo and benchmark code below.
public static class FrameProtocol
{
    private const int HeaderLength = 4;
    private const int MaxPayloadLength = 64 * 1024;

    public static byte[] Encode(string message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        byte[] frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderLength), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static async Task<List<string>> ReadAllMessagesAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (TryReadMessage(ref buffer, out string? message))
                {
                    // TryReadMessage advances the buffer so the loop naturally drains all complete frames.
                    messages.Add(message!);
                }

                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                    {
                        throw new InvalidDataException("Stream ended with an incomplete frame.");
                    }

                    break;
                }
            }
            finally
            {
                SequencePosition consumed = buffer.Start;
                SequencePosition examined = result.IsCompleted ? buffer.Start : result.Buffer.End;
                reader.AdvanceTo(consumed, examined);
            }
        }

        await reader.CompleteAsync();
        return messages;
    }

    // Return quickly on the hot path and let the caller decide how to handle misses.
    public static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out string? message)
    {
        message = null;

        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        Span<byte> headerBytes = stackalloc byte[HeaderLength];
        // Copy the fixed-size header into contiguous stack storage before decoding it.
        buffer.Slice(0, HeaderLength).CopyTo(headerBytes);
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(headerBytes);

        if (payloadLength < 0 || payloadLength > MaxPayloadLength)
        {
            throw new InvalidDataException($"Invalid payload length: {payloadLength}.");
        }

        long frameLength = HeaderLength + payloadLength;
        if (buffer.Length < frameLength)
        {
            // The header is complete, but the payload is still fragmented across future reads.
            return false;
        }

        ReadOnlySequence<byte> payloadSequence = buffer.Slice(HeaderLength, payloadLength);
        byte[] payload = payloadSequence.ToArray();
        message = Encoding.UTF8.GetString(payload);
        buffer = buffer.Slice(frameLength);
        return true;
    }
}

// Core implementation used by the demo and benchmark code below.
public static class ParserTests
{
    public static async Task HeaderSplitAcrossReadsAsync()
    {
        string message = await ParseSingleMessageAsync(FrameProtocol.Encode("header-split"), [1, 1, 2, 3, 8]);
        AssertEqual("header-split", message, nameof(HeaderSplitAcrossReadsAsync));
    }

    public static async Task BodySplitAcrossReadsAsync()
    {
        string message = await ParseSingleMessageAsync(FrameProtocol.Encode("body-split-message"), [4, 2, 2, 2, 2, 64]);
        AssertEqual("body-split-message", message, nameof(BodySplitAcrossReadsAsync));
    }

    public static async Task MultipleMessagesAcrossIrregularChunksAsync()
    {
        byte[] first = FrameProtocol.Encode("alpha");
        byte[] second = FrameProtocol.Encode("beta");
        byte[] third = FrameProtocol.Encode("gamma");
        byte[] combined = [.. first, .. second, .. third];

        var pipe = new Pipe();
        await WriteChunksAsync(pipe.Writer, combined, [2, 3, 1, 7, 2, 5, 64]);
        await pipe.Writer.CompleteAsync();

        List<string> messages = await FrameProtocol.ReadAllMessagesAsync(pipe.Reader);
        string joined = string.Join(',', messages);
        AssertEqual("alpha,beta,gamma", joined, nameof(MultipleMessagesAcrossIrregularChunksAsync));
    }

    public static async Task ZeroLengthMessageAsync()
    {
        string message = await ParseSingleMessageAsync(FrameProtocol.Encode(string.Empty), [2, 2]);
        AssertEqual(string.Empty, message, nameof(ZeroLengthMessageAsync));
    }

    public static async Task InvalidLengthRejectedAsync()
    {
        var pipe = new Pipe();
        byte[] invalid = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(invalid, 70_000);
        await pipe.Writer.WriteAsync(invalid);
        await pipe.Writer.CompleteAsync();

        try
        {
            await FrameProtocol.ReadAllMessagesAsync(pipe.Reader);
            throw new Exception("Expected invalid length to throw.");
        }
        catch (InvalidDataException)
        {
        }
    }

    // Parse incrementally so the sample can demonstrate the intended hot path clearly.
    private static async Task<string> ParseSingleMessageAsync(byte[] frame, int[] chunkSizes)
    {
        var pipe = new Pipe();
        await WriteChunksAsync(pipe.Writer, frame, chunkSizes);
        await pipe.Writer.CompleteAsync();

        List<string> messages = await FrameProtocol.ReadAllMessagesAsync(pipe.Reader);
        if (messages.Count != 1)
        {
            throw new Exception($"Expected exactly one message but got {messages.Count}.");
        }

        return messages[0];
    }

    private static async Task WriteChunksAsync(PipeWriter writer, byte[] data, int[] chunkSizes)
    {
        int offset = 0;
        foreach (int requestedSize in chunkSizes)
        {
            if (offset >= data.Length)
            {
                break;
            }

            // Clamp each requested chunk so the helper can simulate awkward network boundaries safely.
            int size = Math.Min(requestedSize, data.Length - offset);
            await writer.WriteAsync(data.AsMemory(offset, size));
            offset += size;
        }

        if (offset < data.Length)
        {
            await writer.WriteAsync(data.AsMemory(offset));
        }
    }

    private static void AssertEqual(string expected, string actual, string testName)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new Exception($"{testName} failed. Expected '{expected}', got '{actual}'.");
        }
    }
}
