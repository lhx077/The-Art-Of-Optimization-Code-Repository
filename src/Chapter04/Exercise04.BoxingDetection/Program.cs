// ============================================================================
// Exercise 04: Boxing Detection
// The Art of Optimization — Chapter 4: The Type System
// Value Types vs Reference Types
//
// This exercise creates a simple boxing detection tool and demonstrates the
// allocation differences between ArrayList (non-generic) and List<T> (generic),
// as well as other common boxing traps in everyday C# code.
//
// WHAT IS BOXING?
// ---------------
// Boxing is the process of wrapping a value type (which lives on the stack or
// inline in its containing object) inside a System.Object on the managed heap.
// The CLR allocates a new object consisting of:
//   - Object header (8 bytes on x64: sync block index + method table pointer)
//   - The actual value data copied into the heap object
//
// For a simple int (4 bytes), boxing allocates 24 bytes on the heap (8 header +
// 8 method table + 4 value + 4 padding). For a struct with two ints (8 bytes),
// it allocates ~32 bytes. This overhead is invisible in source code but very
// real in the allocator and GC.
//
// COMMON BOXING TRAPS:
// --------------------
// 1. Non-generic collections (ArrayList, Hashtable) — store everything as object
// 2. Non-generic interfaces (IComparable, IComparer) — parameters are object
// 3. String formatting with params object[] — each value type arg is boxed
// 4. LINQ on value types without struct enumerators — boxes the enumerator
// 5. Dictionary/HashSet without IEquatable<T> — falls back to object.Equals
//
// HOW IEquatable<T> AND IComparable<T> PREVENT BOXING:
// ----------------------------------------------------
// When a struct implements IEquatable<T>, methods like List<T>.Contains() and
// Dictionary<TKey,TValue> can call Equals(T other) directly through a
// constrained generic call — no boxing needed. Without IEquatable<T>, the
// runtime must fall back to object.Equals(object), which boxes the argument.
// The same principle applies to IComparable<T> for sorting operations.
//
// HOW MODERN C# STRING INTERPOLATION AVOIDS BOXING:
// -------------------------------------------------
// Since C# 10, interpolated strings like $"{value}" are lowered by the compiler
// into calls to DefaultInterpolatedStringHandler. This handler has typed
// AppendFormatted<T>(T value) overloads that accept value types WITHOUT boxing.
// The older Console.WriteLine("{0}", value) uses params object[] and boxes.
//
// MEASUREMENT TOOL — GC.GetAllocatedBytesForCurrentThread():
// ----------------------------------------------------------
// This API returns the total bytes allocated on the managed heap by the current
// thread since process start. By recording before/after values around a code
// block, we get a precise measurement of allocations caused by that block.
// Unlike GC.GetTotalMemory(), it is per-thread and unaffected by other threads
// or background GC activity, making it ideal for micro-benchmarking allocations.
// ============================================================================

using System.Collections;

// ============================================================================
// TOP-LEVEL STATEMENTS — All executable code comes before type declarations
// ============================================================================

const int ItemCount = 100_000;

// --- Warm up JIT so compilation allocations don't pollute measurements ---
WarmUpAll();

Console.WriteLine("==========================================================================");
Console.WriteLine("  Boxing Detection — Allocation Measurement Tool");
Console.WriteLine("  The Art of Optimization — Chapter 4: Value Types vs Reference Types");
Console.WriteLine("==========================================================================");
Console.WriteLine();

// ============================================================================
// Test 1: ArrayList (non-generic, boxes every value) vs List<T> (generic, no boxing)
// ============================================================================
Console.WriteLine("--- Test 1: ArrayList vs List<T> (adding 100,000 Point values) ---");
Console.WriteLine();

long arrayListAlloc = BoxingDetector.MeasureAllocations(() =>
{
    var list = new ArrayList();
    for (int i = 0; i < ItemCount; i++)
        list.Add(new Point(i, i));  // Each Add() boxes the Point struct
});

