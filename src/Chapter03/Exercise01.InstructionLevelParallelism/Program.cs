// ============================================================================
// Exercise 01 — Instruction-Level Parallelism: Dependency Chains vs.
//               Independent Accumulators
// ============================================================================
//
// Purpose:
//   Modern CPUs are superscalar — they can issue multiple instructions per
//   clock cycle across several execution units (ports). However, this
//   capability is only useful when the instructions are independent of one
//   another. When each instruction depends on the result of the previous
//   one, the CPU is forced to execute them strictly in sequence, leaving
//   most of its execution resources idle. This serial bottleneck is called
//   a "dependency chain."
//
//   This exercise makes the effect visible by benchmarking two ways of
//   computing a running product over an array of doubles:
//
//   1. SingleChain — a single accumulator where each multiply depends on
//      the result of the previous multiply, forming a long dependency chain.
//
//   2. FourIndependentChains — four separate accumulators, each processing
//      every 4th element. Because the four multiply streams are independent,
//      the CPU's out-of-order engine can overlap their latencies across
//      different execution ports simultaneously.
//
//   On a typical modern x86-64 processor, a double-precision multiply has
//   a latency of ~4–5 clock cycles but a throughput of one multiply per
//   cycle (or even two on recent microarchitectures with dual FMA units).
//   The single-chain version can only retire one multiply every 4–5 cycles
//   because each multiply must wait for the previous result. The four-chain
//   version keeps 4 multiplies in flight at once, saturating the pipeline
//   and approaching the throughput limit. The expected speedup is roughly
//   3–4x.
//
//   This technique — splitting a single accumulator into multiple
//   independent accumulators — is known as "loop unrolling with multiple
//   accumulators" and is one of the most classic and effective
//   instruction-level parallelism (ILP) optimizations.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<DependencyChainBenchmarks>();

