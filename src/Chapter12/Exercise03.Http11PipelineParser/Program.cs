// ============================================================================
// Exercise 03 — Simplified HTTP/1.1 Parser with Pipelines
// ============================================================================
// Parses an HTTP/1.1 request line, headers, and a Content-Length body using
// PipeReader + SequenceReader<byte>. Includes a simple Stream-based baseline and
// BenchmarkDotNet comparison.
// ============================================================================

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<HttpParserBenchmarks>();
    return;
}

await RunDemoAsync();

static async Task RunDemoAsync()
{
    Console.WriteLine("=== HTTP/1.1 Pipeline Parser Demo ===");

    string requestText =
        "POST /submit HTTP/1.1\r\n" +
        "Host: example.com\r\n" +
        "Content-Length: 11\r\n" +
        "X-Trace: abc123\r\n\r\n" +
        "hello world";

    var pipe = new Pipe();
    byte[] bytes = Encoding.ASCII.GetBytes(requestText);
    // Feed the request in uneven chunks to mimic segmented network input.
    await pipe.Writer.WriteAsync(bytes.AsMemory(..17));
    await pipe.Writer.WriteAsync(bytes.AsMemory(17..43));
    await pipe.Writer.WriteAsync(bytes.AsMemory(43..));
    await pipe.Writer.CompleteAsync();

    HttpRequestData request = await HttpPipelineParser.ReadSingleAsync(pipe.Reader);
    Console.WriteLine($"Method={request.Method}, Path={request.Path}, Version={request.Version}");
    foreach (var header in request.Headers)
    {
        Console.WriteLine($"Header {header.Key}: {header.Value}");
    }
    Console.WriteLine($"Body={Encoding.UTF8.GetString(request.Body)}");
}

// Core implementation used by the demo and benchmark code below.
public sealed class HttpRequestData
{
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
}

// Core implementation used by the demo and benchmark code below.
public static class HttpPipelineParser
{
    // Keep reading until one complete request is available or the input ends.
    public static async Task<HttpRequestData> ReadSingleAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                if (TryParseRequest(ref buffer, out HttpRequestData? request))
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    await reader.CompleteAsync();
                    return request!;
                }

                if (result.IsCompleted)
                {
                    throw new InvalidDataException("Incomplete HTTP request.");
                }

                reader.AdvanceTo(buffer.Start, result.Buffer.End);
            }
            catch
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                await reader.CompleteAsync();
                throw;
            }
        }
    }

    // Attempt to parse a complete request and stop cleanly when more data is needed.
    public static bool TryParseRequest(ref ReadOnlySequence<byte> buffer, out HttpRequestData? request)
    {
        request = null;
        // SequenceReader helps the parser move across segmented buffers without flattening them.
        var reader = new SequenceReader<byte>(buffer);

        if (!TryReadLine(ref reader, out string? requestLine))
        {
            return false;
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new InvalidDataException("Invalid request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (!TryReadLine(ref reader, out string? line))
            {
                return false;
            }

            // The blank line marks the boundary between headers and body.
            if (line.Length == 0)
            {
                break;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Invalid header line.");
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            headers[name] = value;
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? rawLength) &&
            !int.TryParse(rawLength, out contentLength))
        {
            throw new InvalidDataException("Invalid Content-Length header.");
        }

        if (reader.Remaining < contentLength)
        {
            return false;
        }

        byte[] body = reader.Sequence.Slice(reader.Position, contentLength).ToArray();
        // Advance past the body so the caller's remaining buffer starts at the next request.
        reader.Advance(contentLength);
        buffer = buffer.Slice(reader.Position);

        request = new HttpRequestData
        {
            Method = parts[0],
            Path = parts[1],
            Version = parts[2],
            Headers = headers,
            Body = body
        };

        return true;
    }

    // Split the next logical line without allocating intermediate strings.
    private static bool TryReadLine(ref SequenceReader<byte> reader, out string line)
    {
        line = string.Empty;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> sequence, (byte)'\n'))
        {
            return false;
        }

        ReadOnlySpan<byte> span = sequence.IsSingleSegment ? sequence.FirstSpan : sequence.ToArray();
        if (!span.IsEmpty && span[^1] == (byte)'\r')
        {
            // Drop the CR from CRLF so downstream parsing sees the logical line content only.
            span = span[..^1];
        }

        line = Encoding.ASCII.GetString(span);
        return true;
    }
}

// Core implementation used by the demo and benchmark code below.
public static class HttpStreamParser
{
    // Parse incrementally so the sample can demonstrate the intended hot path clearly.
    public static HttpRequestData Parse(byte[] requestBytes)
    {
        using var stream = new MemoryStream(requestBytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        string? requestLine = reader.ReadLine();
        if (requestLine is null)
        {
            throw new InvalidDataException("Missing request line.");
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                throw new InvalidDataException("Unexpected EOF while reading headers.");
            }

            if (line.Length == 0)
            {
                break;
            }

            int separator = line.IndexOf(':');
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        int contentLength = headers.TryGetValue("Content-Length", out string? rawLength)
            ? int.Parse(rawLength)
            : 0;

        char[] bodyChars = new char[contentLength];
        int read = reader.ReadBlock(bodyChars, 0, contentLength);
        byte[] body = Encoding.UTF8.GetBytes(bodyChars.AsSpan(0, read).ToArray());

        return new HttpRequestData
        {
            Method = parts[0],
            Path = parts[1],
            Version = parts[2],
            Headers = headers,
            Body = body
        };
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class HttpParserBenchmarks
{
    private byte[] _requestBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        string body = new string('x', 1024);
        string request =
            "POST /api/items HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "User-Agent: Bench\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "X-Correlation-Id: 42\r\n\r\n" +
            body;
        _requestBytes = Encoding.ASCII.GetBytes(request);
    }

    [Benchmark(Baseline = true)]
    public HttpRequestData StreamParser()
    {
        return HttpStreamParser.Parse(_requestBytes);
    }

    [Benchmark]
    public async Task<HttpRequestData> PipelineParser()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(_requestBytes);
        await pipe.Writer.CompleteAsync();
        return await HttpPipelineParser.ReadSingleAsync(pipe.Reader);
    }
}
