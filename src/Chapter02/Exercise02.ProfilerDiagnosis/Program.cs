// ============================================================================
// Exercise 02 — Visual Studio Profiler Diagnosis: Word Frequency Counter
// ============================================================================
//
// Purpose:
//   This exercise teaches you to identify CPU hotspots using Visual Studio's
//   Performance Profiler (Debug > Performance Profiler > CPU Usage). We
//   intentionally introduce three common anti-patterns into a word frequency
//   counter, then show the corrected implementation side by side.
//
//   Run the program first to see the wall-clock difference, then attach the
//   VS Profiler to understand WHERE the time is spent and WHY.
//
// Anti-patterns demonstrated:
//   1. String concatenation with += in a loop  (O(n^2) copies)
//   2. Repeated LINQ full-scans per unique word (O(n^2) enumeration)
//   3. Duplicate tracking via List.Contains      (O(n) per lookup)
//
// ============================================================================

using System.Diagnostics;
using System.Text;

// ---------------------------------------------------------------------------
// Generate a large corpus so the performance difference is unmistakable.
// We repeat a realistic paragraph until we exceed the target word count.
// ---------------------------------------------------------------------------

const int TargetWordCount = 60_000;

string paragraph =
    "The quick brown fox jumps over the lazy dog while the cat sleeps on the warm windowsill " +
    "Performance optimization requires careful measurement before making any changes to the code " +
    "Premature optimization is the root of all evil but measured optimization is the root of all speed " +
    "A profiler reveals the truth that intuition alone cannot provide showing exactly where time is spent " +
    "Memory allocation patterns affect garbage collection frequency which in turn affects application latency " +
    "Understanding the runtime behavior of your application is the first step toward meaningful improvement " +
    "The CLR just in time compiler transforms intermediate language into native machine code at runtime " +
    "Each string concatenation allocates a new string object and copies every previous character again ";

string[] paragraphWords = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
var builder = new StringBuilder(TargetWordCount * 8);
int wordsWritten = 0;
while (wordsWritten < TargetWordCount)
{
    foreach (string w in paragraphWords)
    {
        if (wordsWritten > 0) builder.Append(' ');
        builder.Append(w);
        wordsWritten++;
        if (wordsWritten >= TargetWordCount) break;
    }
}

string corpus = builder.ToString();
Console.WriteLine($"Corpus size: {wordsWritten:N0} words, {corpus.Length:N0} characters");
Console.WriteLine(new string('-', 60));

// ---------------------------------------------------------------------------
// Slow (anti-pattern) run
// ---------------------------------------------------------------------------

Console.WriteLine("Running SlowWordCounter ...");
var sw = Stopwatch.StartNew();
Dictionary<string, int> slowResult = SlowWordCounter.CountWords(corpus);
sw.Stop();
long slowMs = sw.ElapsedMilliseconds;
Console.WriteLine($"  Unique words : {slowResult.Count}");
Console.WriteLine($"  Elapsed      : {slowMs:N0} ms");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Fast (optimized) run
// ---------------------------------------------------------------------------

Console.WriteLine("Running FastWordCounter ...");
sw.Restart();
Dictionary<string, int> fastResult = FastWordCounter.CountWords(corpus);
sw.Stop();
long fastMs = sw.ElapsedMilliseconds;
Console.WriteLine($"  Unique words : {fastResult.Count}");
Console.WriteLine($"  Elapsed      : {fastMs:N0} ms");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------

double speedup = slowMs == 0 ? double.PositiveInfinity : (double)slowMs / Math.Max(fastMs, 1);
Console.WriteLine($"Speed-up factor: ~{speedup:F1}x");
Console.WriteLine();
Console.WriteLine("Now re-run this program under the VS Performance Profiler (CPU Usage)");
Console.WriteLine("to see exactly which methods and call sites dominate the slow path.");

// ============================================================================
// Section 1 — The Problematic Implementation
// ============================================================================

static class SlowWordCounter
{
    /// <summary>
    /// Counts word frequencies using three deliberately inefficient techniques.
    /// </summary>
    public static Dictionary<string, int> CountWords(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Normalize to lowercase — we do this in a separate array so both
        // implementations work from the same starting point.
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = words[i].ToLowerInvariant();
        }

        // ---------------------------------------------------------------
        // Problem 1: String concatenation in a loop using +=
        // ---------------------------------------------------------------
        // Each += on a string allocates a brand-new System.String whose
        // length equals the old string plus the appended piece, then copies
        // every character from the old string into the new one.  For n
        // words this produces O(n) allocations whose total copied bytes
        // grow as 1 + 2 + 3 + ... + n = O(n^2).
        //
        // In the VS Profiler you will see heavy time in:
        //   System.String.Concat(String, String)
        //   System.String.Copy / Buffer.Memmove
        // ---------------------------------------------------------------
        string allWordsJoined = "";
        foreach (string word in words)
        {
            allWordsJoined += word + " ";   // O(n^2) — DO NOT do this
        }