long genericListAlloc = BoxingDetector.MeasureAllocations(() =>
{
    var list = new List<Point>();
    for (int i = 0; i < ItemCount; i++)
        list.Add(new Point(i, i));  // No boxing — stored inline as Point
});

Console.WriteLine($"  {"ArrayList (boxes every Add):",-42} {arrayListAlloc,12:N0} bytes");
Console.WriteLine($"  {"List<Point> (no boxing):",-42} {genericListAlloc,12:N0} bytes");
double ratio1 = genericListAlloc > 0
    ? (double)arrayListAlloc / genericListAlloc
    : double.PositiveInfinity;
Console.WriteLine($"  {"Ratio (ArrayList / List<T>):",-42} {ratio1,12:F1}x");
Console.WriteLine();
// ============================================================================
// Test 2: IComparable (non-generic, boxes) vs IComparable<T> (generic, no boxing)
// ============================================================================
Console.WriteLine("--- Test 2: Sorting — Non-generic IComparer vs Generic IComparable<T> ---");
Console.WriteLine();

// Create two identical arrays for fair comparison
Point[] arrayForNonGeneric = new Point[ItemCount];
Point[] arrayForGeneric = new Point[ItemCount];
var rng = new Random(42);
for (int i = 0; i < ItemCount; i++)
{
    var p = new Point(rng.Next(1000), rng.Next(1000));
    arrayForNonGeneric[i] = p;
    arrayForGeneric[i] = p;
}

// Non-generic IComparer — boxes both arguments on every comparison
long nonGenericSortAlloc = BoxingDetector.MeasureAllocations(() =>
{
    Array.Sort(arrayForNonGeneric, new NonGenericPointComparer());
});

// Re-shuffle for fair comparison
Array.Copy(arrayForGeneric, arrayForNonGeneric, ItemCount);

// Generic path — Array.Sort<T> uses IComparable<T> directly, no boxing
long genericSortAlloc = BoxingDetector.MeasureAllocations(() =>
{
    Array.Sort(arrayForGeneric);  // Uses Point's IComparable<Point>
});

Console.WriteLine($"  {"Non-generic IComparer (boxes):",-42} {nonGenericSortAlloc,12:N0} bytes");
Console.WriteLine($"  {"Generic IComparable<T> (no boxing):",-42} {genericSortAlloc,12:N0} bytes");
Console.WriteLine();
// ============================================================================
// Test 3: Console.WriteLine boxing — params object[] vs interpolated string
// ============================================================================
Console.WriteLine("--- Test 3: String Formatting — params object[] vs Interpolation ---");
Console.WriteLine();

var testPoint = new Point(42, 99);

// Redirect Console output to suppress the test output lines
var originalOut = Console.Out;
Console.SetOut(TextWriter.Null);

// Console.WriteLine("{0}, {1}", x, y) — boxes both int arguments into object[]
long paramsBoxingAlloc = BoxingDetector.MeasureAllocations(() =>
{
    for (int i = 0; i < 1_000; i++)
    {
#pragma warning disable CA1305 // Intentionally using non-IFormatProvider overload to show boxing
        Console.WriteLine("{0}, {1}", testPoint.X, testPoint.Y);
#pragma warning restore CA1305
    }
});

// $"{x}, {y}" — C# 10+ lowers this to DefaultInterpolatedStringHandler, no boxing
long interpolationAlloc = BoxingDetector.MeasureAllocations(() =>
{
    for (int i = 0; i < 1_000; i++)
    {
        Console.WriteLine($"{testPoint.X}, {testPoint.Y}");
    }
});

// Restore console output
Console.SetOut(originalOut);

Console.WriteLine($"  {"params object[] (boxes ints):",-42} {paramsBoxingAlloc,12:N0} bytes");
Console.WriteLine($"  {"Interpolation (no boxing):",-42} {interpolationAlloc,12:N0} bytes");
Console.WriteLine();
// ============================================================================
// Test 4: Dictionary key boxing — IEquatable<T> prevents boxing in lookups
// ============================================================================
Console.WriteLine("--- Test 4: Dictionary<Point, int> — IEquatable<T> Key Lookups ---");
Console.WriteLine();

