// ============================================================================
// Exercise 01 — Struct Design: Mutable vs. Readonly Structs and the
//               Defensive Copy Problem
// ============================================================================
//
// Purpose:
//   Value types (structs) in C# live on the stack or inline within their
//   containing object, avoiding heap allocation and GC pressure. However,
//   the compiler must guarantee that a `readonly` location (field, `in`
//   parameter, etc.) is never mutated. When a struct is NOT declared as
//   `readonly struct`, the compiler cannot prove that calling a method on
//   it won't mutate its state. To preserve the readonly guarantee, the
//   compiler silently creates a DEFENSIVE COPY of the struct before every
//   method call. This hidden copy has real performance cost — especially
//   in tight loops.
//
// What is a defensive copy?
//   When you have a readonly reference to a non-readonly struct (e.g. a
//   `readonly` field or an `in` parameter), the compiler copies the struct
//   to a temporary local, then calls the method on the copy. The IL:
//     ldarg.0                    // load `this`
//     ldfld  _mutableField       // load the field value
//     stloc.0                    // store into a LOCAL COPY  <-- defensive copy!
//     ldloca.s 0                 // load address of the copy
//     call   MutableRectangle::Area()
//
//   With a readonly struct, the IL is simply:
//     ldarg.0
//     ldflda _readonlyField      // load FIELD ADDRESS directly (no copy!)
//     call   ReadOnlyRectangle::Area()
//
//   For a 16-byte struct (4 x float), each defensive copy is 16 bytes.
//   In a loop of 1000 iterations calling 2 methods, that's 2000 copies =
//   32 KB of unnecessary stack traffic.
//
// Why readonly struct eliminates defensive copies:
//   Declaring `readonly struct` tells the compiler that no instance method
//   can mutate any field. With this guarantee, the compiler can safely
//   invoke methods directly on the original storage without copying.
//
// The `in` parameter modifier:
//   `in` passes a struct by readonly reference (pointer), avoiding the
//   call-site copy. For a readonly struct, no defensive copies are needed
//   inside the callee either. For a NON-readonly struct, `in` is actually
//   WORSE than by-value because every method call triggers a defensive
//   copy (the parameter is readonly, but the struct type is not).
//
//   For small structs (<=16 bytes), pass-by-value may equal or beat `in`
//   because the CPU can pass small structs in registers, while `in` forces
//   a memory address indirection (pointer dereference).
//
// Expected results:
//   - AsField_ReadOnly should be faster than AsField_Mutable due to no
//     defensive copies on the readonly field.
//   - AsParameter_ReadOnly_In should match or slightly beat
//     AsParameter_Mutable for larger iteration counts.
//   - AsReturn_Mutable and AsReturn_ReadOnly should be equivalent — the
//     readonly modifier has no effect on return-value performance.
//   - Zero heap allocations across all benchmarks.
// ============================================================================

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<RectangleBenchmarks>();

// ---------------------------------------------------------------------------
// MutableRectangle — a standard mutable struct
// ---------------------------------------------------------------------------
// This struct has get/set properties, so its methods COULD mutate state.
// The compiler cannot prove otherwise. When stored in a readonly context
// (readonly field, `in` parameter), every method call triggers a defensive
// copy to protect the original storage from potential mutation.
// ---------------------------------------------------------------------------
public struct MutableRectangle
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public MutableRectangle(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Area() => Width * Height;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(float px, float py)
        => px >= X && px <= X + Width && py >= Y && py <= Y + Height;

    // Inflate mutates in place — this is what makes the struct "mutable"
    // and is the reason the compiler must generate defensive copies when
    // this struct is accessed through a readonly reference.
    public void Inflate(float dx, float dy)
    {
        X -= dx;
        Y -= dy;
        Width += dx * 2;
        Height += dy * 2;
    }
}

