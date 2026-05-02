// =============================================================================
// Exercise 05 — String Intern Analyzer
// The Art of Optimization, Chapter 5: Strings and Text Processing
//
// A diagnostic tool for analyzing string interning behavior and its memory
// impact. Demonstrates when interning helps, when it hurts, and a safe
// pooling alternative.
//
// How the CLR intern table works:
//   The CLR maintains a global hash table (the "intern table") that maps
//   string content to a single canonical instance. When you call
//   string.Intern(s), the runtime looks up s's content in this table:
//     - If found, it returns the existing reference (the canonical copy).
//     - If not found, it adds s (or a copy) to the table and returns it.
//   string.IsInterned(s) performs the lookup WITHOUT adding — it returns
//   the canonical instance if present, or null if not.
//
//   String literals are interned at compile time: the C# compiler stores
//   them in the assembly's metadata (#US heap), and the CLR interns them
//   when the assembly loads. This means two identical literals anywhere
//   in the program share the same object reference.
//
//   CRITICAL: Interned strings are NEVER garbage collected. The intern
//   table acts as a GC root — every string added to it lives for the
//   entire lifetime of the AppDomain. This is fine for a small, fixed
//   set of values, but catastrophic for high-cardinality data.
//
// When to intern:
//   - Small, fixed sets of frequently compared strings (enum names,
//     HTTP headers, status codes, column names).
//   - Strings that appear thousands of times and are long-lived anyway.
//
// When NOT to intern:
//   - User input, GUIDs, timestamps, URLs, file paths — anything with
//     high cardinality or unbounded growth.
//   - Short-lived strings that would otherwise be collected promptly.
//
// The StringPool pattern:
//   For temporary deduplication (e.g., parsing a CSV where many rows
//   share the same city name), use a Dictionary-based pool instead of
//   string.Intern(). The pool deduplicates references just like interning,
//   but you can clear it when you're done, allowing the GC to reclaim
//   all pooled strings.
// =============================================================================

using System.Collections.Generic;
using System.Text;