// ---------------------------------------------------------------------------
// The [MemoryDiagnoser] attribute adds per-invocation allocation tracking.
// Neither benchmark method here should allocate, so the memory columns serve
// as a sanity check — any unexpected allocation would indicate an accidental
// boxing or closure capture that could distort the timing results.
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class DependencyChainBenchmarks
{
    // -----------------------------------------------------------------------
    // We test across three array sizes spanning two orders of magnitude.
    // The dependency-chain bottleneck is a per-element effect, so the ratio
    // between SingleChain and FourIndependentChains should remain roughly
    // constant regardless of N. Varying N simply confirms that the speedup
    // is structural (caused by ILP) rather than an artifact of cache size
    // or branch prediction at a particular array length.
    // -----------------------------------------------------------------------
    [Params(1_000, 10_000, 100_000)]
    public int N;

    private double[] _data = null!;

    // -----------------------------------------------------------------------
    // [GlobalSetup] runs once before measurement begins for each value of N.
    // We fill the array with values very close to 1.0 so that the running
    // product neither overflows to infinity nor underflows to zero, even for
    // large N. The slight variation (1.0 + (i % 7) * 0.0001) prevents the
    // compiler or CPU from recognizing a trivial constant-folding pattern
    // while keeping every element in the safe range [1.0000, 1.0006].
    // -----------------------------------------------------------------------
    [GlobalSetup]
    public void Setup()
    {
        _data = new double[N];
        for (int i = 0; i < N; i++)
        {
            _data[i] = 1.0 + (i % 7) * 0.0001;
        }
    }

    // -----------------------------------------------------------------------
    // SingleChain — the dependency-chain bottleneck
    // -----------------------------------------------------------------------
    // This method computes a running product using a single accumulator:
    //
    //   result *= data[0];   // must complete before …
    //   result *= data[1];   // … this can start, which must complete before …
    //   result *= data[2];   // … this can start, and so on.
    //
    // Each multiply instruction produces the input for the next one. On a
    // modern x86-64 core, a 64-bit floating-point multiply (MULSD / VMULSD)
    // has a latency of approximately 4–5 clock cycles. Because the next
    // multiply cannot even begin until the current one retires its result
    // to the destination register, the effective throughput is limited to
    // one multiply every 4–5 cycles — regardless of how many execution
    // units the CPU has available.
    //
    // The CPU's out-of-order engine can look ahead in the instruction
    // stream and find independent work to execute in parallel, but in this
    // loop there is none: the loop counter increment and the array bounds
    // check are trivially cheap and do not fill the 4-cycle gap. The
    // multiply unit sits idle for ~75% of the time.
    // -----------------------------------------------------------------------
    [Benchmark(Baseline = true)]
    public double SingleChain()
    {
        double result = 1.0;
        double[] data = _data;

        for (int i = 0; i < data.Length; i++)
        {
            result *= data[i];
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // FourIndependentChains — exploiting ILP with multiple accumulators
    // -----------------------------------------------------------------------
    // By splitting the single accumulator into four independent variables
    // (r0, r1, r2, r3), we create four separate dependency chains that have
    // no data dependency on each other:
    //
    //   Cycle 0:  r0 *= data[0]   r1 *= data[1]   r2 *= data[2]   r3 *= data[3]
    //   Cycle 1:  r0 *= data[4]   r1 *= data[5]   r2 *= data[6]   r3 *= data[7]
    //   ...
    //
    // The CPU's out-of-order execution engine detects that r0, r1, r2, and
    // r3 are independent and dispatches their multiplies to different
    // execution ports simultaneously. While r0's multiply from cycle 0 is
    // still in the pipeline (4–5 cycles of latency), the multiplies for
    // r1, r2, and r3 are already executing on other ports. By the time r0's
    // result is ready for its next multiply (at iteration i+4), the pipeline
    // has been kept busy with the other three chains.
    //
    // With 4 independent chains and a multiply latency of ~4–5 cycles, we
    // can keep the pipeline nearly fully saturated, achieving close to the
    // theoretical throughput of one multiply per cycle. This yields a
    // speedup of roughly 3–4x over the single-chain version.
    //
    // The final combination (r0 * r1 * r2 * r3) is a single operation
    // outside the hot loop and has negligible cost.
    //
    // Note: the tail loop handles the remaining elements when N is not
    // evenly divisible by 4. For large N this is a trivial fraction of
    // the total work.
    // -----------------------------------------------------------------------
    [Benchmark]
    public double FourIndependentChains()
    {
        double r0 = 1.0, r1 = 1.0, r2 = 1.0, r3 = 1.0;
        double[] data = _data;
        int len = data.Length;
        int limit = len - 3;

        int i = 0;
        for (; i < limit; i += 4)
        {
            r0 *= data[i];
            r1 *= data[i + 1];
            r2 *= data[i + 2];
            r3 *= data[i + 3];
        }

        // Handle remaining elements that don't fill a full group of 4.
        for (; i < len; i++)
        {
            r0 *= data[i];
        }

        return r0 * r1 * r2 * r3;
    }
}

// ---------------------------------------------------------------------------
// Expected Results Analysis
// ---------------------------------------------------------------------------
// When run in Release mode (dotnet run -c Release), the results should show:
//
// 1. CONSISTENT SPEEDUP ACROSS ALL N VALUES:
//    FourIndependentChains should be approximately 3–4x faster than
//    SingleChain for every value of N. This consistency confirms that the
//    speedup comes from instruction-level parallelism (a per-element
//    microarchitectural effect) rather than from cache behavior or
//    algorithmic differences.
//
// 2. LINEAR SCALING WITH N:
//    Both methods should scale linearly with N — doubling N should roughly
//    double the execution time. The slope of FourIndependentChains is
//    simply ~4x shallower than SingleChain's.
//
// 3. ZERO ALLOCATIONS:
//    The MemoryDiagnoser should report 0 bytes allocated for both methods,
//    confirming that we are measuring pure computation without GC noise.
//
// 4. WHY NOT 4x EXACTLY?
//    The theoretical maximum speedup with 4 accumulators is 4x (matching
//    the ~4-cycle multiply latency). In practice, you may see slightly
//    less due to: (a) front-end decode/dispatch limits, (b) competition
//    for execution ports with the loop control instructions, (c) memory
//    access latency if the array falls out of L1 cache at larger sizes,
//    and (d) the JIT compiler may partially unroll the single-chain loop
//    as well. Nonetheless, a 3–4x improvement from such a simple code
//    transformation is a compelling demonstration of why understanding
//    hardware pipelines matters for performance-critical code.
// ---------------------------------------------------------------------------