// Because Point implements IEquatable<Point>, EqualityComparer<Point>.Default
// will use the typed Equals(Point other) method via a constrained generic call.
// This means dictionary lookups (GetHashCode + Equals) happen WITHOUT boxing.
//
// If Point did NOT implement IEquatable<Point>, EqualityComparer<Point>.Default
// would fall back to calling object.Equals(object), which boxes the Point
// argument on every comparison. For a dictionary with many collisions, this
// could mean thousands of boxing allocations per lookup.

long dictAlloc = BoxingDetector.MeasureAllocations(() =>
{
    var dict = new Dictionary<Point, int>();
    for (int i = 0; i < 10_000; i++)
        dict[new Point(i, i)] = i;

    // Perform lookups — these call GetHashCode() and Equals(Point)
    for (int i = 0; i < 10_000; i++)
        _ = dict.TryGetValue(new Point(i, i), out _);
});

Console.WriteLine($"  {"Dict<Point,int> with IEquatable<T>:",-42} {dictAlloc,12:N0} bytes");
Console.WriteLine("  (Without IEquatable<T>, Equals(object) would box every comparison)");
Console.WriteLine();

// ============================================================================
// GC Collection Summary
// ============================================================================
Console.WriteLine("--- GC Collection Counts During Full Run ---");
Console.WriteLine();
var (g0, g1, g2) = BoxingDetector.MeasureCollections(() =>
{
    // Re-run the heaviest test to show GC impact
    var list = new ArrayList();
    for (int i = 0; i < ItemCount; i++)
        list.Add(new Point(i, i));
});
Console.WriteLine($"  Gen0 collections: {g0}");
Console.WriteLine($"  Gen1 collections: {g1}");
Console.WriteLine($"  Gen2 collections: {g2}");
Console.WriteLine();

Console.WriteLine("==========================================================================");
Console.WriteLine("  Key Takeaway: Generic APIs eliminate boxing. Always prefer List<T> over");
Console.WriteLine("  ArrayList, implement IEquatable<T>/IComparable<T> on structs, and use");
Console.WriteLine("  string interpolation instead of params object[] formatting.");
Console.WriteLine("==========================================================================");

return;

