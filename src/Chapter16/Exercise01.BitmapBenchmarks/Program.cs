// ============================================================================
// Exercise 01 — Bitmap Operations and Benchmarks
// ============================================================================
// Implements a compact bitmap with BitOperations-based population counting and
// vectorized set algebra over ulong storage. Benchmarks compare the custom
// implementation against BitArray for common bulk operations.
// ============================================================================

using System.Collections;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<BitmapBenchmarks>();
    return;
}

var left = DenseBitmap.FromIndices(256, [1, 3, 5, 64, 127, 190]);
var right = DenseBitmap.FromIndices(256, [3, 4, 5, 64, 200]);

Console.WriteLine("=== Bitmap Demo ===");
Console.WriteLine($"Left PopCount:  {left.PopCount()}");
Console.WriteLine($"Right PopCount: {right.PopCount()}");
Console.WriteLine($"Bit 64 in left: {left.Get(64)}");
Console.WriteLine($"Bit 65 in left: {left.Get(65)}");

DenseBitmap andResult = left.Clone();
andResult.And(right);
Console.WriteLine($"AND PopCount:   {andResult.PopCount()}");

DenseBitmap xorResult = left.Clone();
xorResult.Xor(right);
Console.WriteLine($"XOR PopCount:   {xorResult.PopCount()}");

// Core implementation used by the demo and benchmark code below.
public sealed class DenseBitmap
{
    private readonly ulong[] _words;

