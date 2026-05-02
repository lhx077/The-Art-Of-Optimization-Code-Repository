// ============================================================================
// Exercise 03: Regex Performance Analyzer
// The Art of Optimization — Chapter 5: Strings and Text Processing
//
// This exercise builds a simple heuristic analyzer that detects potentially
// dangerous regex patterns — ones that can cause catastrophic backtracking —
// and recommends safer alternatives or compilation strategies.
//
// WHAT IS CATASTROPHIC BACKTRACKING?
// ----------------------------------
// The default .NET regex engine uses an NFA-based backtracking algorithm.
// When a pattern contains ambiguous quantifiers (e.g., nested `(a+)+`),
// the engine may explore an exponential number of paths before concluding
// that no match exists. For an input of length N, the engine can take
// O(2^N) steps, turning a simple match into a multi-second (or infinite) hang.
//
// The classic example is `(a+)+$` matched against "aaa...a!". The engine
// tries every possible way to partition the 'a' characters among the inner
// and outer repetitions before finally failing at the '!' character.
//
// THREE REGEX COMPILATION MODES IN .NET:
// --------------------------------------
// 1. Interpreted (default) — The pattern is parsed into an internal opcode
//    tree and walked at runtime. Fast startup, slower execution. Good for
//    one-off or rarely-used patterns.
//
// 2. Compiled (RegexOptions.Compiled) — The pattern is compiled to IL via
//    Reflection.Emit at first use. Slower startup (~10x), but subsequent
//    matches run significantly faster. Ideal for patterns used in hot loops.
//
// 3. Source-generated ([GeneratedRegex]) — Introduced in .NET 7. The Roslyn
//    source generator emits a specialized Regex subclass at compile time.
//    Zero startup cost, best runtime performance, and the generated code is
//    inspectable and trimmable. The gold standard for known-at-compile-time
//    patterns.
//
// RegexOptions.NonBacktracking (DFA-BASED ENGINE):
// ------------------------------------------------
// Added in .NET 7, this option uses a DFA-based engine that guarantees
// O(N) matching time regardless of pattern complexity. It eliminates
// catastrophic backtracking entirely. The tradeoff: it does not support
// backreferences (\1), lookaheads/lookbehinds, or atomic groups. For
// patterns that don't need those features, it's the safest choice when
// processing untrusted input.
//
// WHY TIMEOUTS ARE ESSENTIAL:
// ---------------------------
// When accepting regex patterns from users (e.g., search filters, log
// queries), you cannot guarantee the pattern is safe. Setting
// Regex.MatchTimeout (or passing a TimeSpan to the constructor) ensures
// that even a catastrophic pattern will throw RegexMatchTimeoutException
// after a bounded duration rather than hanging the thread forever.
// Always use a timeout when the pattern source is not fully trusted.
// ============================================================================

using System.Diagnostics;
using System.Text.RegularExpressions;

// ============================================================================
// TOP-LEVEL STATEMENTS — All executable code comes before type declarations
// ============================================================================

Console.WriteLine("==========================================================================");
Console.WriteLine("  Regex Performance Analyzer");
Console.WriteLine("  The Art of Optimization — Chapter 5: Strings and Text Processing");
Console.WriteLine("==========================================================================");
Console.WriteLine();

// Define test patterns with descriptions and adversarial inputs
var testCases = new (string Pattern, string Description, string AdversarialInput)[]
{
    // 1. Classic catastrophic backtracking: nested quantifiers (a+)+$
    //    The inner a+ and outer ()+ both compete for the same 'a' characters.
    //    When the trailing '$' fails (due to '!'), the engine backtracks through
    //    every possible partition of the 'a' characters — O(2^N) paths.
    (@"(a+)+$",
     "Catastrophic backtracking — nested quantifiers",
     new string('a', 25) + "!"),

    // 2. Safe domain-matching pattern: no nested quantifiers, no ambiguity.
    //    The character class [a-z] and literal '.' are unambiguous, so the
    //    engine never needs to backtrack significantly.
    (@"^[a-z]+(\.[a-z]+)*$",
     "Safe domain pattern — no dangerous nesting",
     "this.is.a.safe.domain.test"),

    // 3. Overlapping + nested: (x+x+)+y — both x+ groups match the same 'x'
    //    characters, and the outer ()+ repeats the ambiguity. Even worse than
    //    (a+)+$ because there are two competing quantifiers inside the group.
    (@"(x+x+)+y",
     "Catastrophic — overlapping quantifiers + nesting",
     new string('x', 25) + "!"),

    // 4. Safe IP address pattern: bounded repetition {1,3} with literal dots.
    //    No ambiguity — \d{1,3} matches 1-3 digits, then a literal '.' must follow.
    (@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
     "Safe IP pattern — bounded repetition",
     "192.168.1.1"),

    // 5. Dangerous pattern from Thought Question 2: ^(([a-z])+\.)+[a-z]+$
    //    The inner ([a-z])+ and outer (...)+ create nested quantifiers over
    //    the same character class. When the input lacks a final '.', the engine
    //    backtracks exponentially trying different groupings of characters.
    (@"^(([a-z])+\.)+[a-z]+$",
     "Dangerous — nested quantifiers over [a-z] (Thought Q2)",
     new string('a', 25) + "!"),
};

