// =============================================================================
// Exercise 02 — Zero-Allocation CSV Parser with Quoted Field Support
// Chapter 05: Strings and Text Processing
// The Art of Optimization
// =============================================================================
//
// This benchmark builds on the basic zero-allocation CSV parser from Chapter 04
// by adding support for RFC 4180 quoted fields — the standard CSV format used
// by Excel, databases, and data interchange tools.
//
// RFC 4180 CSV FORMAT
// -------------------
// The RFC 4180 specification defines these rules for CSV fields:
//   1. Fields are separated by commas (or another delimiter).
//   2. Fields MAY be enclosed in double quotes: "value"
//   3. If a field contains a comma, newline, or double quote, it MUST be quoted.
//   4. A double quote inside a quoted field is escaped by doubling it: ""
//
// Examples:
//   Alice,30,Seattle          → 3 simple fields
//   Alice,30,"Seattle, WA"   → 3 fields; third contains a comma
//   "Say ""hello"""          → 1 field whose value is: Say "hello"
//
// WHY QUOTED FIELD PARSING MATTERS
// --------------------------------
// string.Split(',') CANNOT correctly parse quoted CSV. Given:
//   Alice,30,"Seattle, WA",Engineer
// Split produces 5 fields: [Alice] [30] ["Seattle] [ WA"] [Engineer]
// The correct result is 4 fields: [Alice] [30] [Seattle, WA] [Engineer]
//
// Our span-based parser handles this correctly AND with zero allocations.
// This is both a performance AND correctness advantage.
//
// HOW QUOTED FIELD PARSING WORKS
// ------------------------------
// When MoveNext() encounters a field starting with '"':
//   1. Skip the opening quote.
//   2. Scan forward character by character.
//   3. When we hit a '"', check the next character:
//      - If it's another '"', this is an escaped quote — skip both, continue.
//      - Otherwise, this is the closing quote — the field content is everything
//        between the opening and closing quotes.
//   4. After the closing quote, skip past the separator (if present).
//
// Note: The extracted span still contains escaped "" pairs. A full production
// parser would unescape them, but that requires allocation (building a new
// string). For benchmarking purposes we return the raw content between quotes,
// which is sufficient to demonstrate zero-allocation slicing.
//
// EXPECTED RESULTS
// ----------------
// SpanBased benchmarks: 0 B allocated, handles quoted fields correctly.
// Traditional benchmarks: allocates string[] + strings per call.
// Traditional CANNOT correctly parse quoted fields — Split breaks on commas
// inside quotes, producing wrong field counts and garbled values.
// =============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// Top-level statement — must appear before all type declarations.
BenchmarkRunner.Run<CsvBenchmarks>();

// -----------------------------------------------------------------------------
// CsvFieldEnumerator — a ref struct that parses CSV fields with zero allocations,
// including full support for RFC 4180 quoted fields.
// -----------------------------------------------------------------------------
// This is a ref struct because it holds ReadOnlySpan<char> fields. The compiler
// guarantees it can never escape to the heap — it lives entirely on the stack.
//
// It implements the duck-typed enumerator pattern (GetEnumerator + MoveNext +
// Current) so it works directly with foreach. ref structs cannot implement
// interfaces, but C# foreach only requires the pattern, not the interface.
//
// The parser handles two cases in MoveNext():
//   a) Simple fields: find the next separator, slice before/after it.
//   b) Quoted fields: detect the opening '"', scan for the matching closing '"'
//      (skipping escaped "" pairs), extract the content between quotes, then
//      advance past the separator after the closing quote.
//
// All operations are span slices on the original string's memory — no copies,
// no allocations, no GC pressure. This is what makes it "zero-alloc".
// -----------------------------------------------------------------------------

