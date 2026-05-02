// ============================================================================
// Exercise 05 — Object Pool Optimization
// ============================================================================
// Uses object pools for StringBuilder and MemoryStream in a JSON-like serialization
// workload, then compares pooled and non-pooled execution with BenchmarkDotNet.
// ============================================================================

using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.ObjectPool;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ObjectPoolBenchmarks>();
    return;
}

Demo.Run();

public static class Demo
{
    public static void Run()
    {
        JsonPayloadSerializer serializer = new();
        string json = serializer.SerializePooled(new RequestModel(7, "demo", DateTime.UtcNow, new[] { "a", "b", "c" }));
        Console.WriteLine(json);
    }
}

public sealed class JsonPayloadSerializer
{
    private readonly ObjectPool<StringBuilder> _stringBuilderPool;
    private readonly ObjectPool<ReusableMemoryStream> _memoryStreamPool;

    public JsonPayloadSerializer()
    {
        DefaultObjectPoolProvider provider = new()
        {
            MaximumRetained = Environment.ProcessorCount * 2
        };

        _stringBuilderPool = provider.CreateStringBuilderPool();
        _memoryStreamPool = provider.Create(new ReusableMemoryStreamPolicy());
    }

    public string SerializeWithoutPooling(RequestModel model)
    {
        StringBuilder builder = new();
        using MemoryStream stream = new();
        WriteJson(builder, stream, model);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public string SerializePooled(RequestModel model)
    {
        StringBuilder builder = _stringBuilderPool.Get();
        ReusableMemoryStream stream = _memoryStreamPool.Get();
        try
        {
            WriteJson(builder, stream, model);
            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        finally
        {
            builder.Clear();
            stream.Reset();
            _stringBuilderPool.Return(builder);
            _memoryStreamPool.Return(stream);
        }
    }

    private static void WriteJson(StringBuilder builder, MemoryStream stream, RequestModel model)
    {
        builder.Append('{');
        builder.Append("\"id\":").Append(model.Id).Append(',');
        builder.Append("\"name\":\"").Append(model.Name).Append("\",");
        builder.Append("\"timestamp\":\"").Append(model.Timestamp.ToString("O")).Append("\",");
        builder.Append("\"tags\":[");

        for (int i = 0; i < model.Tags.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(model.Tags[i]).Append('"');
        }

        builder.Append("]}");

        string json = builder.ToString();
        byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(json));
        try
        {
            int written = Encoding.UTF8.GetBytes(json, rented);
            stream.Write(rented, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

public sealed class ReusableMemoryStream : MemoryStream
{
    public void Reset()
    {
        Position = 0;
        SetLength(0);
    }
}

public sealed class ReusableMemoryStreamPolicy : PooledObjectPolicy<ReusableMemoryStream>
{
    public override ReusableMemoryStream Create() => new();

    public override bool Return(ReusableMemoryStream obj)
    {
        obj.Reset();
        return true;
    }
}

public readonly record struct RequestModel(int Id, string Name, DateTime Timestamp, string[] Tags);

[MemoryDiagnoser]
public class ObjectPoolBenchmarks
{
    private readonly JsonPayloadSerializer _serializer = new();
    private RequestModel _model;

    public ObjectPoolBenchmarks()
    {
        _model = new RequestModel(42, "load-test", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), new[] { "alpha", "beta", "gamma", "delta" });
    }

    [Benchmark(Baseline = true)]
    public string WithoutPooling() => _serializer.SerializeWithoutPooling(_model);

    [Benchmark]
    public string WithPooling() => _serializer.SerializePooled(_model);
}
