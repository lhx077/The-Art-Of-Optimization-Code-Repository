// ---------------------------------------------------------------------------
// Exercise 06 — Performance Baseline and Automated Monitoring
// ---------------------------------------------------------------------------
// Performance baselines serve as the reference point against which future
// measurements are compared. Without a recorded baseline, it is impossible to
// determine whether a code change improved, degraded, or had no effect on
// runtime behavior. By exporting BenchmarkDotNet results to JSON, we create
// machine-readable snapshots that can be compared across commits, builds, or
// time periods. This exercise demonstrates how to establish, export, and
// compare performance baselines programmatically.
//
// The benchmark suite below covers five representative workload patterns that
// appear in virtually every non-trivial .NET application: CPU-bound hashing,
// memory-intensive sorting, string processing, hash-table operations, and
// JSON serialization. Together, these benchmarks exercise the CPU pipeline,
// memory allocator, garbage collector, and System.Text.Json serializer —
// providing a broad cross-section of runtime performance characteristics.
// ---------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Exercise06.PerformanceBaseline;

// ---------------------------------------------------------------------------
// Data model for the serialization benchmark. Using a record class gives us
// value-based equality and a concise declaration while remaining a reference
// type suitable for JSON round-tripping. The structure is intentionally
// non-trivial — nested collections, mixed value/reference types, and a
// DateTime field — to reflect the kind of payload a typical web service
// serializes on every request.
// ---------------------------------------------------------------------------
public record Order(
    int Id,
    string CustomerName,
    List<OrderItem> Items,
    decimal Total,
    DateTime CreatedAt);

public record OrderItem(
    string ProductName,
    int Quantity,
    decimal UnitPrice);

// ---------------------------------------------------------------------------
// The [MemoryDiagnoser] attribute instruments every benchmark to report heap
// allocations per invocation. The JSON and GitHub Markdown exporters write
// results to the BenchmarkDotNet.Artifacts/ directory in formats suitable for
// automated comparison (JSON) and human review (Markdown).
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[MarkdownExporterAttribute.GitHub]
public class BaselineBenchmarks
{
    // -----------------------------------------------------------------------
    // Shared state initialized once before all iterations. Separating setup
    // from measurement ensures that we benchmark only the operation of
    // interest, not the cost of constructing test data.
    // -----------------------------------------------------------------------
    private byte[] _dataToHash = null!;
    private int[] _unsortedArray = null!;
    private string[] _dateStrings = null!;
    private string[] _dictionaryKeys = null!;
    private string _orderJson = null!;
    private Order _order = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [GlobalSetup]
    public void Setup()
    {
        // 1 KB payload for hashing — small enough to fit in L1 cache,
        // isolating CPU throughput from memory bandwidth effects.
        _dataToHash = new byte[1024];
        Random.Shared.NextBytes(_dataToHash);

        // 10,000-element array for sorting — large enough to exercise the
        // allocator (Array.Copy for the clone) and the O(n log n) sort path.
        _unsortedArray = new int[10_000];
        for (int i = 0; i < _unsortedArray.Length; i++)
            _unsortedArray[i] = Random.Shared.Next();

        // 1,000 date strings in ISO 8601 format for parse-and-format cycles.
        _dateStrings = new string[1_000];
        var baseDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < _dateStrings.Length; i++)
            _dateStrings[i] = baseDate.AddMinutes(i * 37).ToString("O");

        // 5,000 unique keys for dictionary insert/lookup cycles.
        _dictionaryKeys = new string[5_000];
        for (int i = 0; i < _dictionaryKeys.Length; i++)
            _dictionaryKeys[i] = $"key-{i:D6}";

        // A representative Order object for serialization benchmarks.
        _order = new Order(
            Id: 42,
            CustomerName: "Jane Doe",
            Items: new List<OrderItem>
            {
                new("Widget A", 3, 19.99m),
                new("Widget B", 1, 49.95m),
                new("Widget C", 7, 4.50m),
                new("Widget D", 2, 129.00m),
                new("Widget E", 5, 9.99m)
            },
            Total: 439.27m,
            CreatedAt: DateTime.UtcNow);