// ---------------------------------------------------------------------------
// Top-level statements — run all demonstrations in sequence
// ---------------------------------------------------------------------------

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║        String Intern Analyzer — Diagnostic Tool             ║");
Console.WriteLine("║   The Art of Optimization, Chapter 5: Strings & Text        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Demo 1: Literal interning behavior
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  Demo 1: Literal Interning Behavior");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
InternAnalyzer.DemoLiteralInterning();

Console.WriteLine();

// Demo 2: Duplicate detection and memory savings
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  Demo 2: Duplicate Detection & Memory Savings");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
InternAnalyzer.DemoDuplicateDetection();

Console.WriteLine();

// Demo 3: Intern leak warning with unique strings
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  Demo 3: Intern Leak Warning (Unique Strings)");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
InternAnalyzer.DemoInternLeakWarning();

Console.WriteLine();

// Demo 4: Pooled alternative that avoids the leak
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  Demo 4: StringPool — Safe Pooled Alternative");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
InternAnalyzer.DemoPooledAlternative();

Console.WriteLine();

// Summary
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                        Summary                              ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine("║  1. String literals are automatically interned by the CLR.  ║");
Console.WriteLine("║  2. string.Intern() deduplicates but NEVER releases memory. ║");
Console.WriteLine("║  3. Only intern small, fixed, long-lived string sets.       ║");
Console.WriteLine("║  4. For temporary dedup, use StringPool (Dictionary-based). ║");
Console.WriteLine("║  5. NEVER intern user input, GUIDs, or high-cardinality     ║");
Console.WriteLine("║     data — it causes permanent memory leaks.                ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

// =============================================================================
// InternAnalyzer — static class containing all demonstration methods
// =============================================================================
static class InternAnalyzer
{
    /// <summary>
    /// Demonstrates that string literals are automatically interned by the CLR.
    /// Two identical literals share the same reference. Strings built at runtime
    /// (via new string(), StringBuilder, concatenation) are NOT interned unless
    /// you explicitly call string.Intern().
    /// </summary>
    public static void DemoLiteralInterning()
    {
        // Two identical string literals — the compiler stores one copy in the
        // assembly metadata (#US heap), and the CLR interns it on load.
        string literal1 = "Hello, Interning!";
        string literal2 = "Hello, Interning!";

        Console.WriteLine($"  literal1 = \"{literal1}\"");
        Console.WriteLine($"  literal2 = \"{literal2}\"");
        Console.WriteLine($"  ReferenceEquals(literal1, literal2) = {object.ReferenceEquals(literal1, literal2)}");
        Console.WriteLine("  → Identical literals share the same object reference (interned at compile time).");
        Console.WriteLine();

        // Build the same content at runtime — this creates a NEW object on the heap.
        // The CLR does NOT automatically intern runtime-constructed strings.
        string runtime = new StringBuilder("Hello, ").Append("Interning!").ToString();

        Console.WriteLine($"  runtime   = \"{runtime}\"");
        Console.WriteLine($"  literal1 == runtime              = {literal1 == runtime}");
        Console.WriteLine($"  ReferenceEquals(literal1, runtime) = {object.ReferenceEquals(literal1, runtime)}");
        Console.WriteLine("  → Same content, but different object — runtime strings are NOT auto-interned.");
        Console.WriteLine();

        // string.IsInterned() checks the intern table WITHOUT adding.
        // Because "Hello, Interning!" is already interned (it's a literal),
        // IsInterned returns the canonical reference.
        string? isInternedResult = string.IsInterned(runtime);
        Console.WriteLine($"  string.IsInterned(runtime) is null? {isInternedResult is null}");
        Console.WriteLine("  → IsInterned finds the literal's entry, confirming the content exists in the table.");
        Console.WriteLine();

        // Explicitly intern the runtime string — now it maps to the same canonical instance.
        string interned = string.Intern(runtime);

        Console.WriteLine($"  interned = string.Intern(runtime)");
        Console.WriteLine($"  ReferenceEquals(literal1, interned) = {object.ReferenceEquals(literal1, interned)}");
        Console.WriteLine("  → After Intern(), the runtime string resolves to the literal's canonical reference.");
    }

    /// <summary>
    /// Simulates a scenario with massive string duplication — 100K strings drawn
    /// from only 100 unique values (like status codes). Measures memory before
    /// and after interning to show the deduplication benefit.
    /// </summary>
    public static void DemoDuplicateDetection()
    {
        const int totalStrings = 100_000;
        string[] uniqueValues =
        [
            "OK", "ERROR", "PENDING", "TIMEOUT", "CANCELLED",
            "RETRY", "SUCCESS", "FAILURE", "UNKNOWN", "PROCESSING",
            "QUEUED", "RUNNING", "COMPLETED", "SKIPPED", "BLOCKED",
            "APPROVED", "REJECTED", "EXPIRED", "ARCHIVED", "DELETED",
            "ACTIVE", "INACTIVE", "SUSPENDED", "LOCKED", "UNLOCKED",
            "OPEN", "CLOSED", "MERGED", "DRAFT", "PUBLISHED",
            "VERIFIED", "UNVERIFIED", "BANNED", "WARNED", "MUTED",
            "ONLINE", "OFFLINE", "AWAY", "BUSY", "INVISIBLE",
            "LOW", "MEDIUM", "HIGH", "CRITICAL", "BLOCKER",
            "INFO", "DEBUG", "WARN", "FATAL", "TRACE",
            "GET", "POST", "PUT", "DELETE", "PATCH",
            "HEAD", "OPTIONS", "CONNECT", "UPGRADE", "WEBSOCKET",
            "JSON", "XML", "HTML", "TEXT", "BINARY",
            "UTF8", "ASCII", "BASE64", "GZIP", "DEFLATE",
            "TCP", "UDP", "HTTP", "HTTPS", "WSS",
            "AUTH", "ANON", "TOKEN", "SESSION", "COOKIE",
            "READ", "WRITE", "EXEC", "ADMIN", "GUEST",
            "CACHE_HIT", "CACHE_MISS", "STALE", "FRESH", "REVALIDATE",
            "NORTH", "SOUTH", "EAST", "WEST", "CENTER",
            "RED", "GREEN", "BLUE", "YELLOW", "WHITE"
        ];

        var random = new Random(42);

        // Phase 1: Create 100K strings WITHOUT interning.
        // Each new string(...) call creates a distinct heap object even if the
        // content matches an existing string.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memBefore = GC.GetTotalMemory(true);

        var strings = new string[totalStrings];
        for (int i = 0; i < totalStrings; i++)
        {
            // Force a new heap allocation by copying via char array — avoids
            // the compiler folding these into literals.
            string original = uniqueValues[random.Next(uniqueValues.Length)];
            strings[i] = new string(original.AsSpan());
        }

        long memAfterAlloc = GC.GetTotalMemory(true);
        long allocCost = memAfterAlloc - memBefore;

        Console.WriteLine($"  Created {totalStrings:N0} strings from {uniqueValues.Length} unique values.");
        Console.WriteLine($"  Memory after allocation: ~{allocCost:N0} bytes");
        Console.WriteLine();

        // Phase 2: Intern all strings — duplicates now share one reference.
        for (int i = 0; i < totalStrings; i++)
        {
            strings[i] = string.Intern(strings[i]);
        }

        // Force GC to reclaim the now-unreferenced duplicate objects.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memAfterIntern = GC.GetTotalMemory(true);
        long internCost = memAfterIntern - memBefore;

        Console.WriteLine($"  Memory after interning + GC: ~{internCost:N0} bytes");
        Console.WriteLine($"  Estimated savings: ~{allocCost - internCost:N0} bytes");
        Console.WriteLine("  → Interning eliminated duplicate heap objects for repeated status codes.");
        Console.WriteLine("  → This is the ideal use case: small, fixed set of values appearing many times.");
    }

    /// <summary>
    /// Demonstrates the danger of interning unique/high-cardinality strings.
    /// Interned strings become GC roots — they are NEVER collected, even after
    /// all other references are gone. This effectively creates a memory leak.
    /// </summary>
    public static void DemoInternLeakWarning()
    {
        const int count = 100_000;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memBefore = GC.GetTotalMemory(true);

        // Intern 100K unique strings (GUIDs). Each one is added to the global
        // intern table and will NEVER be released.
        for (int i = 0; i < count; i++)
        {
            string unique = string.Intern(Guid.NewGuid().ToString());
            // We don't even keep a reference — but the intern table does.
            _ = unique;
        }

        long memAfterIntern = GC.GetTotalMemory(true);
        long internedBytes = memAfterIntern - memBefore;

        Console.WriteLine($"  Interned {count:N0} unique GUID strings.");
        Console.WriteLine($"  Memory consumed: ~{internedBytes:N0} bytes");
        Console.WriteLine();

        // Now try to reclaim memory — it won't work.
        Console.WriteLine("  Calling GC.Collect() to attempt reclamation...");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memAfterGC = GC.GetTotalMemory(true);

        Console.WriteLine($"  Memory after GC: ~{memAfterGC - memBefore:N0} bytes (still held)");
        Console.WriteLine();
        Console.WriteLine("  ⚠ WARNING: Interned strings are GC roots. They live for the entire");
        Console.WriteLine("    lifetime of the process. Interning high-cardinality data (GUIDs,");
        Console.WriteLine("    timestamps, user input, URLs) causes a permanent memory leak.");
        Console.WriteLine("    Only intern small, fixed, well-known string sets.");
    }

    /// <summary>
    /// Shows that a Dictionary-based StringPool provides the same deduplication
    /// benefit as interning, but without the permanent memory leak. The pool
    /// can be disposed when no longer needed, allowing the GC to reclaim all
    /// pooled strings.
    /// </summary>
    public static void DemoPooledAlternative()
    {
        const int totalStrings = 100_000;
        string[] cities = ["Beijing", "Shanghai", "Guangzhou", "Shenzhen", "Chengdu",
                           "Hangzhou", "Wuhan", "Nanjing", "Chongqing", "Tianjin",
                           "Suzhou", "Xi'an", "Zhengzhou", "Changsha", "Qingdao",
                           "Dalian", "Xiamen", "Kunming", "Hefei", "Fuzhou"];

        var random = new Random(42);

        // Phase 1: Use StringPool for deduplication.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memBefore = GC.GetTotalMemory(true);

        var pooledStrings = new string[totalStrings];
        using (var pool = new StringPool())
        {
            for (int i = 0; i < totalStrings; i++)
            {
                // Simulate parsing — create a fresh string each time.
                string city = cities[random.Next(cities.Length)];
                string parsed = new string(city.AsSpan());
                pooledStrings[i] = pool.GetOrAdd(parsed);
            }

            long memWithPool = GC.GetTotalMemory(true);

            Console.WriteLine($"  Pooled {totalStrings:N0} strings from {cities.Length} unique cities.");
            Console.WriteLine($"  Pool contains {pool.Count} unique entries.");
            Console.WriteLine($"  Estimated bytes saved by dedup: ~{pool.EstimatedBytesSaved:N0} bytes");
            Console.WriteLine($"  Memory used with pool: ~{memWithPool - memBefore:N0} bytes");
            Console.WriteLine();

            // Phase 2: Dispose the pool — all pooled strings become eligible for GC
            // (assuming no other references hold them).
            Console.WriteLine("  Disposing the pool...");
        }

        // Clear our references too, so the strings can actually be collected.
        Array.Clear(pooledStrings);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memAfterDispose = GC.GetTotalMemory(true);

        Console.WriteLine($"  Memory after pool disposal + GC: ~{memAfterDispose - memBefore:N0} bytes");
        Console.WriteLine();
        Console.WriteLine("  → Unlike string.Intern(), the StringPool allows full memory reclamation.");
        Console.WriteLine("  → Use StringPool for temporary dedup (parsing, batch processing, imports).");
        Console.WriteLine("  → Use string.Intern() only for permanent, small, fixed string sets.");
    }
}

// =============================================================================
// StringPool — A safe, disposable alternative to string.Intern()
//
// This class provides the same deduplication benefit as the CLR intern table:
// multiple references to identical string content are collapsed into a single
// object. The key difference is that StringPool is scoped and disposable —
// when you're done, call Dispose() to clear the internal dictionary, making
// all pooled strings eligible for garbage collection.
//
// Use this instead of string.Intern() when:
//   - You're processing a batch of data with many duplicates (CSV parsing,
//     log analysis, database result sets).
//   - The deduplicated strings are only needed temporarily.
//   - The set of unique values is large or unbounded.
//
// How it works:
//   Internally, a Dictionary<string, string> maps each unique string value
//   to itself. When GetOrAdd() is called, it checks the dictionary:
//     - If the key exists, it returns the existing reference (dedup hit).
//     - If not, it adds the string and returns it (dedup miss).
//   The EstimatedBytesSaved property tracks how many bytes were saved by
//   returning existing references instead of keeping duplicates. The estimate
//   uses (26 + length * 2) bytes per string object on 64-bit .NET — 26 bytes
//   for the object header, method table pointer, length field, and null
//   terminator, plus 2 bytes per char (UTF-16).
// =============================================================================
class StringPool : IDisposable
{
    private Dictionary<string, string>? _pool = new();
    private long _estimatedBytesSaved;

    /// <summary>
    /// Returns the number of unique strings currently in the pool.
    /// </summary>
    public int Count => _pool?.Count ?? 0;

    /// <summary>
    /// Estimated bytes saved by deduplication. Each time a duplicate is found,
    /// we add the estimated size of the string object that was avoided.
    /// </summary>
    public long EstimatedBytesSaved => _estimatedBytesSaved;

    /// <summary>
    /// If <paramref name="value"/> is already in the pool, returns the existing
    /// canonical reference. Otherwise, adds it to the pool and returns it.
    /// This is semantically equivalent to string.Intern() but scoped to this pool.
    /// </summary>
    public string GetOrAdd(string value)
    {
        ObjectDisposedException.ThrowIf(_pool is null, this);

        if (_pool.TryGetValue(value, out string? existing))
        {
            // Dedup hit — the caller's copy can be collected; we return the pooled one.
            // Track the estimated memory savings: object overhead + char data.
            _estimatedBytesSaved += EstimateStringSize(value);
            return existing;
        }

        // Dedup miss — this is a new unique value; add it to the pool.
        _pool[value] = value;
        return value;
    }

    /// <summary>
    /// Clears the internal dictionary, releasing all references to pooled strings.
    /// After disposal, those strings become eligible for garbage collection
    /// (assuming no other references exist). This is the key advantage over
    /// string.Intern(), which never releases its entries.
    /// </summary>
    public void Dispose()
    {
        _pool?.Clear();
        _pool = null;
    }

    /// <summary>
    /// Estimates the heap size of a string object on 64-bit .NET.
    /// Layout: object header (8) + method table ptr (8) + _stringLength (4)
    /// + chars (length * 2) + null terminator (2) = 22 + length * 2,
    /// rounded up to the nearest 8-byte boundary (object alignment).
    /// We use a simplified estimate of 26 + length * 2 for clarity.
    /// </summary>
    private static long EstimateStringSize(string s) => 26L + s.Length * 2L;
}
