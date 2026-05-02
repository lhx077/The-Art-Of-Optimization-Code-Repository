// ============================================================================
// Exercise 04: Boxing Detector
// The Art of Optimization - Chapter 4: The Type System
// Performance Secrets of Value Types and Reference Types
//
// Detects boxing allocations using GC.GetAllocatedBytesForCurrentThread().
// Boxing occurs when a value type is converted to object or an interface,
// requiring a heap allocation to wrap the value. This hidden cost can be
// significant in hot paths.
//
// GC.GetAllocatedBytesForCurrentThread() returns total bytes allocated on
// the managed heap by the CURRENT thread since process start. By recording
// the value before and after an action, we get the exact allocation cost.
// This is more precise than GC.GetTotalMemory() because it is per-thread
// and unaffected by other threads or GC collections.
// ============================================================================

using System.Collections;

// ── JIT Warmup ─────────────────────────────────────────────────────────────
// The first call to any method triggers JIT compilation, which itself
// allocates memory. We run every code path once so those one-time costs
// don't pollute our measurements.

var w1 = new Point(1, 2);
var w2 = new Point(3, 4);
BoxingDetector.MeasureAllocations(() => { });
_ = new ArrayList { w1 };
_ = new List<Point> { w1 };
_ = w1.Equals(w2);
_ = ((object)w1).Equals(w2);
_ = EqualityComparer<Point>.Default.Equals(w1, w2);
Console.WriteLine($"warmup {w1}");
Console.WriteLine("warmup {0} {1}", w1.X, w1.Y);
AcceptEquatable(w1, w2);
AcceptObject(w1);
StoreInInterface(w1, w2);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Console.Clear();

// ── Banner ─────────────────────────────────────────────────────────────────

Console.WriteLine("==========================================================================");
Console.WriteLine("  Boxing Detector - Allocation Measurement Tool");
Console.WriteLine("  Chapter 4: The Type System");
Console.WriteLine("==========================================================================");
Console.WriteLine();
Console.WriteLine($"  {"Test",-50} {"Bytes",8}  {"Boxing?"}");
Console.WriteLine("  " + new string('-', 70));

// ── Test 1: ArrayList vs List<T> ───────────────────────────────────────────
// ArrayList stores elements as 'object'. Adding a value type (Point struct)
// forces boxing: the runtime allocates a heap object to hold the struct copy.
// Each box for Point (2 ints = 8 bytes) costs ~24 bytes on x64 (16-byte
// object header + 8 bytes payload).
//
// List<T> is generic and stores elements in a T[] array. No boxing occurs
// because the storage is Point[], not object[]. This is one of the biggest
// performance wins generics brought to .NET.

Console.WriteLine();
Console.WriteLine("  [Test 1] ArrayList vs List<T>");

BoxingDetector.Report("ArrayList.Add(Point) x1000", () =>
{
    var list = new ArrayList();
    for (int i = 0; i < 1000; i++)
        list.Add(new Point(i, i));  // Boxing! Each Point boxed to object.
});

BoxingDetector.Report("List<Point>.Add(Point) x1000", () =>
{
    var list = new List<Point>(1000);
    for (int i = 0; i < 1000; i++)
        list.Add(new Point(i, i));  // No boxing - stored as Point directly.
});

// ── Test 2: IEquatable<T> vs object.Equals ─────────────────────────────────
// Typed Equals(Point): No boxing. The method accepts Point directly, so
// the struct is passed by value on the stack.
//
// ((object)p1).Equals(p2): Boxes p1 to create an object on the heap,
// then calls virtual object.Equals(object).
//
// EqualityComparer<Point>.Default.Equals(): Detects IEquatable<Point> at
// construction time and calls typed Equals(Point) internally. No boxing.

Console.WriteLine();
Console.WriteLine("  [Test 2] IEquatable<T> vs object.Equals");

var p1 = new Point(10, 20);
var p2 = new Point(10, 20);

BoxingDetector.Report("p1.Equals(p2) [typed IEquatable]", () =>
{
    // Direct call to IEquatable<Point>.Equals(Point). No boxing.
    _ = p1.Equals(p2);
});

BoxingDetector.Report("((object)p1).Equals(p2) [boxed]", () =>
{
    // Cast (object)p1 boxes p1 onto the heap, then calls object.Equals.
    _ = ((object)p1).Equals(p2);
});

BoxingDetector.Report("EqualityComparer<Point>.Default", () =>
{
    // Detects IEquatable<T> and uses typed Equals(T). No boxing.
    _ = EqualityComparer<Point>.Default.Equals(p1, p2);
});

// ── Test 3: String formatting ──────────────────────────────────────────────
// Console.WriteLine("format", arg0, arg1) uses params object[] - each
// int argument is BOXED to fit into object[]. The array is also allocated.
//
// String interpolation ($"...{value}...") in modern C# (.NET 6+) uses
// DefaultInterpolatedStringHandler, which has typed AppendFormatted
// overloads for int, long, float, etc. These accept the value type
// directly WITHOUT boxing. The handler writes to a buffer instead.

Console.WriteLine();
Console.WriteLine("  [Test 3] String formatting");

var p = new Point(42, 99);

BoxingDetector.Report("WriteLine(format, args) [params]", () =>
{
    // params object[] causes boxing of both int arguments.
    Console.WriteLine("Point: {0}, {1}", p.X, p.Y);
});