        _orderJson = JsonSerializer.Serialize(_order, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Benchmark 1: CPU-bound — compute SHA256 hash of a 1 KB byte array.
    //
    // This benchmark isolates pure CPU work. SHA256 is a compute-intensive
    // cryptographic function that processes data in 64-byte blocks through
    // 64 rounds of bitwise operations per block. The 1 KB input fits
    // comfortably in L1 cache, so memory latency is not a factor — the
    // measured time reflects raw instruction throughput. No heap allocation
    // occurs beyond the 32-byte hash result, making this an ideal baseline
    // for CPU-bound performance tracking.
    // -----------------------------------------------------------------------
    [Benchmark(Description = "SHA256 Hash (1 KB)")]
    public byte[] CpuBound_Sha256Hash()
    {
        return SHA256.HashData(_dataToHash);
    }

    // -----------------------------------------------------------------------
    // Benchmark 2: Memory-bound — sort a copy of a 10,000-element array.
    //
    // This benchmark exercises both the memory allocator and memory bandwidth.
    // Each invocation clones the unsorted array (a 40 KB allocation on the
    // small object heap) and then sorts it in place using Array.Sort's
    // introsort implementation. The clone ensures that every iteration starts
    // from the same unsorted state, providing repeatable measurements. The
    // sort itself performs O(n log n) comparisons and swaps, touching memory
    // in patterns that stress the CPU cache hierarchy.
    // -----------------------------------------------------------------------
    [Benchmark(Description = "Array Sort (10K elements)")]
    public int[] MemoryBound_ArraySort()
    {
        int[] copy = (int[])_unsortedArray.Clone();
        Array.Sort(copy);
        return copy;
    }

    // -----------------------------------------------------------------------
    // Benchmark 3: String processing — parse and format 1,000 date strings.
    //
    // Date parsing and formatting is one of the most common string-processing
    // patterns in real-world applications (log analysis, API responses, report
    // generation). Each iteration parses 1,000 ISO 8601 strings into DateTime
    // values and formats them back into a different representation. This
    // exercises the string allocator heavily — each ToString call produces a
    // new string on the heap — making it a useful proxy for allocation-
    // intensive workloads.
    // -----------------------------------------------------------------------
    [Benchmark(Description = "Date Parse+Format (1K strings)")]
    public string StringProcessing_DateParseFormat()
    {
        string last = string.Empty;
        for (int i = 0; i < _dateStrings.Length; i++)
        {
            DateTime dt = DateTime.Parse(_dateStrings[i]);
            last = dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        return last;
    }

    // -----------------------------------------------------------------------
    // Benchmark 4: Collection operations — Dictionary insert + lookup cycle.
    //
    // This benchmark measures the combined cost of hash computation, bucket
    // allocation, and key lookup across 5,000 entries. The dictionary is
    // constructed from scratch on each invocation (exercising the allocator
    // and internal resize logic), then every key is looked up to exercise
    // the hash-based retrieval path. This pattern mirrors real-world caching
    // and indexing scenarios where dictionaries are built and queried within
    // a single request or batch operation.
    // -----------------------------------------------------------------------
    [Benchmark(Description = "Dictionary Insert+Lookup (5K)")]
    public int CollectionOperations_DictionaryInsertLookup()
    {
        var dict = new Dictionary<string, int>(_dictionaryKeys.Length);

        for (int i = 0; i < _dictionaryKeys.Length; i++)
            dict[_dictionaryKeys[i]] = i;

        int sum = 0;
        for (int i = 0; i < _dictionaryKeys.Length; i++)
            sum += dict[_dictionaryKeys[i]];

        return sum;
    }

    // -----------------------------------------------------------------------
    // Benchmark 5: Serialization — JSON serialize and deserialize a complex
    // object using System.Text.Json.
    //
    // JSON serialization is the dominant I/O-adjacent pattern in modern web
    // services: every API response typically involves serializing a domain
    // object to JSON, and every incoming request involves deserializing the
    // payload. System.Text.Json uses a Utf8JsonWriter internally, which
    // allocates intermediate buffers. The round-trip (serialize then
    // deserialize) exercises both code paths and reveals the allocation
    // profile of the serializer. Tracking this benchmark over time helps
    // detect regressions introduced by changes to the data model (e.g.,
    // adding fields, nesting deeper structures).
    // -----------------------------------------------------------------------
    [Benchmark(Description = "JSON Round-Trip (Order)")]
    public Order Serialization_JsonRoundTrip()
    {
        string json = JsonSerializer.Serialize(_order, JsonOptions);
        return JsonSerializer.Deserialize<Order>(json, JsonOptions)!;
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<BaselineBenchmarks>();
    }
}

// ---------------------------------------------------------------------------
// Usage Guide
// ---------------------------------------------------------------------------
//
// HOW TO RUN:
//   dotnet run -c Release
//
//   BenchmarkDotNet requires a Release build to produce accurate results.
//   Running in Debug mode disables JIT optimizations and yields misleading
//   numbers. The framework will warn you if it detects a Debug configuration.
//
// WHERE RESULTS ARE SAVED:
//   After execution completes, BenchmarkDotNet writes all results to the
//   BenchmarkDotNet.Artifacts/ directory relative to the working directory.
//   The [JsonExporterAttribute.Full] attribute produces a JSON file containing
//   every measured data point (mean, median, standard deviation, percentiles,
//   memory allocation counts). The [MarkdownExporterAttribute.GitHub] attribute
//   produces a GitHub-flavored Markdown table suitable for pasting into pull
//   request descriptions or documentation.
//
// HOW TO USE THE COMPARISON SCRIPT:
//   1. Run the benchmarks and copy the JSON result file as your baseline:
//        cp BenchmarkDotNet.Artifacts/results/*-report-full.json baseline.json
//   2. Make code changes, then run the benchmarks again.
//   3. Run the comparison script:
//        pwsh scripts/compare-baselines.ps1 `
//            -BaselinePath baseline.json `
//            -CurrentPath BenchmarkDotNet.Artifacts/results/*-report-full.json
//   4. The script outputs a table showing each benchmark's baseline time,
//      current time, percentage change, and a status indicator (OK or
//      REGRESSION). It exits with code 1 if any benchmark regressed by
//      more than the threshold (default 20%), making it suitable as a CI
//      pipeline gate.
//
// CI INTEGRATION CONSIDERATIONS:
//   - Hardware consistency: Run benchmarks on dedicated, isolated machines
//     (or at minimum, on the same CI runner type) to avoid variance caused
//     by noisy neighbors or differing CPU architectures.
//   - Thermal throttling: Ensure the CI runner is not thermally constrained.
//     Cloud VMs with burstable CPU credits (e.g., AWS T-series) can produce
//     wildly inconsistent results once credits are exhausted.
//   - Process isolation: Close unnecessary background processes and disable
//     automatic updates on benchmark runners. BenchmarkDotNet's built-in
//     outlier detection helps, but cannot fully compensate for a noisy host.
//   - Statistical significance: Consider increasing the iteration count
//     (via [MinIterationCount] / [MaxIterationCount]) in CI to improve
//     confidence in the measured means.
//   - Threshold tuning: The default 20% regression threshold is deliberately
//     generous to avoid false positives. For latency-sensitive code paths,
//     consider tightening it to 10% or even 5%.
//
// WHEN TO UPDATE THE BASELINE:
//   - After intentional performance changes: If you deliberately trade CPU
//     time for lower memory usage (or vice versa), update the baseline to
//     reflect the new expected behavior.
//   - After infrastructure changes: Upgrading the .NET runtime, changing the
//     target framework, or migrating to different hardware all warrant a
//     fresh baseline.
//   - Never to mask regressions: If a benchmark regresses unexpectedly, the
//     correct response is to investigate and fix the regression — not to
//     silently update the baseline. Baselines should be updated through a
//     deliberate, reviewed process (e.g., a dedicated pull request).
// ---------------------------------------------------------------------------
