// ============================================================================
// Exercise 02 — Matrix Operations: Value Type vs Reference Type Performance
//               with readonly struct, in parameters, and class comparison
// ============================================================================
//
// Purpose:
//   This exercise compares three approaches to 2x2 matrix multiplication:
//
//   1. Struct passed by value — each call copies the full 32-byte struct
//      (4 doubles × 8 bytes) onto the stack.
//
//   2. Struct passed by `in` — each call passes an 8-byte pointer instead
//      of copying 32 bytes. Because Matrix2x2 is a `readonly struct`, the
//      compiler knows no method can mutate it, so NO defensive copy is made.
//      This is the sweet spot: the struct is large enough (32 bytes > 16-byte
//      register-passing threshold) that avoiding the copy matters.
//
//   3. Class passed by reference — the matrix lives on the managed heap.
//      Each matrix object requires ~56 bytes (16-byte object header + 32
//      bytes of fields + padding). Creating new matrices in a loop generates
//      heap allocations and GC pressure, which the struct versions avoid
//      entirely.
//
// Why 32 bytes is the key number:
//   On x64, the calling convention can pass structs up to ~16 bytes in
//   registers. Beyond that, the struct must be copied to the stack. A 2x2
//   matrix of doubles is exactly 32 bytes — well above this threshold —
//   making it an ideal candidate for `in` parameter optimization.
//
// Expected benchmark results:
//   - Struct_MultiplyByIn:    Fastest — no 32-byte copies, no heap alloc
//   - Struct_MultiplyByValue: Close behind — 32-byte copies per call, but
//                             still stack-only, no GC pressure
//   - Class_Multiply:         Slowest — heap allocation for every new matrix,
//                             GC pressure, cache-unfriendly indirection
//
//   The [MemoryDiagnoser] will show 0 bytes allocated for both struct
//   benchmarks, while the class benchmark allocates heavily.
// ============================================================================

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<MatrixBenchmarks>();

// 1. readonly struct Matrix2x2 — the optimal layout for immutable math types
// ============================================================================
// A readonly struct guarantees that no instance method can mutate state.
// This is essential for `in` parameter optimization: when the compiler sees
// a readonly struct passed via `in`, it knows the callee won't mutate it,
// so it skips the defensive copy that would otherwise be required.
//
// Size: 4 × sizeof(double) = 4 × 8 = 32 bytes.
// This is above the ~16-byte threshold where `in` starts to show benefits,
// because the CPU can no longer pass the struct entirely in registers.
// Each `in` call passes an 8-byte pointer instead of copying 32 bytes.
// ============================================================================

public readonly struct Matrix2x2
{
    public readonly double M00;
    public readonly double M01;
    public readonly double M10;
    public readonly double M11;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix2x2(double m00, double m01, double m10, double m11)
    {
        M00 = m00;
        M01 = m01;
        M10 = m10;
        M11 = m11;
    }

    /// <summary>The 2x2 identity matrix: [[1,0],[0,1]].</summary>
    public static Matrix2x2 Identity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(1, 0, 0, 1);
    }

    // Instance method: `other` (32 bytes) is passed by readonly reference.
    // Because both `this` and `other` belong to a readonly struct, no
    // defensive copies are generated — the JIT reads fields directly
    // through the pointer.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix2x2 Multiply(in Matrix2x2 other)
    {
        return new Matrix2x2(
            M00 * other.M00 + M01 * other.M10,
            M00 * other.M01 + M01 * other.M11,
            M10 * other.M00 + M11 * other.M10,
            M10 * other.M01 + M11 * other.M11);
    }

    // Static by-value: both 32-byte structs are copied onto the stack.
    // This is the traditional approach — works fine, but each call pays
    // the cost of copying 64 bytes total (32 per parameter).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix2x2 MultiplyByValue(Matrix2x2 a, Matrix2x2 b)
    {
        return new Matrix2x2(
            a.M00 * b.M00 + a.M01 * b.M10,
            a.M00 * b.M01 + a.M01 * b.M11,
            a.M10 * b.M00 + a.M11 * b.M10,
            a.M10 * b.M01 + a.M11 * b.M11);
    }

    // Static by-in: both parameters passed by readonly reference (8 bytes each).
    // Total parameter overhead: 16 bytes (two pointers) vs 64 bytes (two copies).
    // For a tight loop of millions of multiplications, this difference is measurable.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix2x2 MultiplyByIn(in Matrix2x2 a, in Matrix2x2 b)
    {
        return new Matrix2x2(
            a.M00 * b.M00 + a.M01 * b.M10,
            a.M00 * b.M01 + a.M01 * b.M11,
            a.M10 * b.M00 + a.M11 * b.M10,
            a.M10 * b.M01 + a.M11 * b.M11);
    }

    public override string ToString() => $"[{M00}, {M01}; {M10}, {M11}]";
}