    public DenseBitmap(int bitCount)
    {
        if (bitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        BitCount = bitCount;
        _words = new ulong[(bitCount + 63) >> 6];
    }

    private DenseBitmap(int bitCount, ulong[] words)
    {
        BitCount = bitCount;
        _words = words;
    }

    public int BitCount { get; }

    public static DenseBitmap FromIndices(int bitCount, IEnumerable<int> indices)
    {
        DenseBitmap bitmap = new(bitCount);
        foreach (int index in indices)
        {
            // Seed a sparse bitmap quickly from a list of interesting set-bit positions.
            bitmap.Set(index);
        }

        return bitmap;
    }

    public DenseBitmap Clone()
    {
        return new DenseBitmap(BitCount, (ulong[])_words.Clone());
    }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(int index)
    {
        ValidateIndex(index);
        _words[index >> 6] |= 1UL << (index & 63);
    }

    public void Clear(int index)
    {
        ValidateIndex(index);
        _words[index >> 6] &= ~(1UL << (index & 63));
    }

    // Serve reads from the current state without unnecessary extra work.
    public bool Get(int index)
    {
        ValidateIndex(index);
        return ((_words[index >> 6] >> (index & 63)) & 1UL) != 0;
    }

    public int PopCount()
    {
        int total = 0;
        foreach (ulong word in _words)
        {
            total += BitOperations.PopCount(word);
        }

        return total;
    }

    public void And(DenseBitmap other)
    {
        ApplyAnd(other);
    }

    public void Or(DenseBitmap other)
    {
        ApplyOr(other);
    }

    public void Xor(DenseBitmap other)
    {
        ApplyXor(other);
    }

    public BitArray ToBitArray()
    {
        BitArray bits = new(BitCount);
        for (int i = 0; i < BitCount; i++)
        {
            bits[i] = Get(i);
        }

        return bits;
    }

    private void ApplyAnd(DenseBitmap other)
    {
        ValidateCompatible(other);

        Span<ulong> left = _words;
        ReadOnlySpan<ulong> right = other._words;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int vectorWidth = Vector<ulong>.Count;
        int i = 0;
        int lastVectorStart = left.Length - vectorWidth;

        for (; i <= lastVectorStart; i += vectorWidth)
        {
            // Apply the bulk AND over several machine words at once.
            Vector<ulong> result = Vector.BitwiseAnd(
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(left.Slice(i, vectorWidth)),
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(right.Slice(i, vectorWidth)));
            result.CopyTo(left.Slice(i, vectorWidth));
        }

        for (; i < left.Length; i++)
        {
            // Clean up any remaining words after the vector loop.
            left[i] &= right[i];
        }

        MaskUnusedBits();
    }

    private void ApplyOr(DenseBitmap other)
    {
        ValidateCompatible(other);

        Span<ulong> left = _words;
        ReadOnlySpan<ulong> right = other._words;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int vectorWidth = Vector<ulong>.Count;
        int i = 0;
        int lastVectorStart = left.Length - vectorWidth;

        for (; i <= lastVectorStart; i += vectorWidth)
        {
            // Apply the bulk OR over several machine words at once.
            Vector<ulong> result = Vector.BitwiseOr(
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(left.Slice(i, vectorWidth)),
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(right.Slice(i, vectorWidth)));
            result.CopyTo(left.Slice(i, vectorWidth));
        }

        for (; i < left.Length; i++)
        {
            // Clean up any remaining words after the vector loop.
            left[i] |= right[i];
        }

        MaskUnusedBits();
    }

    private void ApplyXor(DenseBitmap other)
    {
        ValidateCompatible(other);

        Span<ulong> left = _words;
        ReadOnlySpan<ulong> right = other._words;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int vectorWidth = Vector<ulong>.Count;
        int i = 0;
        int lastVectorStart = left.Length - vectorWidth;

        for (; i <= lastVectorStart; i += vectorWidth)
        {
            // Apply the bulk XOR over several machine words at once.
            Vector<ulong> result = Vector.Xor(
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(left.Slice(i, vectorWidth)),
                // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
                new Vector<ulong>(right.Slice(i, vectorWidth)));
            result.CopyTo(left.Slice(i, vectorWidth));
        }

        for (; i < left.Length; i++)
        {
            // Clean up any remaining words after the vector loop.
            left[i] ^= right[i];
        }

        MaskUnusedBits();
    }

    private void ValidateCompatible(DenseBitmap other)
    {
        if (other.BitCount != BitCount)
        {
            throw new ArgumentException("Bitmap sizes must match.", nameof(other));
        }
    }

    private void MaskUnusedBits()
    {
        int remainder = BitCount & 63;
        if (remainder == 0)
        {
            return;
        }

        ulong mask = (1UL << remainder) - 1;
        _words[^1] &= mask;
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)BitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class BitmapBenchmarks
{
    private DenseBitmap _customLeft = null!;
    private DenseBitmap _customRight = null!;
    private BitArray _bitArrayLeft = null!;
    private BitArray _bitArrayRight = null!;

    [Params(1_024, 65_536, 1_048_576)]
    public int BitCount;

    [GlobalSetup]
    public void Setup()
    {
        _customLeft = new DenseBitmap(BitCount);
        _customRight = new DenseBitmap(BitCount);

        for (int i = 0; i < BitCount; i++)
        {
            if ((i % 3) == 0)
            {
                _customLeft.Set(i);
            }

            if ((i % 5) <= 1)
            {
                _customRight.Set(i);
            }
        }

        _bitArrayLeft = _customLeft.ToBitArray();
        _bitArrayRight = _customRight.ToBitArray();
    }

    [Benchmark(Baseline = true)]
    public int CustomPopCount()
    {
        return _customLeft.PopCount();
    }

    [Benchmark]
    public int BitArrayPopCount()
    {
        int total = 0;
        for (int i = 0; i < _bitArrayLeft.Length; i++)
        {
            total += _bitArrayLeft[i] ? 1 : 0;
        }

        return total;
    }

    [Benchmark]
    public int CustomAndThenPopCount()
    {
        DenseBitmap copy = _customLeft.Clone();
        copy.And(_customRight);
        return copy.PopCount();
    }

    [Benchmark]
    public int BitArrayAndThenPopCount()
    {
        BitArray copy = new(_bitArrayLeft);
        copy.And(_bitArrayRight);

        int total = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            total += copy[i] ? 1 : 0;
        }

        return total;
    }
}