ref struct CsvFieldEnumerator
{
    private ReadOnlySpan<char> _remaining;
    private ReadOnlySpan<char> _current;
    private readonly char _separator;

    public CsvFieldEnumerator(ReadOnlySpan<char> line, char separator = ',')
    {
        _remaining = line;
        _current = default;
        _separator = separator;
    }

    /// <summary>
    /// The current field — a slice into the original span's memory.
    /// For quoted fields, this is the content between the quotes (with ""
    /// escape sequences still present — unescaping would require allocation).
    /// </summary>
    public ReadOnlySpan<char> Current => _current;

    /// <summary>
    /// Advances to the next CSV field, handling both simple and quoted fields.
    ///
    /// Simple field algorithm:
    ///   1. Find the next separator using IndexOf.
    ///   2. Slice _current = everything before the separator.
    ///   3. Advance _remaining past the separator.
    ///   4. If no separator found, _current = all of _remaining (last field).
    ///
    /// Quoted field algorithm (when field starts with '"'):
    ///   1. Skip the opening quote character.
    ///   2. Scan character by character looking for '"'.
    ///   3. When '"' is found, peek at the next character:
    ///      - If it is also '"', this is an escaped double-quote per RFC 4180.
    ///        Skip both characters and continue scanning.
    ///      - Otherwise, this is the closing quote. The field content is the
    ///        span between the opening and closing quotes.
    ///   4. After the closing quote, skip the separator if present.
    ///
    /// Every operation is a span slice — just pointer + length arithmetic on
    /// the stack. Zero heap allocations regardless of field count or content.
    /// </summary>
    public bool MoveNext()
    {
        if (_remaining.IsEmpty)
            return false;

        // --- Quoted field: starts with '"' ---
        if (_remaining[0] == '"')
        {
            // Skip the opening quote.
            ReadOnlySpan<char> afterOpenQuote = _remaining[1..];
            int pos = 0;

            while (pos < afterOpenQuote.Length)
            {
                if (afterOpenQuote[pos] == '"')
                {
                    // Check if this is an escaped "" or the closing quote.
                    if (pos + 1 < afterOpenQuote.Length && afterOpenQuote[pos + 1] == '"')
                    {
                        // Escaped double-quote ("") — skip both and continue.
                        pos += 2;
                    }
                    else
                    {
                        // Closing quote found. Extract content between quotes.
                        _current = afterOpenQuote[..pos];

                        // Advance past the closing quote.
                        int afterClose = pos + 1;

                        // Skip the separator after the closing quote, if present.
                        if (afterClose < afterOpenQuote.Length
                            && afterOpenQuote[afterClose] == _separator)
                        {
                            afterClose++;
                        }

                        _remaining = afterClose < afterOpenQuote.Length
                            ? afterOpenQuote[afterClose..]
                            : ReadOnlySpan<char>.Empty;

                        return true;
                    }
                }
                else
                {
                    pos++;
                }
            }

            // Malformed: no closing quote found. Treat rest as the field.
            _current = afterOpenQuote;
            _remaining = ReadOnlySpan<char>.Empty;
            return true;
        }

        // --- Simple (unquoted) field ---
        int sepIndex = _remaining.IndexOf(_separator);

        if (sepIndex >= 0)
        {
            _current = _remaining[..sepIndex];
            _remaining = _remaining[(sepIndex + 1)..];
        }
        else
        {
            // Last field — no more separators.
            _current = _remaining;
            _remaining = ReadOnlySpan<char>.Empty;
        }

        return true;
    }

    /// <summary>
    /// Returns itself — enables foreach directly on CsvFieldEnumerator.
    /// Same pattern used by Span&lt;T&gt;.Enumerator in the BCL.
    /// </summary>
    public CsvFieldEnumerator GetEnumerator() => this;
}

// -----------------------------------------------------------------------------
// Traditional CSV parser — the classic heap-allocating approach
// -----------------------------------------------------------------------------
// string.Split() is simple but has two problems:
//   1. Performance: allocates a string[] and new string objects for every call.
//   2. Correctness: it CANNOT handle quoted fields. Given the input:
//        Alice,30,"Seattle, WA",Engineer
//      Split(',') produces 5 fields: [Alice] [30] ["Seattle] [ WA"] [Engineer]
//      The correct answer is 4 fields: [Alice] [30] [Seattle, WA] [Engineer]
//
// The benchmarks below demonstrate both problems side by side.
// -----------------------------------------------------------------------------

static class TraditionalParser
{
    public static string[] Parse(string line, char separator = ',')
        => line.Split(separator);
}

