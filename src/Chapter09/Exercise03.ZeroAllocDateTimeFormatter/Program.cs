// ============================================================================
// Exercise 03 — Zero-Allocation DateTime Formatter
// ============================================================================
// Implements a DateTimeFormatter that formats DateTime values into
// Span<char> buffers with zero intermediate heap allocations.
//
// Features:
//   - ISO 8601 format: "2024-01-15T09:30:45Z"
//   - Custom format support: "yyyy-MM-dd", "HH:mm:ss", "yyyy/MM/dd HH:mm:ss"
//   - Implements ISpanFormattable for seamless integration with interpolated
//     string handlers and TryFormat patterns.
//   - Verified zero-allocation with BenchmarkDotNet MemoryDiagnoser.
//
// Run with --benchmark to execute the BenchmarkDotNet comparison.
// ============================================================================

using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<DateTimeFormatterBenchmarks>();
    return;
}

// --- Demonstration ---
Console.WriteLine("=== Zero-Allocation DateTime Formatter Demo ===");
Console.WriteLine();

var now = new DateTime(2024, 12, 25, 14, 30, 45, DateTimeKind.Utc);
var formatter = new ZeroAllocDateTime(now);
Span<char> buffer = stackalloc char[64];

// ISO 8601
formatter.TryFormatIso8601(buffer, out int written);
Console.WriteLine($"ISO 8601:         {buffer[..written]}");

// Date only
formatter.TryFormatDateOnly(buffer, out written);
Console.WriteLine($"Date only:        {buffer[..written]}");

// Time only
formatter.TryFormatTimeOnly(buffer, out written);
Console.WriteLine($"Time only:        {buffer[..written]}");

// Custom format
formatter.TryFormatCustom(buffer, "yyyy/MM/dd HH:mm:ss".AsSpan(), out written);
Console.WriteLine($"Custom format:    {buffer[..written]}");

// ISpanFormattable usage
Console.WriteLine($"ISpanFormattable: {formatter}");

Console.WriteLine();
Console.WriteLine("Run with --benchmark to compare allocation characteristics.");