foreach (var (pattern, description, adversarialInput) in testCases)
{
    Console.WriteLine("--------------------------------------------------------------------------");
    Console.WriteLine($"  Pattern: {pattern}");
    Console.WriteLine($"  Description: {description}");
    Console.WriteLine("--------------------------------------------------------------------------");
    Console.WriteLine();

    // --- Static Analysis ---
    var warnings = RegexAnalyzer.Analyze(pattern);
    if (warnings.Count == 0)
    {
        Console.WriteLine("  Analysis: No warnings detected — pattern appears safe.");
    }
    else
    {
        Console.WriteLine("  Analysis Warnings:");
        foreach (var w in warnings)
        {
            Console.WriteLine($"    [{w.Severity}] {w.Description}");
            Console.WriteLine($"           Suggestion: {w.Suggestion}");
        }
    }
    Console.WriteLine();

    // --- Recommendation ---
    string recommendation = RegexRecommender.Recommend(pattern);
    Console.WriteLine($"  Recommendation: {recommendation}");
    Console.WriteLine();

    // --- Backtracking Test ---
    Console.WriteLine($"  Backtracking Test (input length {adversarialInput.Length}):");
    var (timedOut, elapsedMs) = BacktrackingTester.TestPattern(pattern, adversarialInput, timeoutMs: 2000);
    if (timedOut)
    {
        Console.WriteLine($"    TIMED OUT after {elapsedMs:N0} ms — confirms dangerous backtracking!");
    }
    else
    {
        Console.WriteLine($"    Completed in {elapsedMs:N0} ms — no backtracking issue detected.");
    }
    Console.WriteLine();
}

Console.WriteLine("==========================================================================");
Console.WriteLine("  Key Takeaways:");
Console.WriteLine("  1. Nested quantifiers like (a+)+ cause exponential backtracking.");
Console.WriteLine("  2. Use RegexOptions.NonBacktracking for untrusted patterns (O(n) guarantee).");
Console.WriteLine("  3. Always set a timeout when the pattern comes from user input.");
Console.WriteLine("  4. Prefer [GeneratedRegex] for compile-time-known patterns.");
Console.WriteLine("==========================================================================");

return;

// ============================================================================
// TYPE DECLARATIONS — Must come AFTER all top-level statements in C#
// ============================================================================

// ============================================================================
// RegexAnalyzer — Heuristic static analysis of regex patterns
// ============================================================================
// This analyzer uses simple string scanning (IndexOf, substring checks) to
// detect common dangerous patterns. It is NOT a full regex parser — it catches
// the most common pitfalls but cannot reason about all possible interactions.
//
// The three checks implemented:
// 1. Nested quantifiers: a quantifier (+, *) inside a group that itself has
//    a quantifier. This is the #1 cause of catastrophic backtracking.
// 2. Overlapping alternatives: alternatives in a group that share a common
//    prefix, causing the engine to try both paths on the same input.
// 3. Unbounded repetition: .+ or .* without lazy modifier (?), which can
//    cause excessive backtracking when the rest of the pattern fails.
// ============================================================================
static class RegexAnalyzer
{
    public static List<RegexWarning> Analyze(string pattern)
    {
        var warnings = new List<RegexWarning>();

        CheckNestedQuantifiers(pattern, warnings);
        CheckOverlappingAlternatives(pattern, warnings);
        CheckUnboundedRepetition(pattern, warnings);

        return warnings;
    }