// -----------------------------------------------------------------------------
// Benchmark class
// -----------------------------------------------------------------------------
// [MemoryDiagnoser] tracks heap allocations per call, proving the span-based
// parser allocates zero bytes.
//
// We test two CSV strings:
//   - Simple: no quoted fields — both parsers produce correct results.
//   - Complex: contains quoted fields with embedded commas and escaped quotes.
//     The traditional parser produces WRONG results (too many fields, garbled
//     values), while the span-based parser handles it correctly.
//
// Each benchmark sums field lengths to prevent dead-code elimination and to
// ensure both approaches do equivalent work.
// -----------------------------------------------------------------------------

[MemoryDiagnoser]
public class CsvBenchmarks
{
    private string _simpleCsv = null!;
    private string _complexCsv = null!;

    /// <summary>
    /// Prepares two test CSV strings:
    ///
    /// Simple: "Alice,30,Seattle,Engineer,Active"
    ///   → 5 fields, no quoting needed.
    ///
    /// Complex: Alice,30,"Seattle, WA","Software ""Engineer""",Active
    ///   → 5 fields, but field 3 contains a comma and field 4 contains
    ///     escaped double quotes. This is valid RFC 4180 CSV.
    ///   → string.Split(',') will produce 7 fields (wrong!).
    ///   → CsvFieldEnumerator will produce 5 fields (correct).
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _simpleCsv = "Alice,30,Seattle,Engineer,Active";

        // Complex CSV with quoted fields per RFC 4180:
        //   Field 1: Alice              (simple)
        //   Field 2: 30                 (simple)
        //   Field 3: "Seattle, WA"      (quoted — contains comma)
        //   Field 4: "Software ""Engineer"""  (quoted — contains escaped quotes)
        //   Field 5: Active             (simple)
        _complexCsv = "Alice,30,\"Seattle, WA\",\"Software \"\"Engineer\"\"\",Active";
    }

    /// <summary>
    /// Traditional parser on simple CSV.
    /// Allocates a string[] (5 elements) + 5 string objects per call.
    /// Produces correct results for unquoted fields.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Traditional_Simple()
    {
        int totalLength = 0;
        string[] fields = TraditionalParser.Parse(_simpleCsv);
        for (int i = 0; i < fields.Length; i++)
            totalLength += fields[i].Length;
        return totalLength;
    }

    /// <summary>
    /// Span-based parser on simple CSV.
    /// Zero allocations — all field access is via ReadOnlySpan slices into
    /// the original string's character buffer. Nothing touches the heap.
    /// </summary>
    [Benchmark]
    public int SpanBased_Simple()
    {
        int totalLength = 0;
        foreach (ReadOnlySpan<char> field in new CsvFieldEnumerator(_simpleCsv.AsSpan()))
            totalLength += field.Length;
        return totalLength;
    }

    /// <summary>
    /// Traditional parser on complex (quoted) CSV.
    /// Not only does this allocate, it produces WRONG results:
    ///   Input:  Alice,30,"Seattle, WA","Software ""Engineer""",Active
    ///   Split:  [Alice] [30] ["Seattle] [ WA"] ["Software ""Engineer"""] [Active]
    ///           → 6 fields (wrong! should be 5)
    ///           → Field values are garbled with stray quotes
    /// This demonstrates that string.Split is fundamentally broken for CSV.
    /// </summary>
    [Benchmark]
    public int Traditional_Complex()
    {
        int totalLength = 0;
        string[] fields = TraditionalParser.Parse(_complexCsv);
        for (int i = 0; i < fields.Length; i++)
            totalLength += fields[i].Length;
        return totalLength;
    }

    /// <summary>
    /// Span-based parser on complex (quoted) CSV.
    /// Zero allocations AND correct results:
    ///   Input:  Alice,30,"Seattle, WA","Software ""Engineer""",Active
    ///   Fields: [Alice] [30] [Seattle, WA] [Software ""Engineer""] [Active]
    ///           → 5 fields (correct!)
    ///           → Quoted content extracted properly
    /// The "" escape sequences remain in the output (unescaping would require
    /// allocation), but the field boundaries are correct.
    /// </summary>
    [Benchmark]
    public int SpanBased_Complex()
    {
        int totalLength = 0;
        foreach (ReadOnlySpan<char> field in new CsvFieldEnumerator(_complexCsv.AsSpan()))
            totalLength += field.Length;
        return totalLength;
    }
}