// ---------------------------------------------------------------------------
// ReadOnlyRectangle — a readonly struct (the preferred pattern)
// ---------------------------------------------------------------------------
// The `readonly` modifier on the struct declaration guarantees that NO
// instance member can mutate any field. The compiler enforces this at
// compile time. Because the compiler knows the struct is immutable, it
// NEVER generates defensive copies, regardless of context:
//   - readonly fields:  no copy needed
//   - `in` parameters:  no copy needed
//   - ref readonly:     no copy needed
// ---------------------------------------------------------------------------
public readonly struct ReadOnlyRectangle
{
    public readonly float X;
    public readonly float Y;
    public readonly float Width;
    public readonly float Height;

    public ReadOnlyRectangle(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Area() => Width * Height;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(float px, float py)
        => px >= X && px <= X + Width && py >= Y && py <= Y + Height;

    // Inflate returns a NEW instance instead of mutating in place.
    // This is the idiomatic pattern for readonly structs — all "mutation"
    // operations return new values, just like System.DateTime or
    // System.Numerics.Vector3.
    public ReadOnlyRectangle Inflate(float dx, float dy)
        => new(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);
}

// ---------------------------------------------------------------------------
// Benchmark class
// ---------------------------------------------------------------------------
// [MemoryDiagnoser] adds columns for heap allocations per invocation.
// All benchmarks here should report 0 bytes allocated — any allocation
// would indicate accidental boxing (e.g., calling a method through an
// interface on a struct without constrained dispatch).
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class RectangleBenchmarks
{
    // -----------------------------------------------------------------------
    // THE DEFENSIVE COPY TRAP:
    // Both fields below are `readonly`. For _mutableField, every method
    // call triggers a defensive copy because MutableRectangle is NOT a
    // readonly struct. For _readonlyField, no copies are ever made because
    // ReadOnlyRectangle IS a readonly struct.
    //
    // This is the key insight: `readonly` on the FIELD + non-readonly STRUCT
    // = defensive copies on every method call. The fix: make the struct
    // `readonly struct`.
    // -----------------------------------------------------------------------
    private readonly MutableRectangle _mutableField = new(10, 20, 100, 50);
    private readonly ReadOnlyRectangle _readonlyField = new(10, 20, 100, 50);

    private const int Iterations = 1000;

    // -----------------------------------------------------------------------
    // Scenario 1: Readonly field + mutable struct = DEFENSIVE COPIES
    // -----------------------------------------------------------------------
    // Each call to _mutableField.Area() and _mutableField.Contains()
    // generates a 16-byte defensive copy. Over 1000 iterations with 2
    // calls each, that's 2000 hidden copies = 32 KB of stack traffic.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public float AsField_Mutable()
    {
        float sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += _mutableField.Area();
            _ = _mutableField.Contains(50, 30);
        }
        return sum;
    }

    // -----------------------------------------------------------------------
    // Scenario 2: Readonly field + readonly struct = NO DEFENSIVE COPIES
    // -----------------------------------------------------------------------
    // Because ReadOnlyRectangle is a `readonly struct`, the compiler knows
    // that Area() and Contains() cannot mutate the field. It calls the
    // methods directly on the field's address — zero copies, zero overhead.
    // This should be measurably faster than AsField_Mutable.
    // -----------------------------------------------------------------------
    [Benchmark]
    public float AsField_ReadOnly()
    {
        float sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += _readonlyField.Area();
            _ = _readonlyField.Contains(50, 30);
        }
        return sum;
    }

    // -----------------------------------------------------------------------
    // Scenario 3: Pass mutable struct by value
    // -----------------------------------------------------------------------
    // When a struct is passed by value, the caller copies it onto the
    // callee's stack frame. Inside the method, the parameter is a local
    // mutable copy — no defensive copies are needed because the method
    // owns its copy and can freely mutate it.
    //
    // For small structs (<=16 bytes on x64), the JIT may pass the struct
    // in registers instead of on the stack, making this very efficient.
    // Our MutableRectangle is exactly 16 bytes (4 x float), right at the
    // threshold where register passing is possible.
    // -----------------------------------------------------------------------
    [Benchmark]
    public float AsParameter_Mutable()
    {
        var rect = new MutableRectangle(10, 20, 100, 50);
        return CallWithMutableByValue(rect);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float CallWithMutableByValue(MutableRectangle r)
    {
        float sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += r.Area();
            _ = r.Contains(50, 30);
        }
        return sum;
    }

    // -----------------------------------------------------------------------
    // Scenario 4: Pass readonly struct by `in` reference
    // -----------------------------------------------------------------------
    // The `in` modifier passes the struct by readonly reference (a pointer),
    // avoiding the 16-byte copy at the call site. Inside the callee, the
    // parameter is a readonly reference. Because ReadOnlyRectangle is a
    // `readonly struct`, no defensive copies are generated for method calls
    // on the parameter — the compiler knows Area() and Contains() won't
    // mutate the struct.
    //
    // IMPORTANT: If we used `in MutableRectangle` instead, every method
    // call inside the callee would trigger a defensive copy — making it
    // SLOWER than pass-by-value. That's why `in` should only be used with
    // readonly structs.
    //
    // For this 16-byte struct, `in` may not outperform by-value because
    // the CPU can pass small structs in registers directly, while `in`
    // forces a memory address indirection (pointer dereference). The `in`
    // keyword shines most for structs larger than ~16 bytes.
    // -----------------------------------------------------------------------
    [Benchmark]
    public float AsParameter_ReadOnly_In()
    {
        var rect = new ReadOnlyRectangle(10, 20, 100, 50);
        return CallWithReadOnlyByIn(in rect);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float CallWithReadOnlyByIn(in ReadOnlyRectangle r)
    {
        float sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += r.Area();
            _ = r.Contains(50, 30);
        }
        return sum;
    }

    // -----------------------------------------------------------------------
    // Scenario 5: Create and return a mutable struct
    // -----------------------------------------------------------------------
    // Measures the cost of constructing and returning a MutableRectangle.
    // The readonly modifier has no effect on return-value semantics — the
    // struct is always copied into the caller's return slot regardless.
    // This serves as a control group alongside AsReturn_ReadOnly.
    // -----------------------------------------------------------------------
    [Benchmark]
    public MutableRectangle AsReturn_Mutable()
    {
        return new MutableRectangle(10, 20, 100, 50);
    }

    // -----------------------------------------------------------------------
    // Scenario 6: Create and return a readonly struct
    // -----------------------------------------------------------------------
    // Should perform identically to AsReturn_Mutable. The readonly modifier
    // does not affect construction or return-value copying. Both scenarios
    // confirm that `readonly struct` has zero overhead for construction and
    // return patterns.
    // -----------------------------------------------------------------------
    [Benchmark]
    public ReadOnlyRectangle AsReturn_ReadOnly()
    {
        return new ReadOnlyRectangle(10, 20, 100, 50);
    }
}