        // We split again just to consume the concatenated string so the
        // compiler cannot optimize the dead code away.
        string[] wordsAgain = allWordsJoined.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ---------------------------------------------------------------
        // Problem 2: Repeated LINQ queries for counting
        // ---------------------------------------------------------------
        // For every unique word we perform a full linear scan of the
        // entire array with Enumerable.Where + Enumerable.Count.  If
        // there are U unique words and N total words, this is O(U * N).
        // Because LINQ uses deferred execution, the Where predicate is
        // re-evaluated from scratch on every Count() call.
        //
        // In the VS Profiler you will see thousands of calls to:
        //   System.Linq.Enumerable.Where
        //   System.Linq.Enumerable.Count
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // Problem 3: Using List<string> to track seen words
        // ---------------------------------------------------------------
        // List<T>.Contains performs a linear scan — O(n) per call.
        // As the list grows, each Contains check becomes slower.
        // With U unique words encountered over N total words, the
        // aggregate cost is O(N * U) in the worst case.
        //
        // In the VS Profiler you will see time in:
        //   System.Collections.Generic.List`1.Contains
        //   System.Collections.Generic.ObjectEqualityComparer`1.Equals
        // ---------------------------------------------------------------
        var seenWords = new List<string>();          // Should be HashSet<string>
        var frequency = new Dictionary<string, int>();

        foreach (string word in wordsAgain)
        {
            if (!seenWords.Contains(word))           // O(n) linear scan
            {
                seenWords.Add(word);

                // Full LINQ scan for this word's count — O(n) per unique word
                int count = wordsAgain.Where(w => w == word).Count();
                frequency[word] = count;
            }
        }

        return frequency;
    }
}

// ============================================================================
// Section 2 — The Optimized Implementation
// ============================================================================

static class FastWordCounter
{
    /// <summary>
    /// Counts word frequencies in a single pass with O(n) total time.
    /// Every fix directly addresses one of the three anti-patterns above.
    /// </summary>
    public static Dictionary<string, int> CountWords(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ---------------------------------------------------------------
        // Fix 1: Use StringBuilder instead of string +=
        // ---------------------------------------------------------------
        // StringBuilder maintains an internal char[] buffer that grows by
        // doubling, so appending n words is amortized O(n) total — no
        // quadratic copying.  For this exercise we build the joined
        // string to keep the structure parallel to the slow version.
        // ---------------------------------------------------------------
        var sb = new StringBuilder(text.Length);
        foreach (string word in words)
        {
            string lower = word.ToLowerInvariant();
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(lower);
        }

        string[] wordsAgain = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ---------------------------------------------------------------
        // Fix 2 & 3: Single-pass counting with Dictionary
        // ---------------------------------------------------------------
        // Instead of scanning the array once per unique word (O(U*N)),
        // we iterate the array exactly once and increment a counter in
        // a Dictionary<string, int>.  Dictionary uses a hash table, so
        // both TryGetValue and indexer assignment are amortized O(1).
        //
        // This also eliminates the need for a separate "seen" collection
        // entirely — the Dictionary itself tracks which words we have
        // encountered.  No List.Contains, no HashSet needed.
        //
        // Total complexity: O(n) time, O(U) space.
        // ---------------------------------------------------------------
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string word in wordsAgain)
        {
            if (frequency.TryGetValue(word, out int count))
            {
                frequency[word] = count + 1;
            }
            else
            {
                frequency[word] = 1;
            }
        }

        return frequency;
    }
}

// ============================================================================
// Profiler Analysis Guide
// ============================================================================
//
// After running this program normally to confirm the timing difference,
// re-run it under the Visual Studio Performance Profiler to practice
// diagnosing the root causes:
//
// 1. COLLECTING A TRACE
//    Debug > Performance Profiler (Alt+F2) > check "CPU Usage" > Start.
//    Let the program finish, then click "Stop collection."
//
// 2. CPU USAGE — TOP FUNCTIONS VIEW
//    Sort by "Total CPU" or "Self CPU."  In the slow path you will see:
//      - System.String.Concat(String, String)  — from the += loop
//      - System.Linq.Enumerable.WhereArrayIterator.MoveNext — from
//        the repeated Where().Count() calls
//      - System.Collections.Generic.List`1.Contains — from the linear
//        duplicate check
//    In the fast path these entries will be absent or negligible.
//
// 3. FLAME GRAPH / CALL TREE
//    Open the Call Tree or Flame Graph tab.  Expand the hot path:
//      Main > SlowWordCounter.CountWords
//        > String.Concat           (Problem 1)
//        > Enumerable.Where/Count  (Problem 2)
//        > List`1.Contains         (Problem 3)
//    The width of each bar is proportional to its CPU time.  The three
//    anti-patterns will dominate the flame graph for the slow path.
//
// 4. INTERPRETING THE RESULTS
//    - String.Concat calls indicate string immutability overhead.
//      Fix: replace with StringBuilder or string.Join.
//    - Repeated Enumerable.Where/Count calls indicate redundant
//      iteration.  Fix: aggregate in a single pass with Dictionary.
//    - List.Contains calls with growing self-time indicate O(n)
//      membership tests.  Fix: use HashSet<T> for O(1) lookups.
//
// 5. VALIDATING THE FIX
//    After switching to FastWordCounter, re-profile.  The flame graph
//    should show a flat, narrow bar for CountWords with almost all
//    time in String.Split and Dictionary operations — the expected
//    O(n) work that cannot be eliminated.
//
// ============================================================================
