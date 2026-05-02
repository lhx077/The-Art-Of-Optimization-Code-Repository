// ============================================================================
// Exercise 01 — High-Performance Memory Comparison
// ============================================================================
// Compares two ReadOnlySpan<byte> values using batched 8-byte reads via
// Unsafe.ReadUnaligned<ulong>. The remaining tail bytes are handled separately.
// BenchmarkDotNet compares this implementation with SequenceEqual.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<MemoryComparisonBenchmarks>();
    return;
}

byte[] left = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();
byte[] right = left.ToArray();
right[^1] ^= 1;

Console.WriteLine("=== Memory Comparison Demo ===");
Console.WriteLine($"Equal arrays: {FastMemoryComparer.Equals(left, left)}");
Console.WriteLine($"Different arrays: {FastMemoryComparer.Equals(left, right)}");

// Core implementation used by the demo and benchmark code below.
public static class FastMemoryComparer
{
    public static bool Equals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte leftRef = ref MemoryMarshal.GetReference(left);
        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte rightRef = ref MemoryMarshal.GetReference(right);
        int offset = 0;

        while (offset <= left.Length - sizeof(ulong))
        {
            // Compare eight bytes at a time before falling back to the scalar tail loop.
            ulong leftChunk = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftRef, offset));
            ulong rightChunk = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightRef, offset));
            if (leftChunk != rightChunk)
            {
                return false;
            }

            offset += sizeof(ulong);
        }

        while (offset < left.Length)
        {
            // Handle the trailing bytes that do not fill a whole ulong chunk.
            if (Unsafe.Add(ref leftRef, offset) != Unsafe.Add(ref rightRef, offset))
            {
                return false;
            }

            offset++;
        }

        return true;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class MemoryComparisonBenchmarks
{
    private byte[] _left = null!;
    private byte[] _right = null!;

    [Params(31, 256, 4099)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        _left = new byte[Length];
        _right = new byte[Length];

        for (int i = 0; i < Length; i++)
        {
            byte value = (byte)(i * 31 + 7);
            _left[i] = value;
            _right[i] = value;
        }
    }

    [Benchmark(Baseline = true)]
    public bool SequenceEqual()
    {
        return _left.AsSpan().SequenceEqual(_right);
    }

    [Benchmark]
    public bool BatchedUnsafeCompare()
    {
        return FastMemoryComparer.Equals(_left, _right);
    }
}
