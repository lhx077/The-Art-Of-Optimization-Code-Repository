// ============================================================================
// Exercise 03 — SIMD ASCII Uppercase Conversion
// ============================================================================
// Converts ASCII lowercase letters to uppercase with Vector<byte>-based mask
// operations. Non-letter characters remain unchanged, and a scalar tail handles
// the remainder when the input length is not a multiple of the SIMD width.
// ============================================================================

using System.Numerics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<SimdAsciiUppercaseBenchmarks>();
    return;
}

string sample = "simd makes ascii FAST! 123 xyz";
Console.WriteLine("=== SIMD ASCII Uppercase Demo ===");
Console.WriteLine($"Original: {sample}");
Console.WriteLine($"SIMD:     {AsciiUppercaser.ToUpperAsciiSimd(sample)}");
Console.WriteLine($"Built-in: {sample.ToUpperInvariant()}");

// Core implementation used by the demo and benchmark code below.
public static class AsciiUppercaser
{
    public static string ToUpperAsciiSimd(string input)
    {
        byte[] source = Encoding.ASCII.GetBytes(input);
        byte[] destination = new byte[source.Length];
        // Convert into a reusable byte buffer, then decode once at the end.
        Convert(source, destination);
        return Encoding.ASCII.GetString(destination);
    }

    public static void Convert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is too small.");
        }

        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        int width = Vector<byte>.Count;
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<byte> lowercaseStartMinusOne = new((byte)('a' - 1));
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<byte> lowercaseEndPlusOne = new((byte)('z' + 1));
        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        Vector<byte> caseBit = new(0x20);
        int i = 0;
        int lastVectorStart = source.Length - width;

        for (; i <= lastVectorStart; i += width)
        {
            // Compare the whole block against ASCII lowercase bounds to build a per-lane mask.
            Vector<byte> values = new(source.Slice(i, width));
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<byte> aboveLowerBound = Vector.GreaterThan(values, lowercaseStartMinusOne);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<byte> belowUpperBound = Vector.LessThan(values, lowercaseEndPlusOne);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<byte> lowercaseMask = Vector.BitwiseAnd(aboveLowerBound, belowUpperBound);
            // Intersect with 0x20 so only lowercase letters lose their ASCII case bit.
            Vector<byte> caseMask = Vector.BitwiseAnd(lowercaseMask, caseBit);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<byte> upper = Vector.Subtract(values, caseMask);
            upper.CopyTo(destination.Slice(i, width));
        }

        for (; i < source.Length; i++)
        {
            byte value = source[i];
            // The scalar tail handles leftover bytes when the input length is not SIMD-aligned.
            destination[i] = value is >= (byte)'a' and <= (byte)'z'
                ? (byte)(value - 32)
                : value;
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class SimdAsciiUppercaseBenchmarks
{
    private string _input = null!;
    private byte[] _source = null!;
    private byte[] _destination = null!;

    [Params(32, 256, 4096)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        char[] chars = new char[Length];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(i % 11 switch
            {
                0 => '-',
                1 => ' ',
                2 => 'A' + (i % 26),
                _ => 'a' + (i % 26)
            });
        }

        _input = new string(chars);
        _source = Encoding.ASCII.GetBytes(_input);
        _destination = new byte[_source.Length];
    }

    [Benchmark(Baseline = true)]
    public string BuiltIn()
    {
        return _input.ToUpperInvariant();
    }

    [Benchmark]
    public void Simd()
    {
        AsciiUppercaser.Convert(_source, _destination);
    }
}
