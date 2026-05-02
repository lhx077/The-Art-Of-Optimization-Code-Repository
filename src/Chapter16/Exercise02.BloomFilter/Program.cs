// ============================================================================
// Exercise 02 — Bloom Filter
// ============================================================================
// Implements a simple Bloom filter backed by a bit array. The benchmark section
// studies false-positive behavior under different bit budgets and hash counts.
// ============================================================================

using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<BloomFilterBenchmarks>();
    return;
}

SimpleBloomFilter filter = new(bitCount: 2048, hashCount: 4);
filter.Add("alpha");
filter.Add("beta");
filter.Add("gamma");

Console.WriteLine("=== Bloom Filter Demo ===");
Console.WriteLine($"alpha: {filter.MayContain("alpha")}");
Console.WriteLine($"delta: {filter.MayContain("delta")}");
Console.WriteLine($"False positives in sample: {EstimateFalsePositiveRate(filter, inserted: 200, tested: 2000):P2}");

static double EstimateFalsePositiveRate(SimpleBloomFilter filter, int inserted, int tested)
{
    SimpleBloomFilter probe = new(filter.BitCount, filter.HashCount);
    for (int i = 0; i < inserted; i++)
    {
        probe.Add($"item-{i}");
    }

    int falsePositives = 0;
    for (int i = inserted; i < inserted + tested; i++)
    {
        if (probe.MayContain($"item-{i}"))
        {
            falsePositives++;
        }
    }

    return (double)falsePositives / tested;
}

// Core implementation used by the demo and benchmark code below.
public sealed class SimpleBloomFilter
{
    private readonly ulong[] _bits;

    public SimpleBloomFilter(int bitCount, int hashCount)
    {
        if (bitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        if (hashCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hashCount));
        }

        BitCount = bitCount;
        HashCount = hashCount;
        _bits = new ulong[(bitCount + 63) >> 6];
    }

    public int BitCount { get; }
    public int HashCount { get; }

    public void Add(string value)
    {
        ulong hash1 = Fnv1A64(value, 14695981039346656037UL);
        ulong hash2 = Mix64(hash1 ^ 0x9E3779B97F4A7C15UL);

        foreach (int index in GetBitPositions(hash1, hash2))
        {
            // Each derived position sets one bit; collisions are expected and harmless.
            _bits[index >> 6] |= 1UL << (index & 63);
        }
    }

    public bool MayContain(string value)
    {
        ulong hash1 = Fnv1A64(value, 14695981039346656037UL);
        ulong hash2 = Mix64(hash1 ^ 0x9E3779B97F4A7C15UL);

        foreach (int index in GetBitPositions(hash1, hash2))
        {
            // One missing bit is enough to prove the value was never added.
            if (((_bits[index >> 6] >> (index & 63)) & 1UL) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private int[] GetBitPositions(ulong hash1, ulong hash2)
    {
        int[] positions = new int[HashCount];

        // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
        if (Vector<ulong>.Count >= 2 && HashCount >= 2)
        {
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Span<ulong> seeds = stackalloc ulong[Vector<ulong>.Count];
            for (int i = 0; i < seeds.Length; i++)
            {
                seeds[i] = (ulong)(i + 1);
            }

            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<ulong> baseHash = new(hash1);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<ulong> stepHash = new(hash2);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<ulong> multipliers = new(seeds);
            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Vector<ulong> vectorHashes = baseHash + (stepHash * multipliers);

            // The vectorized path processes several values per iteration before a scalar tail finishes the remainder.
            Span<ulong> buffer = stackalloc ulong[Vector<ulong>.Count];
            vectorHashes.CopyTo(buffer);

            int produced = Math.Min(HashCount, buffer.Length);
            for (int i = 0; i < produced; i++)
            {
                // Materialize as many probe positions as the SIMD batch produced.
                positions[i] = (int)(buffer[i] % (uint)BitCount);
            }

            for (int i = produced; i < HashCount; i++)
            {
                // Fall back to scalar generation when the requested hash count exceeds one SIMD batch.
                positions[i] = (int)((hash1 + ((ulong)(i + 1) * hash2)) % (uint)BitCount);
            }

            return positions;
        }

        for (int i = 0; i < HashCount; i++)
        {
            positions[i] = (int)((hash1 + ((ulong)(i + 1) * hash2)) % (uint)BitCount);
        }

        return positions;
    }

    private static ulong Fnv1A64(string value, ulong offsetBasis)
    {
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;

        foreach (char c in value)
        {
            // Fold each character into the rolling hash before the final avalanche mix.
            hash ^= c;
            hash *= prime;
        }

        return Mix64(hash);
    }

    private static ulong Mix64(ulong value)
    {
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        value *= 0xc4ceb9fe1a85ec53UL;
        value ^= value >> 33;
        return value;
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class BloomFilterBenchmarks
{
    private string[] _inserted = null!;
    private string[] _missing = null!;

    [Params(4096, 16384, 65536)]
    public int BitCount;

    [Params(3, 5, 7)]
    public int HashCount;

    [GlobalSetup]
    public void Setup()
    {
        _inserted = Enumerable.Range(0, 4000).Select(i => $"known-{i}").ToArray();
        _missing = Enumerable.Range(0, 4000).Select(i => $"unknown-{i}").ToArray();
    }

    [Benchmark]
    public double FalsePositiveRate()
    {
        SimpleBloomFilter filter = new(BitCount, HashCount);
        foreach (string value in _inserted)
        {
            filter.Add(value);
        }

        int falsePositives = 0;
        foreach (string value in _missing)
        {
            if (filter.MayContain(value))
            {
                falsePositives++;
            }
        }

        return (double)falsePositives / _missing.Length;
    }
}
