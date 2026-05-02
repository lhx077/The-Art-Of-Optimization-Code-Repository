// ============================================================================
// Exercise 01 — Zero-Allocation String Searcher
// ============================================================================
// Searches multiple patterns inside a large text using ReadOnlySpan<char> only.
// Returns match positions rather than substrings and supports case-insensitive
// search. BenchmarkDotNet compares it with string.IndexOf.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<StringSearcherBenchmarks>();
    return;
}

ReadOnlySpan<char> text = "The quick brown fox jumps over the lazy dog. THE quick fox returns.";
ReadOnlySpan<string> patterns = ["quick", "fox", "the"];

var searcher = new StringSearcher(patterns, StringComparison.OrdinalIgnoreCase);
Span<MatchResult> results = stackalloc MatchResult[32];
int count = searcher.FindAll(text, results);

Console.WriteLine("=== StringSearcher Demo ===");
for (int i = 0; i < count; i++)
{
    var match = results[i];
    Console.WriteLine($"PatternIndex={match.PatternIndex}, Position={match.Position}, Length={match.Length}");
}

// MatchResult points back into the original text so callers can inspect matches
// without allocating substrings for every hit.
public readonly record struct MatchResult(int PatternIndex, int Position, int Length);

// StringSearcher scans one pattern at a time over the same input span.
// The implementation stays allocation-free on the hot path because it only works
// with spans and writes results into a caller-provided destination buffer.
public sealed class StringSearcher
{
    private readonly string[] _patterns;
    private readonly StringComparison _comparison;

    public StringSearcher(ReadOnlySpan<string> patterns, StringComparison comparison = StringComparison.Ordinal)
    {
        _patterns = patterns.ToArray();
        _comparison = comparison;
    }

    // Scan the input span and write every hit into the caller-provided buffer.
    // Advancing by one character after a hit keeps overlapping matches visible.
    // Extra matches are ignored once the destination span is full so the search
    // itself can stay allocation-free.
    public int FindAll(ReadOnlySpan<char> text, Span<MatchResult> destination)
    {
        int written = 0;

        for (int patternIndex = 0; patternIndex < _patterns.Length; patternIndex++)
        {
            ReadOnlySpan<char> pattern = _patterns[patternIndex].AsSpan();
            int start = 0;

            while (start <= text.Length - pattern.Length)
            {
                int index = text[start..].IndexOf(pattern, _comparison);
                if (index < 0)
                    break;

                int absoluteIndex = start + index;
                // Store coordinates only; the caller can slice the original text later.
                if (written < destination.Length)
                {
                    destination[written++] = new MatchResult(patternIndex, absoluteIndex, pattern.Length);
                }

                // Move forward by one character so overlapping matches are still discovered.
                start = absoluteIndex + 1;
            }
        }

        return written;
    }

    // Expose the single-pattern fast path when callers only need one match.
    public int FindFirst(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        return text.IndexOf(pattern, _comparison);
    }
}

[MemoryDiagnoser]
// Compare the custom span-based scanner with a straightforward IndexOf loop so
// the benchmark focuses on allocation behavior and repeated pattern scans.
public class StringSearcherBenchmarks
{
    private string _text = null!;
    private StringSearcher _searcher = null!;
    private string[] _patterns = null!;
    private MatchResult[] _results = null!;

    [GlobalSetup]
    public void Setup()
    {
        _patterns = ["alpha", "gamma", "omega", "delta"];
        _searcher = new StringSearcher(_patterns, StringComparison.OrdinalIgnoreCase);
        _results = new MatchResult[2048];

        var lines = new List<string>(2000);
        for (int i = 0; i < 2000; i++)
        {
            lines.Add($"Line {i}: alpha beta gamma delta epsilon omega ALPHA GAMMA");
        }
        _text = string.Join('\n', lines);
    }

    [Benchmark(Baseline = true)]
    public int StringIndexOfLoop()
    {
        int total = 0;
        foreach (string pattern in _patterns)
        {
            int start = 0;
            while (start < _text.Length)
            {
                int idx = _text.IndexOf(pattern, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;

                total++;
                start = idx + 1;
            }
        }
        return total;
    }

    [Benchmark]
    public int SpanSearcher()
    {
        return _searcher.FindAll(_text.AsSpan(), _results);
    }
}
