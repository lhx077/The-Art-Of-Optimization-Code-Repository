// ============================================================================
// Exercise 02 — INI Configuration Parser
// ============================================================================
// Uses a ref struct parser over ReadOnlySpan<char>. Parsed keys and values are
// stored as ReadOnlyMemory<char> slices into the original source string to
// avoid creating new strings. Supports sections and comments.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<IniParserBenchmarks>();
    return;
}

string sample = """
; Global comment
[database]
host = localhost
port = 5432

[features]
flagA = true
flagB = false
# Another comment
""";

var document = IniDocument.Parse(sample);
Console.WriteLine("=== INI Parser Demo ===");
foreach (var entry in document.Entries)
{
    Console.WriteLine($"[{entry.Section.Span}] {entry.Key.Span} = {entry.Value.Span}");
}

// IniEntry stores slices into the original source string so section names,
// keys, and values can be reported without allocating replacement strings.
public readonly record struct IniEntry(
    ReadOnlyMemory<char> Section,
    ReadOnlyMemory<char> Key,
    ReadOnlyMemory<char> Value);

// IniDocument keeps the original source alive because every parsed entry points
// back into that string through ReadOnlyMemory<char> slices.
public sealed class IniDocument
{
    public string Source { get; }
    public IReadOnlyList<IniEntry> Entries { get; }

    private IniDocument(string source, List<IniEntry> entries)
    {
        Source = source;
        Entries = entries;
    }

    // Parse incrementally so the sample can demonstrate the intended hot path clearly.
    public static IniDocument Parse(string source)
    {
        var parser = new IniParser(source);
        return new IniDocument(source, parser.ParseAll());
    }
}

// IniParser is a ref struct so the parser itself stays stack-confined while it
// walks spans that refer directly to the source text.
public ref struct IniParser
{
    private ReadOnlySpan<char> _remaining;
    private readonly string _source;
    private ReadOnlyMemory<char> _currentSection;

    public IniParser(string source)
    {
        _source = source;
        _remaining = source.AsSpan();
        _currentSection = ReadOnlyMemory<char>.Empty;
    }

    // Walk the source text incrementally so sections and key-value pairs stay zero-copy.
    public List<IniEntry> ParseAll()
    {
        var entries = new List<IniEntry>();

        while (TryReadLine(out var currentLine, out int startIndex))
        {
            // Trim only for parsing decisions; absolute offsets are still computed
            // from the original line start plus the number of leading spaces removed.
            ReadOnlySpan<char> line = currentLine.Trim();
            if (line.IsEmpty || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                // Compute the section slice from the original line rather than
                // searching by content, so repeated text cannot shift the offset.
                int lineStartOffset = currentLine.Length - currentLine.TrimStart().Length;
                int sectionStart = startIndex + lineStartOffset + 1;
                int sectionLength = line.Length - 2;
                _currentSection = _source.AsMemory(sectionStart, sectionLength);
                continue;
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            ReadOnlySpan<char> rawKey = line[..equalsIndex];
            ReadOnlySpan<char> rawValue = line[(equalsIndex + 1)..];
            ReadOnlySpan<char> keySpan = rawKey.Trim();
            ReadOnlySpan<char> valueSpan = rawValue.Trim();

            // Translate the trimmed spans back into absolute positions by counting
            // how many leading spaces were removed from each side. This avoids the
            // hidden bug where IndexOf could bind the value slice to the key when
            // both pieces of text are identical.
            int lineStartOffsetForTrimmed = currentLine.Length - currentLine.TrimStart().Length;
            int lineOffset = startIndex + lineStartOffsetForTrimmed;
            int keyStartOffset = rawKey.Length - rawKey.TrimStart().Length;
            int valueStartOffset = (equalsIndex + 1) + (rawValue.Length - rawValue.TrimStart().Length);
            int keyStart = lineOffset + keyStartOffset;
            int valueStart = lineOffset + valueStartOffset;

            entries.Add(new IniEntry(
                _currentSection,
                _source.AsMemory(keyStart, keySpan.Length),
                _source.AsMemory(valueStart, valueSpan.Length)));
        }

        return entries;
    }

    // Split the next logical line without allocating intermediate strings.
    // Returning a span plus its absolute starting index lets ParseAll map trimmed
    // slices back to the original source string later.
    private bool TryReadLine(out ReadOnlySpan<char> line, out int startIndex)
    {
        int consumed = _source.Length - _remaining.Length;
        startIndex = consumed;

        if (_remaining.IsEmpty)
        {
            line = default;
            return false;
        }

        int newLineIndex = _remaining.IndexOf('\n');
        if (newLineIndex < 0)
        {
            // The final line has no trailing newline, so consume the whole remainder.
            line = TrimCr(_remaining);
            _remaining = ReadOnlySpan<char>.Empty;
            return true;
        }

        // Advance past the newline so the next call starts at the following line.
        line = TrimCr(_remaining[..newLineIndex]);
        _remaining = _remaining[(newLineIndex + 1)..];
        return true;
    }

    private static ReadOnlySpan<char> TrimCr(ReadOnlySpan<char> span)
        => !span.IsEmpty && span[^1] == '\r' ? span[..^1] : span;
}

[MemoryDiagnoser]
// Grow the input to many sections so the benchmark highlights parsing cost rather
// than setup overhead from the small demo string above.
public class IniParserBenchmarks
{
    private string _largeIni = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lines = new List<string>(80_000);
        for (int s = 0; s < 400; s++)
        {
            lines.Add($"[section{s}]");
            for (int i = 0; i < 200; i++)
            {
                lines.Add($"key{i} = value_{s}_{i}");
            }
            lines.Add("; comment");
        }
        _largeIni = string.Join('\n', lines);
    }

    [Benchmark]
    public int ParseLargeIni()
    {
        return IniDocument.Parse(_largeIni).Entries.Count;
    }
}