    // --- Check 1: Nested quantifiers ---
    // Look for groups containing + or * that are themselves followed by + or *.
    // Examples: (a+)+, (a*)+, (a+)*, (x+x+)+
    private static void CheckNestedQuantifiers(string pattern, List<RegexWarning> warnings)
    {
        // Find each group by scanning for matching parentheses
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '(') continue;

            // Find the matching closing parenthesis (simple depth tracking)
            int depth = 1;
            int closeIndex = -1;
            for (int j = i + 1; j < pattern.Length && depth > 0; j++)
            {
                if (pattern[j] == '(' && (j == 0 || pattern[j - 1] != '\\'))
                    depth++;
                else if (pattern[j] == ')' && (j == 0 || pattern[j - 1] != '\\'))
                {
                    depth--;
                    if (depth == 0) closeIndex = j;
                }
            }

            if (closeIndex < 0) continue;

            // Check if the group has an outer quantifier
            bool hasOuterQuantifier = closeIndex + 1 < pattern.Length
                && (pattern[closeIndex + 1] == '+' || pattern[closeIndex + 1] == '*');

            if (!hasOuterQuantifier) continue;

            // Check if there's an inner quantifier inside this group
            string groupContent = pattern.Substring(i + 1, closeIndex - i - 1);
            bool hasInnerQuantifier = false;
            for (int k = 0; k < groupContent.Length; k++)
            {
                if ((groupContent[k] == '+' || groupContent[k] == '*')
                    && (k == 0 || groupContent[k - 1] != '\\'))
                {
                    hasInnerQuantifier = true;
                    break;
                }
            }

            if (hasInnerQuantifier)
            {
                warnings.Add(new RegexWarning(
                    "High",
                    $"Nested quantifiers detected: ({groupContent}){pattern[closeIndex + 1]} — " +
                    "this causes exponential backtracking O(2^N).",
                    "Rewrite to eliminate nesting, or use RegexOptions.NonBacktracking."));
            }
        }
    }

    // --- Check 2: Overlapping alternatives ---
    // Look for alternation groups where branches share a common prefix.
    // Examples: (a|a), (ab|ac), (foo|foobar)
    private static void CheckOverlappingAlternatives(string pattern, List<RegexWarning> warnings)
    {
        // Find groups containing '|'
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '(') continue;

            int depth = 1;
            int closeIndex = -1;
            for (int j = i + 1; j < pattern.Length && depth > 0; j++)
            {
                if (pattern[j] == '(' && (j == 0 || pattern[j - 1] != '\\'))
                    depth++;
                else if (pattern[j] == ')' && (j == 0 || pattern[j - 1] != '\\'))
                {
                    depth--;
                    if (depth == 0) closeIndex = j;
                }
            }

            if (closeIndex < 0) continue;

            string groupContent = pattern.Substring(i + 1, closeIndex - i - 1);
            if (!groupContent.Contains('|')) continue;

            // Split alternatives at the top level (depth 0) only
            var alternatives = new List<string>();
            int altDepth = 0;
            int start = 0;
            for (int k = 0; k < groupContent.Length; k++)
            {
                if (groupContent[k] == '(' && (k == 0 || groupContent[k - 1] != '\\'))
                    altDepth++;
                else if (groupContent[k] == ')' && (k == 0 || groupContent[k - 1] != '\\'))
                    altDepth--;
                else if (groupContent[k] == '|' && altDepth == 0)
                {
                    alternatives.Add(groupContent.Substring(start, k - start));
                    start = k + 1;
                }
            }
            alternatives.Add(groupContent.Substring(start));

            // Check each pair for common prefix
            for (int a = 0; a < alternatives.Count; a++)
            {
                for (int b = a + 1; b < alternatives.Count; b++)
                {
                    if (alternatives[a].Length > 0 && alternatives[b].Length > 0
                        && alternatives[a][0] == alternatives[b][0])
                    {
                        warnings.Add(new RegexWarning(
                            "Medium",
                            $"Overlapping alternatives: '{alternatives[a]}' and '{alternatives[b]}' " +
                            "share a common prefix — engine tries both branches on same input.",
                            "Factor out the common prefix, e.g., change (ab|ac) to a(b|c)."));
                        // Only report once per group
                        goto NextGroup;
                    }
                }
            }
            NextGroup:;
        }
    }

    // --- Check 3: Unbounded repetition ---
    // Look for .+ or .* without a lazy modifier (?). These match as much as
    // possible, then backtrack character by character when the rest of the
    // pattern fails. Not always catastrophic, but a common performance trap.
    private static void CheckUnboundedRepetition(string pattern, List<RegexWarning> warnings)
    {
        for (int i = 0; i < pattern.Length - 1; i++)
        {
            if (pattern[i] == '.' && (pattern[i + 1] == '+' || pattern[i + 1] == '*'))
            {
                // Check if it's lazy (.+? or .*?) — lazy is generally safer
                bool isLazy = i + 2 < pattern.Length && pattern[i + 2] == '?';
                if (!isLazy)
                {
                    warnings.Add(new RegexWarning(
                        "Low",
                        $"Unbounded greedy repetition '.{pattern[i + 1]}' — may cause excessive " +
                        "backtracking if followed by a pattern that can also match.",
                        "Consider using lazy '.+?' or '.*?', or be more specific than '.'."));
                }
            }
        }
    }
}

