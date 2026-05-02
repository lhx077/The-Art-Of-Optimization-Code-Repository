// =============================================================================
// Exercise 03 — Zero-Allocation CSV Parser with ref struct and Span<T>
// Chapter 04: Type System — Value Types vs Reference Types
// The Art of Optimization
// =============================================================================
//
// This benchmark demonstrates how ref struct combined with ReadOnlySpan<char>
// enables zero-allocation string parsing — one of the most powerful performance
// techniques available in modern C#.
//
// BACKGROUND — THE COST OF STRING.SPLIT
// --------------------------------------
// The traditional approach to parsing CSV fields is string.Split(','). This is
// simple and readable, but every call allocates:
//   1. A new string[] array to hold the results.
//   2. A new System.String object for EACH field (copying characters out).
// For a line with 50 fields parsed 10,000 times, that is 500,000 string
// allocations + 10,000 array allocations — enormous GC pressure that fills
// Gen0, triggers collections, and stalls your application.
//
// THE SOLUTION — REF STRUCT + READONLYSPAN
// -----------------------------------------
// A ref struct is a struct that can ONLY live on the stack. The compiler
// enforces these hard rules:
//   - Cannot be boxed (no casting to object or interfaces).
//   - Cannot be a field of a class or a regular struct.
//   - Cannot be captured by lambdas, async methods, or iterators.
//   - Cannot be used as a generic type argument (in older C# versions).
// These restrictions exist because ref structs can contain Span<T> fields —
// interior pointers into memory. If a ref struct escaped to the heap, the GC
// could relocate the underlying memory and leave a dangling pointer.
// Span<T> and ReadOnlySpan<T> are themselves ref structs, so ONLY another
// ref struct can hold them as fields — both share the "stack-only" guarantee.
//
// ReadOnlySpan<char> is a view into an existing string WITHOUT copying.
// Slicing (span[..n] or span[n..]) creates a new span pointing into the
// SAME backing memory — zero allocations, zero copies.
//
// THE ENUMERATOR PATTERN FOR REF STRUCTS
// ----------------------------------------
// CsvReader cannot implement IEnumerator<T> (ref structs cannot implement
// interfaces). But C# foreach uses duck typing — it only requires:
//   - A GetEnumerator() method on the collection type
//   - The returned type has: bool MoveNext() and a Current property
// CsvReader acts as BOTH collection and enumerator (GetEnumerator returns
// itself). Same pattern as Span<T>.Enumerator in the BCL.
//
// EXPECTED RESULTS
// ----------------
// SpanBasedCsvReader: 0 B allocated, significantly faster.
// TraditionalSplit: large allocations scaling linearly with Iterations.
// =============================================================================

using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// Top-level statement — must appear before all type declarations.
BenchmarkRunner.Run<CsvParserBenchmarks>();

// -----------------------------------------------------------------------------
// CsvReader — a ref struct that parses CSV fields with zero allocations
// -----------------------------------------------------------------------------
// This is a ref struct because it contains a ReadOnlySpan<char> field. The
// compiler enforces that it can never escape to the heap — it lives and dies
// on the stack. This is what makes zero-allocation parsing possible.
//
// It implements the duck-typed enumerator pattern: it does not implement
// IEnumerator<T> (it cannot — ref structs cannot implement interfaces).
// Instead, it provides GetEnumerator(), MoveNext(), and Current, which is
// all that foreach needs to work.
// -----------------------------------------------------------------------------

ref struct CsvReader
{
    private ReadOnlySpan<char> _remaining;

    public CsvReader(ReadOnlySpan<char> line)
    {
        _remaining = line;
        Current = default;
    }

    /// <summary>
    /// The current field — a slice of the original span, not a new string.
    /// No characters are copied; this is just a (pointer + length) pair
    /// pointing into the original string's memory.
    /// </summary>
    public ReadOnlySpan<char> Current { get; private set; }

    /// <summary>
    /// Advances to the next comma-delimited field.
    ///
    /// Algorithm:
    ///   1. If _remaining is empty, iteration is complete — return false.
    ///   2. Search for the next comma using IndexOf(',').
    ///   3. If found: Current = everything before the comma,
    ///                _remaining = everything after the comma.
    ///   4. If not found: Current = all of _remaining (last field),
    ///                    _remaining = ReadOnlySpan{char}.Empty.
    ///
    /// Every operation here is a span slice — just adjusting a pointer and
    /// length on the stack. Zero heap allocations.
    /// </summary>
    public bool MoveNext()
    {
        if (_remaining.IsEmpty)
            return false;

        int commaIndex = _remaining.IndexOf(',');

        if (commaIndex >= 0)
        {
            // Found a comma — slice out the field before it, advance past it.
            Current = _remaining[..commaIndex];
            _remaining = _remaining[(commaIndex + 1)..];
        }
        else
        {
            // No comma found — this is the last field. Consume everything.
            Current = _remaining;
            _remaining = ReadOnlySpan<char>.Empty;
        }

        return true;
    }

    /// <summary>
    /// Returns itself — this is what enables foreach to work directly on
    /// CsvReader. The compiler calls GetEnumerator() on the "collection",
    /// then repeatedly calls MoveNext()/Current on the result. Since
    /// CsvReader IS the enumerator, it just returns this.
    /// </summary>
    public CsvReader GetEnumerator() => this;
}

