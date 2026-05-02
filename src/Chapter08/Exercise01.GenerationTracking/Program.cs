// ============================================================================
// Exercise 01 — Object Generation and LOH Allocation Observation
// ============================================================================
// The .NET garbage collector organizes the managed heap into generations:
//
//   Gen 0 — Newly allocated small objects. Collected most frequently.
//   Gen 1 — Objects that survived one Gen 0 collection. Acts as a buffer
//            between short-lived (Gen 0) and long-lived (Gen 2) objects.
//   Gen 2 — Objects that survived at least two collections, plus all Large
//            Object Heap (LOH) allocations.
//
// The Large Object Heap (LOH):
//   Any object >= 85,000 bytes is allocated directly on the LOH, which is
//   logically part of Gen 2. This means:
//     1. LOH objects are "born" in Gen 2 — GC.GetGeneration() returns 2
//        immediately after allocation, with no prior collection needed.
//     2. LOH is NOT compacted by default. The runtime uses a free-list
//        allocator, which can lead to fragmentation over time.
//        (GCSettings.LargeObjectHeapCompactionMode can override this.)
//     3. Collecting LOH objects requires a full Gen 2 collection, which is
//        the most expensive GC operation — it pauses all managed threads
//        (in workstation GC) or uses background collection (server GC).
//
// Performance implications:
//   - Frequent LOH allocations cause frequent Gen 2 collections.
//   - LOH fragmentation wastes memory and can trigger OutOfMemoryException
//     even when total free memory is sufficient.
//   - Prefer object pooling (ArrayPool<T>) for large, short-lived buffers.
//   - The 85,000-byte threshold is an implementation detail of the CLR and
//     has remained unchanged since .NET Framework 1.0 through .NET 10.
//
// This exercise allocates byte arrays at various sizes around the 85,000-byte
// boundary and observes which generation each object lands in, clearly showing
// the SOH-to-LOH transition point.
// ============================================================================

// ---------------------------------------------------------------------------
// Step 1: Record GC collection counts before any allocations
// ---------------------------------------------------------------------------

int gen0Before = GC.CollectionCount(0);
int gen1Before = GC.CollectionCount(1);
int gen2Before = GC.CollectionCount(2);

Console.WriteLine("==========================================================");
Console.WriteLine("  Object Generation and LOH Allocation Observation");
Console.WriteLine("==========================================================");
Console.WriteLine();
// ---------------------------------------------------------------------------
// Step 2: Define the allocation sizes to test
// ---------------------------------------------------------------------------
// We pick sizes that span from well below the LOH threshold (1 KB) to well
// above it (200 KB), with fine-grained steps near the 85,000-byte boundary
// to pinpoint exactly where the transition occurs.

int[] sizes =
[
    1_024,      //   1 KB — comfortably in SOH
    10_240,     //  10 KB
    40_960,     //  40 KB
    61_440,     //  60 KB
    81_920,     //  80 KB — still SOH
    84_000,     //  ~82 KB — approaching the threshold
    84_900,     //  ~82.9 KB — very close
    84_990,     //  ~83 KB — just under
    84_999,     //  one byte below the threshold
    85_000,     //  exactly 85,000 bytes — the LOH threshold
    85_001,     //  one byte above
    85_100,     //  just over
    92_160,     //  90 KB — clearly LOH
    102_400,    // 100 KB
    153_600,    // 150 KB
    204_800,    // 200 KB — large LOH allocation
];

// ---------------------------------------------------------------------------
// Step 3: Allocate each array and record its generation immediately
// ---------------------------------------------------------------------------
// We store results in a list of AllocationResult records so we can print
// them in a formatted table after all allocations are complete.

List<AllocationResult> results = new(sizes.Length);

// We also keep references alive so the GC cannot collect them before we
// inspect their generations in Step 5.
List<byte[]> keepAlive = new(sizes.Length);

Console.WriteLine("Allocating objects and checking their generation...");
Console.WriteLine();

foreach (int size in sizes)
{
    // Allocate a byte array of the requested size.
    // Arrays >= 85,000 bytes go directly to the LOH (Gen 2).
    byte[] buffer = new byte[size];

    int generation = GC.GetGeneration(buffer);

    // An object is on the LOH if it is born into Gen 2 without ever being
    // promoted. Since we check immediately after allocation, Gen 2 here
    // means LOH placement.
    bool isLoh = generation == 2;

    results.Add(new AllocationResult(size, generation, isLoh));
    keepAlive.Add(buffer);
}

