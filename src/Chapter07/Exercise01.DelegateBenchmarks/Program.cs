using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<DelegateCallBenchmarks>();

// ---------------------------------------------------------------------------
// Interface + implementations used by the interface and generic benchmarks
// ---------------------------------------------------------------------------

public interface ITransformer
{
    int Transform(int value);
}

public class ClassTransformer : ITransformer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Transform(int value) => value + 1;
}

public struct StructTransformer : ITransformer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Transform(int value) => value + 1;
}

// ---------------------------------------------------------------------------
// Benchmark suite — Delegate, Interface, and Function-Pointer call overhead
// ---------------------------------------------------------------------------
//
// Expected ranking (fastest → slowest):
//
//   DirectCall ≈ GenericStructConstraint ≈ FunctionPointer
//       > CachedStaticDelegate ≈ CachedInstanceDelegate ≈ InterfaceCall
//           >> NewDelegateEachCall
//
// Why?
//   • Direct call:  The JIT can inline the callee entirely — zero overhead.
//   • Generic struct constraint:  The JIT generates a specialised version for
//     the concrete struct type, devirtualises the interface call, and inlines
//     the body.  Effectively identical to a direct call.
//   • Function pointer (delegate*):  A raw indirect call through a pointer.
//     No delegate object, no vtable — just one `calli` instruction.  Slightly
//     harder for the JIT to inline than a direct call, but no GC overhead.
//   • Cached delegate:  One indirection through the delegate's internal
//     function pointer (~1-2 ns overhead).  The delegate object itself is
//     allocated once and reused, so no GC pressure in the hot loop.
//   • Interface call:  Virtual dispatch via the vtable.  Comparable cost to a
//     cached delegate call.
//   • New delegate each call:  A *new* Func<int,int> is heap-allocated on
//     every single iteration.  This creates massive GC pressure and dominates
//     the benchmark time.
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class DelegateCallBenchmarks
{
    // ---- Cached delegates (allocated once, reused every iteration) ----
    private readonly Func<int, int> _cachedStaticDelegate = Increment;
    private readonly Func<int, int> _cachedInstanceDelegate;

    // ---- Interface reference (virtual dispatch) ----
    private readonly ITransformer _interfaceTransformer = new ClassTransformer();

    [Params(1_000_000)]
    public int N;

    public DelegateCallBenchmarks()
    {
        // Instance method delegate — the delegate captures 'this'
        _cachedInstanceDelegate = InstanceIncrement;
    }

    // ---- Target methods ----

    /// <summary>
    /// Static helper — the JIT can inline this when called directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Increment(int x) => x + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int InstanceIncrement(int x) => x + 1;

    // ---- Generic helper for devirtualised struct dispatch ----

    /// <summary>
    /// When T is a struct that implements ITransformer the JIT creates a
    /// specialised copy of this method, devirtualises the Transform call,
    /// and inlines the body — giving direct-call performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CallWithConstraint<T>(int value, T transformer)
        where T : ITransformer
        => transformer.Transform(value);

    // ==================================================================
    // Benchmarks
    // ==================================================================

    /// <summary>
    /// Baseline — direct static call.  The JIT inlines Increment() so the
    /// loop body compiles down to a single `inc` instruction.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int DirectCall()
    {
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = Increment(sum);
        return sum;
    }

    /// <summary>
    /// Cached Func&lt;int,int&gt; pointing to a static method.
    /// One indirection through the delegate's function pointer per call.
    /// No allocations in the hot loop.
    /// </summary>
    [Benchmark]
    public int CachedStaticDelegate()
    {
        var fn = _cachedStaticDelegate;
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = fn(sum);
        return sum;
    }

    /// <summary>
    /// Cached Func&lt;int,int&gt; pointing to an instance method.
    /// Same indirection cost as the static variant; the delegate also
    /// stores the target object reference.
    /// </summary>
    [Benchmark]
    public int CachedInstanceDelegate()
    {
        var fn = _cachedInstanceDelegate;
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = fn(sum);
        return sum;
    }

    /// <summary>
    /// A brand-new Func&lt;int,int&gt; is heap-allocated on EVERY iteration.
    /// This is the worst-case scenario: ~24-32 bytes allocated per call,
    /// causing enormous GC pressure.
    /// </summary>
    [Benchmark]
    public int NewDelegateEachCall()
    {
        int sum = 0;
        for (int i = 0; i < N; i++)
        {
            var fn = new Func<int, int>(Increment);
            sum = fn(sum);
        }
        return sum;
    }

    /// <summary>
    /// Call through an ITransformer interface reference.
    /// The runtime performs a vtable lookup — cost is comparable to a
    /// cached delegate call.
    /// </summary>
    [Benchmark]
    public int InterfaceCall()
    {
        var t = _interfaceTransformer;
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = t.Transform(sum);
        return sum;
    }

    /// <summary>
    /// Generic method constrained to ITransformer with a struct type arg.
    /// The JIT specialises for StructTransformer, devirtualises Transform,
    /// and inlines the body — performance matches a direct call.
    /// </summary>
    [Benchmark]
    public int GenericStructConstraint()
    {
        var t = new StructTransformer();
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = CallWithConstraint(sum, t);
        return sum;
    }

    /// <summary>
    /// Unsafe function pointer (delegate*).  A raw `calli` with no
    /// delegate object overhead.  Requires AllowUnsafeBlocks.
    /// Closest to a C-style function pointer — minimal overhead, but
    /// the JIT usually cannot inline through it.
    /// </summary>
    [Benchmark]
    public unsafe int FunctionPointer()
    {
        delegate*<int, int> fp = &Increment;
        int sum = 0;
        for (int i = 0; i < N; i++)
            sum = fp(sum);
        return sum;
    }
}