// -----------------------------------------------------------------------------
// Traditional CSV parser — the classic heap-allocating approach
// -----------------------------------------------------------------------------
// string.Split(',') must:
//   a) Scan the string to count delimiters
//   b) Allocate a string[] array of the right size
//   c) For each field, allocate a NEW string object (copying characters)
// Simple and readable, but costly in hot paths.
// -----------------------------------------------------------------------------

static class TraditionalCsvParser
{
    /// <summary>
    /// Parses CSV fields using string.Split(',').
    /// Allocates a new string[] and a new string for each field.
    /// </summary>
    public static string[] Parse(string line) => line.Split(',');
}

// -----------------------------------------------------------------------------
// Benchmark class
// -----------------------------------------------------------------------------
// [MemoryDiagnoser] tells BenchmarkDotNet to track heap allocations per call.
// This is how we prove the Span-based parser allocates zero bytes.
//
// [Params] varies the iteration count so we can see how allocation pressure
// scales linearly with the traditional approach while staying at zero for
// the span-based approach.
// -----------------------------------------------------------------------------

[MemoryDiagnoser]
public class CsvParserBenchmarks
{
    private string _csvLine = null!;

    /// <summary>
    /// Number of times to parse the CSV line per benchmark invocation.
    /// At 10,000 iterations with 50 fields, the traditional approach creates
    /// 500,000 string objects + 10,000 arrays — visible GC pressure.
    /// </summary>
    [Params(100, 10_000)]
    public int Iterations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Build a realistic CSV line with 50 fields of varying lengths.
        // This simulates a real-world data row with names, numbers, dates,
        // emails, and other typical field values.
        var sb = new StringBuilder();
        string[] sampleFields =
        [
            "Alice", "30", "Engineer", "Seattle", "USA",
            "alice@example.com", "2024-01-15", "Active", "Premium", "12345",
            "Bob", "25", "Designer", "Portland", "USA",
            "bob@example.com", "2024-03-22", "Active", "Basic", "67890",
            "Charlie", "35", "Manager", "Denver", "USA",
            "charlie@example.com", "2023-11-08", "Inactive", "Premium", "11223",
            "Diana", "28", "Analyst", "Austin", "USA",
            "diana@example.com", "2024-06-01", "Active", "Enterprise", "44556",
            "Eve", "42", "Director", "Chicago", "USA",
            "eve@example.com", "2022-09-30", "Active", "Premium", "77889",
            "Frank", "31", "Developer", "Boston", "USA",
            "frank@example.com", "2024-02-14", "Active", "Basic", "99001",
            "Grace", "27", "Tester", "Miami", "USA",
            "grace@example.com", "2024-04-18", "Active", "Premium", "22334",
            "Hank", "39", "Architect", "Phoenix", "USA",
            "hank@example.com", "2023-07-25", "Active", "Enterprise", "55667",
            "Ivy", "33", "Scientist", "SanDiego", "USA",
            "ivy@example.com", "2024-05-10", "Active", "Premium", "88990",
            "Jack", "45", "VP", "NewYork", "USA",
            "jack@example.com", "2021-12-01", "Active", "Enterprise", "33445"
        ];

        for (int i = 0; i < sampleFields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(sampleFields[i]);
        }

        _csvLine = sb.ToString();
    }

    /// <summary>
    /// Traditional approach: string.Split(',') creates a new string[] and
    /// individual string objects on the heap every single iteration.
    /// The GC must eventually collect all of them.
    ///
    /// We sum field lengths to ensure the compiler does not optimize away
    /// the work, and to make both benchmarks do equivalent computation.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int TraditionalSplit()
    {
        int totalLength = 0;

        for (int i = 0; i < Iterations; i++)
        {
            string[] fields = TraditionalCsvParser.Parse(_csvLine);
            for (int j = 0; j < fields.Length; j++)
                totalLength += fields[j].Length;
        }

        return totalLength;
    }

    /// <summary>
    /// Zero-allocation approach: CsvReader slices the original string's
    /// memory via ReadOnlySpan&lt;char&gt;. No new strings or arrays are
    /// created — everything stays on the stack.
    ///
    /// .AsSpan() on a string returns a ReadOnlySpan&lt;char&gt; pointing at
    /// the string's internal character buffer. From there, every slice
    /// operation just adjusts a pointer and length — zero copies.
    ///
    /// foreach works here because CsvReader provides the duck-typed
    /// enumerator pattern: GetEnumerator() + MoveNext() + Current.
    /// </summary>
    [Benchmark]
    public int SpanBasedCsvReader()
    {
        int totalLength = 0;

        for (int i = 0; i < Iterations; i++)
        {
            foreach (ReadOnlySpan<char> field in new CsvReader(_csvLine.AsSpan()))
                totalLength += field.Length;
        }

        return totalLength;
    }
}