// ============================================================================
// Exercise 03 — AoS versus SoA Physics Update
// ============================================================================
// Simulates one million 2D bodies with two layouts: an array of structs and a
// structure of arrays. The SoA path also includes a Vector<float>-based update.
// ============================================================================

using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<AosVsSoaBenchmarks>();
    return;
}

Console.WriteLine("Run with --benchmark to compare AoS and SoA particle updates.");

public struct Body2D
{
    public float PositionX;
    public float PositionY;
    public float VelocityX;
    public float VelocityY;
}

// Core implementation used by the demo and benchmark code below.
public sealed class BodySoA
{
    public float[] PositionX = null!;
    public float[] PositionY = null!;
    public float[] VelocityX = null!;
    public float[] VelocityY = null!;
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class AosVsSoaBenchmarks
{
    private Body2D[] _aos = null!;
    private BodySoA _soa = null!;

    [Params(1_000_000)]
    public int Count;

    [Params(1f / 60f)]
    public float DeltaTime;

    [GlobalSetup]
    public void Setup()
    {
        _aos = new Body2D[Count];
        _soa = new BodySoA
        {
            PositionX = new float[Count],
            PositionY = new float[Count],
            VelocityX = new float[Count],
            VelocityY = new float[Count]
        };

        for (int i = 0; i < Count; i++)
        {
            float px = i * 0.01f;
            float py = i * 0.02f;
            float vx = 1f + (i % 17) * 0.1f;
            float vy = -0.5f + (i % 13) * 0.05f;

            _aos[i] = new Body2D { PositionX = px, PositionY = py, VelocityX = vx, VelocityY = vy };
            _soa.PositionX[i] = px;
            _soa.PositionY[i] = py;
            _soa.VelocityX[i] = vx;
            _soa.VelocityY[i] = vy;
        }
    }

    [Benchmark(Baseline = true)]
    public float AosUpdate()
    {
        float checksum = 0;
        for (int i = 0; i < _aos.Length; i++)
        {
            _aos[i].PositionX += _aos[i].VelocityX * DeltaTime;
            _aos[i].PositionY += _aos[i].VelocityY * DeltaTime;
            checksum += _aos[i].PositionX;
        }

        return checksum;
    }

    [Benchmark]
    public float SoaScalarUpdate()
    {
        float[] px = _soa.PositionX;
        float[] py = _soa.PositionY;
        float[] vx = _soa.VelocityX;
        float[] vy = _soa.VelocityY;

        float checksum = 0;
        for (int i = 0; i < px.Length; i++)
        {
            px[i] += vx[i] * DeltaTime;
            py[i] += vy[i] * DeltaTime;
            checksum += px[i];
        }

        return checksum;
    }

    [Benchmark]
    public float SoaSimdUpdate()
    {
        Span<float> px = _soa.PositionX;
        Span<float> py = _soa.PositionY;
        ReadOnlySpan<float> vx = _soa.VelocityX;
        ReadOnlySpan<float> vy = _soa.VelocityY;

        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<float> dt = new(DeltaTime);
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int width = Vector<float>.Count;
        int i = 0;
        int lastVectorStart = px.Length - width;

        for (; i <= lastVectorStart; i += width)
        {
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            (new Vector<float>(px.Slice(i, width)) + (new Vector<float>(vx.Slice(i, width)) * dt)).CopyTo(px.Slice(i, width));
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            (new Vector<float>(py.Slice(i, width)) + (new Vector<float>(vy.Slice(i, width)) * dt)).CopyTo(py.Slice(i, width));
        }

        for (; i < px.Length; i++)
        {
            px[i] += vx[i] * DeltaTime;
            py[i] += vy[i] * DeltaTime;
        }

        float checksum = 0;
        for (i = 0; i < px.Length; i++)
        {
            checksum += px[i];
        }

        return checksum;
    }
}