// ---------------------------------------------------------------------------
// Step 4: Print the allocation table
// ---------------------------------------------------------------------------

const string header = "  Size (bytes) |  Size (KB)  | Generation | LOH? ";
const string separator = "  -------------|-------------|------------|------";

Console.WriteLine(header);
Console.WriteLine(separator);

foreach (AllocationResult r in results)
{
    string lohMarker = r.IsLoh ? " YES" : "  no";
    string sizeKb = $"{r.SizeBytes / 1024.0,9:F1}";
    Console.WriteLine($"  {r.SizeBytes,12:N0} | {sizeKb} KB |    Gen {r.Generation}   | {lohMarker}");
}

Console.WriteLine();
Console.WriteLine($"  LOH threshold: 85,000 bytes (objects >= this size go to LOH)");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Step 5: Show GC collection counts after allocation
// ---------------------------------------------------------------------------

int gen0After = GC.CollectionCount(0);
int gen1After = GC.CollectionCount(1);
int gen2After = GC.CollectionCount(2);

Console.WriteLine("----------------------------------------------------------");
Console.WriteLine("  GC Collections During Allocation Phase");
Console.WriteLine("----------------------------------------------------------");
Console.WriteLine($"  Gen 0 collections: {gen0After - gen0Before}");
Console.WriteLine($"  Gen 1 collections: {gen1After - gen1Before}");
Console.WriteLine($"  Gen 2 collections: {gen2After - gen2Before}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Step 6: Force collections and observe generation promotion
// ---------------------------------------------------------------------------
// After a Gen 0 collection, surviving SOH objects are promoted to Gen 1.
// After a Gen 1 collection, survivors move to Gen 2.
// LOH objects remain in Gen 2 throughout — they are never promoted because
// they start there.

Console.WriteLine("----------------------------------------------------------");
Console.WriteLine("  Forcing GC Collections — Observing Promotion");
Console.WriteLine("----------------------------------------------------------");
Console.WriteLine();

// Force a Gen 0 collection. SOH objects that are still referenced will be
// promoted from Gen 0 → Gen 1.
GC.Collect(0, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();

Console.WriteLine("  After Gen 0 collection:");
PrintGenerations(keepAlive, sizes);

// Force a Gen 1 collection. Surviving Gen 1 objects promote to Gen 2.
GC.Collect(1, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();

Console.WriteLine("  After Gen 1 collection:");
PrintGenerations(keepAlive, sizes);

// Force a full Gen 2 collection. All surviving objects remain in Gen 2.
// This is the most expensive collection — it scans the entire heap
// including the LOH.
GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();

Console.WriteLine("  After Gen 2 (full) collection:");
PrintGenerations(keepAlive, sizes);

Console.WriteLine("==========================================================");
Console.WriteLine("  Key Takeaways:");
Console.WriteLine("  - Objects < 85,000 bytes start in Gen 0 (SOH)");
Console.WriteLine("  - Objects >= 85,000 bytes start in Gen 2 (LOH)");
Console.WriteLine("  - SOH objects get promoted: Gen 0 → Gen 1 → Gen 2");
Console.WriteLine("  - LOH objects stay in Gen 2 from birth — no promotion");
Console.WriteLine("  - Full GC (Gen 2) is needed to reclaim LOH memory");
Console.WriteLine("  - Use ArrayPool<T> to avoid repeated LOH allocations");
Console.WriteLine("==========================================================");

// Keep references alive until the very end so the GC does not collect them
// before we finish observing their generations.
GC.KeepAlive(keepAlive);

// ---------------------------------------------------------------------------
// Helper: Print current generation of each kept-alive object
// ---------------------------------------------------------------------------

static void PrintGenerations(List<byte[]> objects, int[] sizes)
{
    for (int i = 0; i < objects.Count; i++)
    {
        int gen = GC.GetGeneration(objects[i]);
        string label = sizes[i] >= 85_000 ? "LOH" : "SOH";
        Console.WriteLine($"    {sizes[i],12:N0} bytes → Gen {gen}  ({label})");
    }
    Console.WriteLine();
}

// ---------------------------------------------------------------------------
// Record type for storing allocation results
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable snapshot of a single allocation's observed generation.
/// </summary>
/// <param name="SizeBytes">The number of bytes allocated.</param>
/// <param name="Generation">The GC generation reported immediately after allocation.</param>
/// <param name="IsLoh">True if the object was placed on the Large Object Heap (Gen 2 at birth).</param>
record AllocationResult(int SizeBytes, int Generation, bool IsLoh);