// ============================================================================
// Zero-allocation DateTime formatting struct
// ============================================================================
public readonly struct ZeroAllocDateTime : ISpanFormattable
{
    private readonly DateTime _value;

    public ZeroAllocDateTime(DateTime value) => _value = value;

    // -----------------------------------------------------------------------
    // ISO 8601: "2024-12-25T14:30:45Z" (20 chars for UTC)
    // -----------------------------------------------------------------------
    public bool TryFormatIso8601(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;

        // "yyyy-MM-ddTHH:mm:ssZ" = 20 chars
        if (destination.Length < 20)
            return false;

        WriteYear(destination, _value.Year, ref charsWritten);
        destination[charsWritten++] = '-';
        WriteTwoDigits(destination, _value.Month, ref charsWritten);
        destination[charsWritten++] = '-';
        WriteTwoDigits(destination, _value.Day, ref charsWritten);
        destination[charsWritten++] = 'T';
        WriteTwoDigits(destination, _value.Hour, ref charsWritten);
        destination[charsWritten++] = ':';
        WriteTwoDigits(destination, _value.Minute, ref charsWritten);
        destination[charsWritten++] = ':';
        WriteTwoDigits(destination, _value.Second, ref charsWritten);
        destination[charsWritten++] = 'Z';

        return true;
    }

    // -----------------------------------------------------------------------
    // Date only: "2024-12-25" (10 chars)
    // -----------------------------------------------------------------------
    public bool TryFormatDateOnly(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (destination.Length < 10)
            return false;

        WriteYear(destination, _value.Year, ref charsWritten);
        destination[charsWritten++] = '-';
        WriteTwoDigits(destination, _value.Month, ref charsWritten);
        destination[charsWritten++] = '-';
        WriteTwoDigits(destination, _value.Day, ref charsWritten);

        return true;
    }

    // -----------------------------------------------------------------------
    // Time only: "14:30:45" (8 chars)
    // -----------------------------------------------------------------------
    public bool TryFormatTimeOnly(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (destination.Length < 8)
            return false;

        WriteTwoDigits(destination, _value.Hour, ref charsWritten);
        destination[charsWritten++] = ':';
        WriteTwoDigits(destination, _value.Minute, ref charsWritten);
        destination[charsWritten++] = ':';
        WriteTwoDigits(destination, _value.Second, ref charsWritten);

        return true;
    }

    // -----------------------------------------------------------------------
    // Custom format supporting: yyyy, MM, dd, HH, mm, ss and literal chars
    // -----------------------------------------------------------------------
    public bool TryFormatCustom(Span<char> destination, ReadOnlySpan<char> format, out int charsWritten)
    {
        charsWritten = 0;
        int fi = 0;

        while (fi < format.Length)
        {
            if (charsWritten >= destination.Length)
                return false;

            // Try to match known tokens
            if (fi + 4 <= format.Length && format[fi..].StartsWith("yyyy".AsSpan()))
            {
                if (destination.Length - charsWritten < 4) return false;
                WriteYear(destination, _value.Year, ref charsWritten);
                fi += 4;
            }
            else if (fi + 2 <= format.Length && format[fi..].StartsWith("MM".AsSpan()))
            {
                if (destination.Length - charsWritten < 2) return false;
                WriteTwoDigits(destination, _value.Month, ref charsWritten);
                fi += 2;
            }
            else if (fi + 2 <= format.Length && format[fi..].StartsWith("dd".AsSpan()))
            {
                if (destination.Length - charsWritten < 2) return false;
                WriteTwoDigits(destination, _value.Day, ref charsWritten);
                fi += 2;
            }
            else if (fi + 2 <= format.Length && format[fi..].StartsWith("HH".AsSpan()))
            {
                if (destination.Length - charsWritten < 2) return false;
                WriteTwoDigits(destination, _value.Hour, ref charsWritten);
                fi += 2;
            }
            else if (fi + 2 <= format.Length && format[fi..].StartsWith("mm".AsSpan()))
            {
                if (destination.Length - charsWritten < 2) return false;
                WriteTwoDigits(destination, _value.Minute, ref charsWritten);
                fi += 2;
            }
            else if (fi + 2 <= format.Length && format[fi..].StartsWith("ss".AsSpan()))
            {
                if (destination.Length - charsWritten < 2) return false;
                WriteTwoDigits(destination, _value.Second, ref charsWritten);
                fi += 2;
            }
            else
            {
                // Literal character pass-through
                destination[charsWritten++] = format[fi++];
            }
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // ISpanFormattable implementation
    // -----------------------------------------------------------------------
    public bool TryFormat(Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (format.IsEmpty || format.SequenceEqual("O".AsSpan()) || format.SequenceEqual("o".AsSpan()))
            return TryFormatIso8601(destination, out charsWritten);

        if (format.SequenceEqual("d".AsSpan()))
            return TryFormatDateOnly(destination, out charsWritten);

        if (format.SequenceEqual("t".AsSpan()))
            return TryFormatTimeOnly(destination, out charsWritten);

        return TryFormatCustom(destination, format, out charsWritten);
    }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Span<char> buf = stackalloc char[64];
        TryFormat(buf, out int written, format.AsSpan(), formatProvider);
        return buf[..written].ToString();
    }

    public override string ToString()
    {
        Span<char> buf = stackalloc char[20];
        TryFormatIso8601(buf, out int written);
        return buf[..written].ToString();
    }

    // -----------------------------------------------------------------------
    // Helpers — all operate directly on Span<char>, zero allocation
    // -----------------------------------------------------------------------
    private static void WriteYear(Span<char> dest, int year, ref int pos)
    {
        dest[pos++] = (char)('0' + year / 1000);
        dest[pos++] = (char)('0' + (year / 100) % 10);
        dest[pos++] = (char)('0' + (year / 10) % 10);
        dest[pos++] = (char)('0' + year % 10);
    }

    private static void WriteTwoDigits(Span<char> dest, int value, ref int pos)
    {
        dest[pos++] = (char)('0' + value / 10);
        dest[pos++] = (char)('0' + value % 10);
    }
}

// ============================================================================
// Benchmarks comparing zero-alloc formatting vs standard ToString
// ============================================================================
[MemoryDiagnoser]
public class DateTimeFormatterBenchmarks
{
    private DateTime _dateTime;
    private ZeroAllocDateTime _formatter;
    private char[] _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dateTime = new DateTime(2024, 12, 25, 14, 30, 45, DateTimeKind.Utc);
        _formatter = new ZeroAllocDateTime(_dateTime);
        _buffer = new char[64];
    }

    // --- ISO 8601 ---

    [Benchmark(Baseline = true)]
    public string Standard_ToString_O()
    {
        return _dateTime.ToString("O");
    }

    [Benchmark]
    public int ZeroAlloc_Iso8601()
    {
        _formatter.TryFormatIso8601(_buffer, out int written);
        return written;
    }

    // --- Date only ---

    [Benchmark]
    public string Standard_ToString_Date()
    {
        return _dateTime.ToString("yyyy-MM-dd");
    }

    [Benchmark]
    public int ZeroAlloc_DateOnly()
    {
        _formatter.TryFormatDateOnly(_buffer, out int written);
        return written;
    }

    // --- Custom format ---

    [Benchmark]
    public string Standard_ToString_Custom()
    {
        return _dateTime.ToString("yyyy/MM/dd HH:mm:ss");
    }

    [Benchmark]
    public int ZeroAlloc_Custom()
    {
        _formatter.TryFormatCustom(_buffer, "yyyy/MM/dd HH:mm:ss".AsSpan(), out int written);
        return written;
    }

    // --- Stackalloc path for full zero-alloc including buffer ---

    [Benchmark]
    public int ZeroAlloc_Stackalloc_Iso8601()
    {
        Span<char> buf = stackalloc char[20];
        _formatter.TryFormatIso8601(buf, out int written);
        return written;
    }
}
