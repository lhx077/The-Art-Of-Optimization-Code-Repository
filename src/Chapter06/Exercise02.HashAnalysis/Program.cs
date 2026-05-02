using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 02 — Hash Function Analysis
// The Art of Optimization, Chapter 6: Collections and Data Structures
//
// What makes a good hash function?
//   1. Uniform distribution (avalanche effect) — a small change in input
//      should flip roughly half the bits in the output.
//   2. Speed — the hash is called on every insert, lookup, and delete.
//   3. Low collision rate — collisions force the dictionary to walk a chain
//      (or probe further), turning O(1) lookups into O(n) in the worst case.
//
// We compare three custom hashes against the built-in string.GetHashCode(),
// which on modern .NET uses the Marvin32 algorithm with per-process
// randomized seed — extremely well-tuned for general-purpose use.
// ============================================================================

// FIRST executable statement — required by BenchmarkDotNet top-level style.
BenchmarkRunner.Run<HashBenchmarks>();

// ---------------------------------------------------------------------------
// Custom hash functions operating on ReadOnlySpan<char> for zero-allocation.
// ---------------------------------------------------------------------------
static class CustomHash
{
    // FNV-1a (Fowler–Noll–Vo) hash
    // -----------------------------------------------------------------------
    // Algorithm: for each byte, XOR with the hash then multiply by a prime.
    // The XOR-before-multiply order gives FNV-1a better avalanche properties
    // than the original FNV-1 (multiply-then-XOR). Widely used in hash tables,
    // checksums, and non-cryptographic fingerprinting.
    //
    // Offset basis and prime are the standard 32-bit FNV parameters.
    // -----------------------------------------------------------------------
    public static int FnvHash(ReadOnlySpan<char> input)
    {
        const uint offsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        uint hash = offsetBasis;
        foreach (char c in input)
        {
            hash ^= c;          // XOR first  (FNV-1a order)
            hash *= fnvPrime;    // then multiply by prime
        }

        return (int)hash;
    }

    // DJB2 hash (Daniel J. Bernstein)
    // -----------------------------------------------------------------------
    // One of the simplest effective hash functions. The magic constant 5381 and
    // the multiplier 33 were chosen empirically — they produce surprisingly
    // good distribution for short ASCII strings. The multiply-by-33 is often
    // implemented as (hash << 5) + hash for speed, but the JIT handles this.
    // -----------------------------------------------------------------------
    public static int DjbHash(ReadOnlySpan<char> input)
    {
        uint hash = 5381;
        foreach (char c in input)
        {
            hash = hash * 33 + c;   // hash * 33 + c
        }

        return (int)hash;
    }

    // Naive positional hash — intentionally poor
    // -----------------------------------------------------------------------
    // Sums char * (position + 1). This is a terrible hash because:
    //   - No mixing between positions (no avalanche).
    //   - Anagrams with similar character sums collide easily.
    //   - The output range is tiny compared to the key space.
    // Included as a baseline to show how bad distribution hurts performance.
    // -----------------------------------------------------------------------
    public static int SimpleMultiplyHash(ReadOnlySpan<char> input)
    {
        int hash = 0;
        for (int i = 0; i < input.Length; i++)
        {
            hash += input[i] * (i + 1);
        }

        return hash;
    }
}

// ---------------------------------------------------------------------------
// Collision analysis utility
// ---------------------------------------------------------------------------
static class CollisionAnalyzer
{
    /// <summary>
    /// Distributes <paramref name="keys"/> into <paramref name="bucketCount"/> buckets
    /// using the supplied hash function and reports the collision rate.
    ///
    /// Collision rate = (buckets with more than 1 entry) / (total occupied buckets) * 100.
    /// A perfect hash with no collisions yields 0 %. The naive hash will be noticeably
    /// worse because its limited output range causes many keys to land in the same bucket.
    /// </summary>
    public static double MeasureCollisionRate(string[] keys, Func<string, int> hashFunc, int bucketCount)
    {
        var buckets = new int[bucketCount];

        foreach (string key in keys)
        {
            // Ensure non-negative index via bitwise AND.
            int index = (hashFunc(key) & 0x7FFF_FFFF) % bucketCount;
            buckets[index]++;
        }

        int occupied = 0;
        int collided = 0;
        int maxChain = 0;

        foreach (int count in buckets)
        {
            if (count > 0) occupied++;
            if (count > 1) collided++;
            if (count > maxChain) maxChain = count;
        }

        double collisionRate = occupied > 0
            ? (double)collided / occupied * 100.0
            : 0.0;

        // Print a concise distribution summary.
        Console.WriteLine($"  Buckets: {bucketCount:N0} | Occupied: {occupied:N0} | " +
                          $"Collided: {collided:N0} | Max chain: {maxChain} | " +
                          $"Collision rate: {collisionRate:F2}%");

        return collisionRate;
    }
}