// ---------------------------------------------------------------------------
// Expected Results Analysis
// ---------------------------------------------------------------------------
// When run in Release mode (dotnet run -c Release), the results should show:
//
// 1. AsField_ReadOnly FASTER than AsField_Mutable:
//    The readonly field + mutable struct combination forces 2000 defensive
//    copies per benchmark invocation. The readonly struct version has zero
//    copies. Expect a measurable speedup (potentially 1.5-3x depending on
//    JIT optimizations).
//
// 2. AsParameter_ReadOnly_In SIMILAR to AsParameter_Mutable:
//    For this 16-byte struct (exactly at the register-passing threshold),
//    the `in` reference may not significantly outperform by-value passing.
//    The benefit of `in` becomes more pronounced with larger structs (>16
//    bytes) where the copy cost at the call site is more significant.
//
// 3. AsReturn_Mutable EQUAL to AsReturn_ReadOnly:
//    Return-value performance is unaffected by the readonly modifier.
//    Both should show nearly identical timings, confirming that readonly
//    struct has zero overhead for construction and return patterns.
//
// 4. ZERO ALLOCATIONS:
//    The MemoryDiagnoser should report 0 bytes allocated for all methods,
//    confirming that we are measuring pure value-type operations without
//    any accidental boxing or heap allocation.
// ---------------------------------------------------------------------------