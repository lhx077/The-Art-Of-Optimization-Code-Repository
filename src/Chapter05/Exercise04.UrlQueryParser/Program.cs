using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 04 — Zero-Allocation URL Query String Parser
//
// URL query strings follow the format: ?key1=value1&key2=value2&key3=value3
// Special characters are percent-encoded: spaces become '+' or '%20',
// '=' becomes '%3D', '/' becomes '%2F', etc.  The format is %XX where XX
// is the two-digit hexadecimal ASCII code of the character.
//
// Traditional parsing (string.Split, Dictionary, Uri.UnescapeDataString)
// allocates many intermediate strings and a dictionary on every call.
//
// This exercise demonstrates a zero-allocation approach using:
//   - ref struct QueryStringEnumerator: iterates key-value pairs as
//     ReadOnlySpan<char> slices — no string objects created.
//   - readonly ref struct QueryParam: holds Key/Value spans without boxing.
//   - PercentDecoder: decodes %XX sequences into a caller-provided Span<char>
//     buffer, avoiding all heap allocation.
//
// The enumerator implements GetEnumerator() returning itself, which lets
// C# foreach desugar into pure struct calls — the compiler never boxes it
// and no IEnumerator<T> interface allocation occurs.
//
// Expected benchmark results:
//   SpanBased methods  → 0 bytes allocated (Gen0 = 0)
//   Traditional methods → hundreds of bytes per call (strings + dictionary)
// ============================================================================

BenchmarkRunner.Run<UrlParserBenchmarks>();

// ============================================================================
// QueryParam — A single key-value pair backed by ReadOnlySpan<char>
// ============================================================================
//
// Because this is a readonly ref struct, it can only live on the stack.
// The Key and Value fields are slices into the original query string —
// no new string objects are created at any point.
// ============================================================================

readonly ref struct QueryParam
{
    public readonly ReadOnlySpan<char> Key;
    public readonly ReadOnlySpan<char> Value;

    public QueryParam(ReadOnlySpan<char> key, ReadOnlySpan<char> value)
    {
        Key = key;
        Value = value;
    }
}

// ============================================================================
// QueryStringEnumerator — Iterates query parameters without allocation
// ============================================================================
//
// This ref struct walks through a query string character-by-character
// (via Span slicing), yielding QueryParam values.  Because it implements
// the duck-typed enumerator pattern (GetEnumerator + Current + MoveNext),
// a foreach loop over it compiles to direct struct method calls — the
// compiler never allocates an IEnumerator on the heap.
//
// Parsing logic:
//   1. Strip the leading '?' if present.
//   2. On each MoveNext(), find the next '&' delimiter (or end of string).
//   3. Within that segment, find '=' to split key from value.
//   4. Edge cases: "flag" (no '=') yields key="flag", value="".
//      Empty segments (e.g. "&&") are skipped.
// ============================================================================