// ---------------------------------------------------------------------------
// IEqualityComparer<string> implementations for use with Dictionary<TKey,TValue>.
//
// A Dictionary uses GetHashCode() to pick a bucket and Equals() to resolve
// collisions within that bucket. By supplying a custom comparer we can swap
// the hash algorithm without changing the dictionary's API.
// ---------------------------------------------------------------------------
class FnvComparer : IEqualityComparer<string>
{
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
    public int GetHashCode(string obj) => CustomHash.FnvHash(obj.AsSpan());
}

class DjbComparer : IEqualityComparer<string>
{
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
    public int GetHashCode(string obj) => CustomHash.DjbHash(obj.AsSpan());
}

// ---------------------------------------------------------------------------
// Benchmarks
//
// We measure the time to insert 100,000 random 8-char alphanumeric strings
// into a Dictionary using different hash strategies.
//
// Expected results:
//   - string.GetHashCode() (Marvin32) is highly optimized by the runtime and
//     includes per-process randomization for HashDoS protection. It will
//     likely be the fastest or very close.
//   - FNV-1a and DJB2 are solid general-purpose hashes. They may be
//     competitive for short, fixed-length keys but will probably show
//     slightly higher collision rates than Marvin32.
//   - The naive SimpleMultiplyHash is included only in the collision analysis
//     (not benchmarked for Dictionary) to highlight how poor distribution
//     degrades real-world performance.
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class HashBenchmarks
{
    private string[] _keys = null!;

    private static readonly FnvComparer FnvCmp = new();
    private static readonly DjbComparer DjbCmp = new();

    [GlobalSetup]
    public void Setup()
    {
        const int keyCount = 100_000;
        const int keyLength = 8;
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        var rng = new Random(42); // fixed seed for reproducibility
        _keys = new string[keyCount];

        Span<char> buffer = stackalloc char[keyLength];
        for (int i = 0; i < keyCount; i++)
        {
            for (int j = 0; j < keyLength; j++)
                buffer[j] = chars[rng.Next(chars.Length)];
            _keys[i] = new string(buffer);
        }

        // --- Collision analysis (printed once during setup) ---
        const int bucketCount = 100_003; // prime close to key count
        Console.WriteLine();
        Console.WriteLine("=== Collision Analysis (100,000 keys, 100,003 buckets) ===");

        Console.Write("  string.GetHashCode : ");
        CollisionAnalyzer.MeasureCollisionRate(_keys, s => s.GetHashCode(), bucketCount);

        Console.Write("  FNV-1a             : ");
        CollisionAnalyzer.MeasureCollisionRate(_keys, s => CustomHash.FnvHash(s.AsSpan()), bucketCount);

        Console.Write("  DJB2               : ");
        CollisionAnalyzer.MeasureCollisionRate(_keys, s => CustomHash.DjbHash(s.AsSpan()), bucketCount);

        Console.Write("  SimpleMultiply     : ");
        CollisionAnalyzer.MeasureCollisionRate(_keys, s => CustomHash.SimpleMultiplyHash(s.AsSpan()), bucketCount);

        Console.WriteLine();
    }

    [Benchmark(Baseline = true)]
    public Dictionary<string, int> DefaultGetHashCode()
    {
        var dict = new Dictionary<string, int>(_keys.Length);
        for (int i = 0; i < _keys.Length; i++)
            dict[_keys[i]] = i;
        return dict;
    }

    [Benchmark]
    public Dictionary<string, int> FnvHash_Dictionary()
    {
        var dict = new Dictionary<string, int>(_keys.Length, FnvCmp);
        for (int i = 0; i < _keys.Length; i++)
            dict[_keys[i]] = i;
        return dict;
    }

    [Benchmark]
    public Dictionary<string, int> DjbHash_Dictionary()
    {
        var dict = new Dictionary<string, int>(_keys.Length, DjbCmp);
        for (int i = 0; i < _keys.Length; i++)
            dict[_keys[i]] = i;
        return dict;
    }
}