BoxingDetector.Report("WriteLine($\"...\") [interpolated]", () =>
{
    // DefaultInterpolatedStringHandler: typed AppendFormatted(int). No boxing.
    Console.WriteLine($"Point: {p.X}, {p.Y}");
});

// ── Test 4: Interface dispatch ─────────────────────────────────────────────
// Constrained generic call (where T : IEquatable<T>):
//   The JIT emits a "constrained callvirt" IL instruction. For value types
//   this resolves to a direct call WITHOUT boxing. The struct is passed by
//   reference to its own Equals implementation. This is the key mechanism
//   that makes generic collections efficient for value types.
//
// Accepting object parameter:
//   Passing a struct as object always requires boxing.
//
// Storing in an interface variable (IEquatable<Point> iface = point):
//   Interface variables are reference types. Storing a struct in one
//   requires boxing. The subsequent call is virtual dispatch on the box.
Console.WriteLine();
Console.WriteLine("  [Test 4] Interface dispatch");

var q1 = new Point(5, 10);
var q2 = new Point(5, 10);

BoxingDetector.Report("Constrained call <T:IEquatable>", () =>
{
    // Generic constraint: JIT uses constrained callvirt, calling
    // Point.Equals(Point) directly without boxing.
    AcceptEquatable(q1, q2);
});

BoxingDetector.Report("Accept as object parameter", () =>
{
    // Passing a struct where object is expected always boxes.
    AcceptObject(q1);
});

BoxingDetector.Report("Store in IEquatable<Point> var", () =>
{
    // Assigning a struct to an interface variable boxes it.
    StoreInInterface(q1, q2);
});

// ── Footer ─────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("  " + new string('-', 70));
Console.WriteLine();
Console.WriteLine("  Key takeaways:");
Console.WriteLine("    - Use List<T> instead of ArrayList for value types.");
Console.WriteLine("    - Implement IEquatable<T> on structs; use typed Equals(T).");
Console.WriteLine("    - Prefer string interpolation ($\"\") over composite formatting.");
Console.WriteLine("    - Use generic constraints (where T : IInterface) instead of");
Console.WriteLine("      interface-typed variables to avoid boxing value types.");
Console.WriteLine();

// ============================================================================
// LOCAL FUNCTIONS (still part of top-level statements)
// ============================================================================

// Constrained generic call: T is substituted with Point at JIT time.
// The JIT emits constrained callvirt that calls Point.Equals(Point)
// directly without boxing. The struct is passed by ref internally.
static bool AcceptEquatable<T>(T a, T b) where T : IEquatable<T>
    => a.Equals(b);

// Accepting object: the caller must box the struct to pass it.
static void AcceptObject(object obj)
    => _ = obj.GetHashCode();

// Storing in an interface variable: boxes the struct.
// The interface variable holds a reference to the boxed copy on the heap.
static bool StoreInInterface(Point a, Point b)
{
    IEquatable<Point> iface = a;  // Boxing happens here!
    return iface.Equals(b);
}

// ============================================================================
// TYPE DECLARATIONS (must appear after all top-level statements)
// ============================================================================

/// <summary>
/// Measures managed heap allocations for a given action using
/// GC.GetAllocatedBytesForCurrentThread() for per-thread precision.
/// </summary>
static class BoxingDetector
{
    /// <summary>
    /// Executes the action and returns bytes allocated on the managed heap
    /// during its execution (current thread only).
    /// </summary>
    public static long MeasureAllocations(Action action)
    {
        // Force a collection to reduce noise from pending allocations.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return after - before;
    }

    /// <summary>
    /// Runs the action, measures allocations, and prints a formatted line.
    /// </summary>
    public static void Report(string label, Action action)
    {
        long bytes = MeasureAllocations(action);
        bool boxing = bytes > 0;
        string marker = boxing ? "Yes" : "No";
        Console.WriteLine($"  {label,-50} {bytes,8}  {marker}");
    }
}

/// <summary>
/// A simple value type that properly implements IEquatable&lt;Point&gt;.
/// Implementing IEquatable&lt;T&gt; on structs is critical for performance:
/// it provides a typed Equals(T) method that avoids boxing when called
/// directly or through EqualityComparer&lt;T&gt;.Default.
/// </summary>
struct Point : IEquatable<Point>
{
    public int X { get; }
    public int Y { get; }

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// Typed equality: no boxing. Both operands are Point values on the stack.
    /// Called by EqualityComparer&lt;Point&gt;.Default and by generic methods
    /// with IEquatable&lt;T&gt; constraints.
    /// </summary>
    public bool Equals(Point other)
        => X == other.X && Y == other.Y;

    /// <summary>
    /// Override of object.Equals(object). The parameter arrives as object
    /// (already boxed by the caller). We unbox and delegate to typed Equals.
    /// Always override this alongside IEquatable&lt;T&gt; for consistency.
    /// </summary>
    public override bool Equals(object? obj)
        => obj is Point other && Equals(other);

    /// <summary>
    /// Must override GetHashCode when overriding Equals.
    /// </summary>
    public override int GetHashCode()
        => HashCode.Combine(X, Y);

    public override string ToString()
        => $"({X}, {Y})";
}