// ============================================================================
// 2. class Matrix2x2Class — heap-allocated reference type for comparison
// ============================================================================
// Every `new Matrix2x2Class(...)` allocates on the managed heap:
//   - 8 bytes for the object header (sync block + type handle)
//   - 8 bytes for the method table pointer
//   - 32 bytes for the four double fields
//   - Total: ~48 bytes per object (may vary with alignment/padding)
//
// In a loop of N multiplications, this creates N heap objects. The GC must
// eventually collect all of them, causing pauses and cache pollution.
// The struct version creates zero heap objects — everything lives on the stack.
// ============================================================================

public class Matrix2x2Class
{
    public readonly double M00;
    public readonly double M01;
    public readonly double M10;
    public readonly double M11;

    public Matrix2x2Class(double m00, double m01, double m10, double m11)
    {
        M00 = m00;
        M01 = m01;
        M10 = m10;
        M11 = m11;
    }

    public static Matrix2x2Class Identity => new(1, 0, 0, 1);

    // Each call returns a NEW heap-allocated object.
    // The multiply logic is identical to the struct version — the only
    // difference is allocation strategy (heap vs stack).
    public Matrix2x2Class Multiply(Matrix2x2Class other)
    {
        return new Matrix2x2Class(
            M00 * other.M00 + M01 * other.M10,
            M00 * other.M01 + M01 * other.M11,
            M10 * other.M00 + M11 * other.M10,
            M10 * other.M01 + M11 * other.M11);
    }

    public override string ToString() => $"[{M00}, {M01}; {M10}, {M11}]";
}

// ============================================================================
// 3. Benchmarks — struct by-value vs struct by-in vs class
// ============================================================================
// We multiply consecutive pairs from pre-filled arrays and accumulate a result.
// This simulates a realistic workload (e.g., transform chains in a game engine).
//
// [MemoryDiagnoser] tracks heap allocations per operation:
//   - Struct benchmarks should show 0 B allocated (everything on the stack)
//   - Class benchmark should show significant allocation (one object per multiply)
// ============================================================================

[MemoryDiagnoser]
public class MatrixBenchmarks
{
    [Params(10_000, 1_000_000)]
    public int N;

    private Matrix2x2[] _structs = null!;
    private Matrix2x2Class[] _classes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42); // Fixed seed for reproducibility

        _structs = new Matrix2x2[N];
        _classes = new Matrix2x2Class[N];

        for (int i = 0; i < N; i++)
        {
            double m00 = rng.NextDouble(), m01 = rng.NextDouble();
            double m10 = rng.NextDouble(), m11 = rng.NextDouble();
            _structs[i] = new Matrix2x2(m00, m01, m10, m11);
            _classes[i] = new Matrix2x2Class(m00, m01, m10, m11);
        }
    }

    // Multiply consecutive pairs using pass-by-value.
    // Each call to MultiplyByValue copies two 32-byte structs (64 bytes total)
    // onto the stack. Over N/2 iterations, that's N/2 × 64 = 32N bytes copied.
    [Benchmark]
    public Matrix2x2 Struct_MultiplyByValue()
    {
        var result = Matrix2x2.Identity;
        for (int i = 0; i < N - 1; i++)
            result = Matrix2x2.MultiplyByValue(_structs[i], _structs[i + 1]);
        return result;
    }

    // Multiply consecutive pairs using `in` parameters.
    // Each call passes two 8-byte pointers instead of two 32-byte copies.
    // Over N/2 iterations, parameter overhead drops from 32N bytes to 8N bytes.
    // Because Matrix2x2 is a readonly struct, no defensive copies are generated.
    [Benchmark(Baseline = true)]
    public Matrix2x2 Struct_MultiplyByIn()
    {
        var result = Matrix2x2.Identity;
        for (int i = 0; i < N - 1; i++)
            result = Matrix2x2.MultiplyByIn(in _structs[i], in _structs[i + 1]);
        return result;
    }

    // Multiply consecutive pairs using the class version.
    // Each iteration calls `new Matrix2x2Class(...)`, allocating ~48 bytes on
    // the heap. For N = 1,000,000, that's ~48 MB of garbage generated in a
    // single benchmark invocation. The GC must collect all of it, adding
    // latency spikes and reducing throughput.
    [Benchmark]
    public Matrix2x2Class Class_Multiply()
    {
        var result = Matrix2x2Class.Identity;
        for (int i = 0; i < N - 1; i++)
            result = _classes[i].Multiply(_classes[i + 1]);
        return result;
    }

}