// ============================================================================
// Local function: warm up all code paths so JIT allocations don't pollute
// ============================================================================
void WarmUpAll()
{
    var p1 = new Point(1, 2);
    var p2 = new Point(3, 4);

    // Warm up BoxingDetector
    BoxingDetector.MeasureAllocations(() => { });
    BoxingDetector.MeasureCollections(() => { });

    // Warm up ArrayList and List<Point>
    _ = new ArrayList { p1 };
    _ = new List<Point> { p1 };

    // Warm up equality and comparison
    _ = p1.Equals(p2);
    _ = p1.CompareTo(p2);
    _ = ((object)p1).Equals(p2);
    _ = EqualityComparer<Point>.Default.Equals(p1, p2);

    // Warm up sorting
    var warmArr = new[] { p2, p1 };
    Array.Sort(warmArr);
    Array.Sort(warmArr, new NonGenericPointComparer());

    // Warm up string formatting
    var savedOut = Console.Out;
    Console.SetOut(TextWriter.Null);
#pragma warning disable CA1305
    Console.WriteLine("{0}, {1}", p1.X, p1.Y);
#pragma warning restore CA1305
    Console.WriteLine($"{p1.X}, {p1.Y}");
    Console.SetOut(savedOut);

    // Warm up dictionary
    var d = new Dictionary<Point, int> { [p1] = 1 };
    _ = d.TryGetValue(p1, out _);

    // Force full GC to clean up warmup allocations
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

// ============================================================================
// TYPE DECLARATIONS — Must come AFTER all top-level statements in C#
// ============================================================================

// ============================================================================
// BoxingDetector — Measures heap allocations and GC collections
// ============================================================================
// This struct provides two static methods for measuring the allocation impact
// of any code block. It uses GC.GetAllocatedBytesForCurrentThread() which
// returns a monotonically increasing counter of bytes allocated by the current
// thread. The difference before/after tells us exactly how much was allocated.
// ============================================================================
struct BoxingDetector
{
    /// <summary>
    /// Measures the number of bytes allocated on the managed heap by the given action.
    /// Performs a full GC before measuring to minimize noise from prior allocations.
    /// </summary>
    public static long MeasureAllocations(Action action)
    {
        // Force a full GC to clean up any pending allocations from previous work.
        // The three-step pattern (Collect, WaitForFinalizers, Collect) ensures that
        // objects with finalizers are also fully cleaned up — the first Collect
        // enqueues finalizable objects, WaitForPendingFinalizers runs them, and
        // the second Collect reclaims the now-dead finalized objects.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return after - before;
    }

    /// <summary>
    /// Measures how many GC collections occur in each generation during the action.
    /// </summary>
    public static (int g0, int g1, int g2) MeasureCollections(Action action)
    {
        int g0Before = GC.CollectionCount(0);
        int g1Before = GC.CollectionCount(1);
        int g2Before = GC.CollectionCount(2);

        action();

        return (
            GC.CollectionCount(0) - g0Before,
            GC.CollectionCount(1) - g1Before,
            GC.CollectionCount(2) - g2Before
        );
    }
}
// ============================================================================
// Point — A simple value type that correctly implements generic interfaces
// ============================================================================
// By implementing IEquatable<Point>, we provide a typed Equals(Point other)
// method. When the runtime needs to compare two Points (e.g., in a Dictionary
// or List.Contains), it can call this method directly through a constrained
// generic call — no boxing required.
//
// By implementing IComparable<Point>, we provide a typed CompareTo(Point other)
// method. Array.Sort<T>() and List<T>.Sort() use this directly when T
// implements IComparable<T>, avoiding the boxing that would occur if they
// had to go through IComparable.CompareTo(object).
//
// We also override Equals(object?) and GetHashCode() for correctness — these
// are called when boxing does occur (e.g., in non-generic collections) and
// must be consistent with the typed overloads.
// ============================================================================
struct Point : IEquatable<Point>, IComparable<Point>
{
    public int X { get; }
    public int Y { get; }

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    // --- IEquatable<Point> — avoids boxing in generic equality checks ---
    public bool Equals(Point other) => X == other.X && Y == other.Y;

    // --- IComparable<Point> — avoids boxing in generic sorting ---
    // Compares by X first, then by Y (lexicographic order).
    public int CompareTo(Point other)
    {
        int cmp = X.CompareTo(other.X);
        return cmp != 0 ? cmp : Y.CompareTo(other.Y);
    }

    // --- object.Equals override — called when boxing occurs ---
    public override bool Equals(object? obj) => obj is Point other && Equals(other);

    // --- GetHashCode — must be consistent with Equals ---
    public override int GetHashCode() => HashCode.Combine(X, Y);

    // --- ToString — for display purposes ---
    public override string ToString() => $"({X}, {Y})";
}

// ============================================================================
// NonGenericPointComparer — Deliberately uses the non-generic IComparer
// ============================================================================
// This comparer implements IComparer (not IComparer<Point>), so Array.Sort
// must box both Point arguments when calling Compare(object, object).
// This is intentional — we use it to demonstrate the boxing cost of
// non-generic interfaces.
// ============================================================================
class NonGenericPointComparer : IComparer
{
    public int Compare(object? x, object? y)
    {
        // Both x and y arrive already boxed. We unbox them to compare.
        var p1 = (Point)x!;
        var p2 = (Point)y!;
        int cmp = p1.X.CompareTo(p2.X);
        return cmp != 0 ? cmp : p1.Y.CompareTo(p2.Y);
    }
}