// ============================================================================
// RegexRecommender — Suggests optimal compilation strategy for a pattern
// ============================================================================
// Based on the analysis results and pattern characteristics, this class
// recommends the best way to compile and use the regex in .NET.
// ============================================================================
static class RegexRecommender
{
    public static string Recommend(string pattern)
    {
        var warnings = RegexAnalyzer.Analyze(pattern);
        bool hasDangerousBacktracking = warnings.Exists(w => w.Severity == "High");
        bool hasMediumRisk = warnings.Exists(w => w.Severity == "Medium");

        if (hasDangerousBacktracking)
        {
            // Pattern has catastrophic backtracking risk — the DFA engine is the
            // only safe choice. RegexOptions.NonBacktracking guarantees O(N) time.
            return "Use RegexOptions.NonBacktracking (.NET 7+) to guarantee O(N) matching. " +
                   "This pattern has catastrophic backtracking risk and MUST have a timeout " +
                   "if NonBacktracking is not available.";
        }

        if (hasMediumRisk)
        {
            return "Pattern has moderate risk. Use RegexOptions.Compiled for performance, " +
                   "and always set a MatchTimeout. Consider refactoring overlapping alternatives.";
        }

        // Safe pattern — recommend the best compilation strategy
        // For patterns known at compile time, source-generated regex is ideal.
        // For patterns constructed at runtime, Compiled is the next best option.
        return "Pattern appears safe. For best performance: use [GeneratedRegex] if the " +
               "pattern is known at compile time, or RegexOptions.Compiled for runtime patterns. " +
               "Always set a MatchTimeout when processing untrusted input.";
    }
}

// ============================================================================
// BacktrackingTester — Empirically tests patterns for backtracking behavior
// ============================================================================
// This class runs a regex match with a timeout to detect whether a pattern
// actually exhibits catastrophic backtracking on adversarial input. The
// Regex constructor accepts a TimeSpan timeout parameter — if the match
// exceeds this duration, it throws RegexMatchTimeoutException.
//
// This is the definitive test: static analysis can warn about potential
// issues, but only an actual match attempt reveals whether the pattern
// truly hangs on a given input.
// ============================================================================
static class BacktrackingTester
{
    /// <summary>
    /// Tests a regex pattern against an input string with a timeout.
    /// Returns whether the match timed out and how long it took.
    /// </summary>
    public static (bool timedOut, long elapsedMs) TestPattern(
        string pattern, string input, int timeoutMs = 1000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Create the regex with an explicit timeout. This is the key safety
            // mechanism — without it, a catastrophic pattern would hang forever.
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(timeoutMs));
            _ = regex.IsMatch(input);
            sw.Stop();
            return (timedOut: false, elapsedMs: sw.ElapsedMilliseconds);
        }
        catch (RegexMatchTimeoutException)
        {
            sw.Stop();
            return (timedOut: true, elapsedMs: sw.ElapsedMilliseconds);
        }
    }
}

// ============================================================================
// RegexWarning — Immutable record representing a single analysis finding
// ============================================================================
// Using a record gives us value equality, ToString(), and deconstruction
// for free — ideal for a simple data carrier.
// ============================================================================
record RegexWarning(string Severity, string Description, string Suggestion);