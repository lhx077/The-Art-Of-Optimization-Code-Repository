// ============================================================================
// Exercise 02 — Intrinsic Memory Comparison
// ============================================================================
// Implements byte-sequence equality with ISA-specific fast paths. The algorithm
// short-circuits on the first mismatch and falls back to scalar comparison when
// vector support is unavailable or the input is too small.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<IntrinsicMemoryCompareBenchmarks>();
    return;
}

byte[] left = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
byte[] right = left.ToArray();
right[31] ^= 1;

Console.WriteLine("=== Intrinsic Memory Comparison Demo ===");
Console.WriteLine($"Equal arrays: {IntrinsicMemoryComparer.SequenceEqual(left, left)}");
Console.WriteLine($"Different arrays: {IntrinsicMemoryComparer.SequenceEqual(left, right)}");
// Use the widest hardware path first and fall back to narrower or scalar logic when needed.
Console.WriteLine($"Avx2 supported: {Avx2.IsSupported}");
// Keep an SSE fallback so the sample still benefits on machines without AVX2.
Console.WriteLine($"Sse2 supported: {Sse2.IsSupported}");

// Core implementation used by the demo and benchmark code below.
public static class IntrinsicMemoryComparer
{
    public static bool SequenceEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        // Use the widest hardware path first and fall back to narrower or scalar logic when needed.
        if (Avx2.IsSupported && left.Length >= Vector256<byte>.Count)
        {
            return SequenceEqualAvx2(left, right);
        }

        // Keep an SSE fallback so the sample still benefits on machines without AVX2.
        if (Sse2.IsSupported && left.Length >= Vector128<byte>.Count)
        {
            return SequenceEqualSse2(left, right);
        }

        return SequenceEqualScalar(left, right);
    }

    private static bool SequenceEqualScalar(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            // The scalar path preserves the same early-exit behavior without requiring SIMD support.
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqualSse2(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte leftRef = ref MemoryMarshal.GetReference(left);
        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte rightRef = ref MemoryMarshal.GetReference(right);
        int i = 0;
        int lastVectorStart = left.Length - Vector128<byte>.Count;

        for (; i <= lastVectorStart; i += Vector128<byte>.Count)
        {
            // Load 16 bytes per step and compare all lanes in one SIMD instruction group.
            Vector128<byte> leftVector = Vector128.LoadUnsafe(ref Unsafe.Add(ref leftRef, i));
            Vector128<byte> rightVector = Vector128.LoadUnsafe(ref Unsafe.Add(ref rightRef, i));
            // Keep an SSE fallback so the sample still benefits on machines without AVX2.
            Vector128<byte> comparison = Sse2.CompareEqual(leftVector, rightVector);
            // MoveMask turns per-byte equality into a compact bitmask for the fast all-equal check.
            if (Sse2.MoveMask(comparison) != 0xFFFF)
            {
                return false;
            }
        }

        for (; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqualAvx2(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte leftRef = ref MemoryMarshal.GetReference(left);
        // Reinterpret the existing bytes directly instead of copying them into another buffer.
        ref byte rightRef = ref MemoryMarshal.GetReference(right);
        int i = 0;
        int lastVectorStart = left.Length - Vector256<byte>.Count;

        for (; i <= lastVectorStart; i += Vector256<byte>.Count)
        {
            // Load 32 bytes per step on AVX2-capable machines to maximize throughput.
            Vector256<byte> leftVector = Vector256.LoadUnsafe(ref Unsafe.Add(ref leftRef, i));
            Vector256<byte> rightVector = Vector256.LoadUnsafe(ref Unsafe.Add(ref rightRef, i));
            // Use the widest hardware path first and fall back to narrower or scalar logic when needed.
            Vector256<byte> comparison = Avx2.CompareEqual(leftVector, rightVector);
            // A full -1 mask means every compared lane matched.
            if (Avx2.MoveMask(comparison) != -1)
            {
                return false;
            }
        }

        for (; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class IntrinsicMemoryCompareBenchmarks
{
    private byte[] _baseline = null!;
    private byte[] _candidate = null!;

    [Params(32, 256, 4096)]
    public int Length;

    [Params(0, 1, 2)]
    public int DifferencePattern;

    [GlobalSetup]
    public void Setup()
    {
        _baseline = new byte[Length];
        _candidate = new byte[Length];

        for (int i = 0; i < Length; i++)
        {
            byte value = (byte)(i * 13 + 5);
            _baseline[i] = value;
            _candidate[i] = value;
        }

        if (DifferencePattern == 1)
        {
            _candidate[0] ^= 1;
        }
        else if (DifferencePattern == 2)
        {
            _candidate[^1] ^= 1;
        }
    }

    [Benchmark(Baseline = true)]
    public bool SequenceEqual()
    {
        return _baseline.AsSpan().SequenceEqual(_candidate);
    }

    [Benchmark]
    public bool IntrinsicComparer()
    {
        return IntrinsicMemoryComparer.SequenceEqual(_baseline, _candidate);
    }
}