ref struct QueryStringEnumerator
{
    private ReadOnlySpan<char> _remaining;
    private QueryParam _current;

    public QueryStringEnumerator(ReadOnlySpan<char> queryString)
    {
        // Strip the leading '?' that typically starts a query string.
        if (queryString.Length > 0 && queryString[0] == '?')
            queryString = queryString.Slice(1);

        _remaining = queryString;
        _current = default;
    }

    /// <summary>The current key-value pair.</summary>
    public QueryParam Current => _current;

    /// <summary>
    /// Advances to the next parameter.  Returns false when the string
    /// has been fully consumed.
    /// </summary>
    public bool MoveNext()
    {
        // Skip empty segments produced by consecutive '&' characters.
        while (_remaining.Length > 0)
        {
            // Find the next '&' delimiter, or take the rest of the string.
            int ampIndex = _remaining.IndexOf('&');
            ReadOnlySpan<char> segment;

            if (ampIndex >= 0)
            {
                segment = _remaining.Slice(0, ampIndex);
                _remaining = _remaining.Slice(ampIndex + 1);
            }
            else
            {
                segment = _remaining;
                _remaining = default;
            }

            // Skip empty segments (e.g. from "&&" or trailing "&").
            if (segment.IsEmpty)
                continue;

            // Split the segment on '=' to separate key and value.
            // If there is no '=', the entire segment is the key (e.g. "flag").
            int eqIndex = segment.IndexOf('=');
            if (eqIndex >= 0)
            {
                _current = new QueryParam(
                    segment.Slice(0, eqIndex),
                    segment.Slice(eqIndex + 1));
            }
            else
            {
                _current = new QueryParam(segment, ReadOnlySpan<char>.Empty);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns this enumerator itself — the duck-typed pattern that lets
    /// foreach work directly on the struct without boxing.
    /// </summary>
    public QueryStringEnumerator GetEnumerator() => this;
}

// ============================================================================
// PercentDecoder — Decodes %XX and '+' sequences without allocation
// ============================================================================
//
// Percent-encoding (RFC 3986) represents unsafe characters as %XX where
// XX is the hex value of the byte.  The '+' character is a legacy encoding
// for space (from application/x-www-form-urlencoded).
//
// This decoder writes into a caller-provided Span<char> buffer, so it
// never allocates heap memory.  The caller is responsible for providing
// a buffer large enough (input.Length is always sufficient because
// decoding can only shrink or maintain the length, never grow it).
// ============================================================================

static class PercentDecoder
{
    /// <summary>
    /// Decodes percent-encoded and '+' characters from <paramref name="input"/>
    /// into <paramref name="output"/>.  Returns the number of chars written.
    /// </summary>
    /// <returns>
    /// true if decoding succeeded; false if the output buffer was too small.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<char> input, Span<char> output, out int written)
    {
        written = 0;
        int i = 0;

        while (i < input.Length)
        {
            if (written >= output.Length)
            {
                written = 0;
                return false;
            }

            char c = input[i];

            if (c == '+')
            {
                // Legacy form-encoding: '+' represents a space.
                output[written++] = ' ';
                i++;
            }
            else if (c == '%' && i + 2 < input.Length
                     && IsHexDigit(input[i + 1])
                     && IsHexDigit(input[i + 2]))
            {
                // Decode %XX → character with that hex value.
                int high = HexVal(input[i + 1]);
                int low = HexVal(input[i + 2]);
                output[written++] = (char)((high << 4) | low);
                i += 3;
            }
            else
            {
                // Ordinary character — copy as-is.
                output[written++] = c;
                i++;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0
    };
}

// ============================================================================
// Benchmarks — Span-based vs Traditional query string parsing
// ============================================================================

[MemoryDiagnoser]
public class UrlParserBenchmarks
{
    private string _simpleQuery = default!;
    private string _complexQuery = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Simple: plain ASCII keys and values, no encoding needed.
        _simpleQuery = "?name=Alice&age=30&city=Seattle";

        // Complex: '+' for spaces, %XX sequences, empty value, bare flag.
        _complexQuery = "?q=hello+world&path=%2Fapi%2Fv1&token=abc%3D%3D&empty=&flag";
    }

    // ----------------------------------------------------------------
    // Span-based benchmarks — expected: 0 bytes allocated
    // ----------------------------------------------------------------

    [Benchmark(Description = "SpanBased_Simple")]
    public int SpanBased_Simple()
    {
        // Iterate all parameters using the zero-alloc enumerator.
        // We sum lengths to prevent the JIT from optimizing the loop away.
        int total = 0;
        foreach (var param in new QueryStringEnumerator(_simpleQuery))
        {
            total += param.Key.Length + param.Value.Length;
        }
        return total;
    }

    [Benchmark(Description = "SpanBased_Complex")]
    public int SpanBased_Complex()
    {
        // Parse and decode each value into a stack-allocated buffer.
        int total = 0;
        Span<char> decodeBuffer = stackalloc char[256];

        foreach (var param in new QueryStringEnumerator(_complexQuery))
        {
            total += param.Key.Length;

            if (PercentDecoder.TryDecode(param.Value, decodeBuffer, out int written))
                total += written;
        }
        return total;
    }

    // ----------------------------------------------------------------
    // Traditional benchmarks — expected: hundreds of bytes allocated
    // (strings from Split, dictionary, Uri.UnescapeDataString results)
    // ----------------------------------------------------------------

    [Benchmark(Description = "Traditional_Simple", Baseline = true)]
    public int Traditional_Simple()
    {
        // The classic approach: split into strings, build a dictionary.
        var dict = new Dictionary<string, string>();
        string query = _simpleQuery.TrimStart('?');
        string[] pairs = query.Split('&');

        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq >= 0)
                dict[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            else
                dict[pair] = string.Empty;
        }

        // Sum lengths to match what the span-based benchmark does.
        int total = 0;
        foreach (var kvp in dict)
            total += kvp.Key.Length + kvp.Value.Length;
        return total;
    }

    [Benchmark(Description = "Traditional_Complex")]
    public int Traditional_Complex()
    {
        var dict = new Dictionary<string, string>();
        string query = _complexQuery.TrimStart('?');
        string[] pairs = query.Split('&');

        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq >= 0)
            {
                string key = pair.Substring(0, eq);
                // Uri.UnescapeDataString handles %XX; we also replace '+' with space.
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                dict[key] = value;
            }
            else
            {
                dict[pair] = string.Empty;
            }
        }

        int total = 0;
        foreach (var kvp in dict)
            total += kvp.Key.Length + kvp.Value.Length;
        return total;
    }
}
