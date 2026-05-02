// =============================================================================
// Exercise 04: LOH Fragmentation and Compaction
// =============================================================================
//
// Key Concepts:
//   The .NET GC divides the managed heap into Small Object Heap (SOH) and
//   Large Object Heap (LOH). Objects >= 85,000 bytes are allocated on the LOH.
//
//   Unlike the SOH (which compacts during Gen2 collection), the LOH is NOT
//   compacted during normal garbage collection. The reason is simple economics:
//   moving large objects means copying potentially megabytes of data (memcpy),
//   which is far more expensive than moving small objects. The GC avoids this
//   cost by default.
//
//   Instead, the LOH uses a FREE-LIST ALLOCATOR. When a large object is freed,
//   its memory block is added to a free list. New allocations scan this list
//   for a suitable gap. This works well when allocations are uniform, but
//   leads to FRAGMENTATION when objects of varying sizes are allocated and
//   freed in non-contiguous patterns — gaps too small for new requests appear,
//   wasting address space.
//
//   .NET provides GCSettings.LargeObjectHeapCompactionMode to request a
//   one-time LOH compaction. Setting it to CompactOnce causes the NEXT full
//   GC (Gen2) to compact the LOH, then the mode automatically resets to
//   Default. This is an escape hatch, not a routine operation.
//
//   Real-world scenarios where LOH fragmentation bites:
//     - Image processing pipelines (large pixel buffers of varying sizes)
//     - Serialization/deserialization of large payloads (JSON, protobuf)
//     - Large buffer pools in network servers
//     - In-memory caching of variable-size data
//
//   Best practice: use ArrayPool<T> (System.Buffers) to RENT and RETURN
//   large arrays instead of allocating new ones. This avoids LOH churn
//   entirely and is the single most effective mitigation.
// =============================================================================

using System.Runtime;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
const int ArrayCount = 200;          // Total number of large arrays to allocate
const int SmallArraySize = 100_000;  // ~100 KB (just above the 85 KB LOH threshold)
const int LargeArraySize = 500_000;  // ~500 KB
const int HugeArraySize = 5_000_000; // ~5 MB — the contiguous block we try to fit

// ---------------------------------------------------------------------------
// Phase 1: Create LOH fragmentation by interleaving large allocations
// ---------------------------------------------------------------------------
PrintPhase("Phase 1: Allocating large arrays with alternating sizes on the LOH");

// We allocate arrays alternating between ~100KB and ~500KB.
// We keep references to ALL of them initially. The alternating sizes
// are key — when we later free every other one, we create gaps of
// different sizes, which is the classic fragmentation pattern.
byte[][] arrays = new byte[ArrayCount][];

for (int i = 0; i < ArrayCount; i++)
{
    // Even indices get small arrays, odd indices get large arrays.
    // This interleaving maximizes fragmentation when we free one set.
    int size = (i % 2 == 0) ? SmallArraySize : LargeArraySize;
    arrays[i] = new byte[size];

    // Touch the first byte to ensure the array is actually committed in memory
    arrays[i][0] = (byte)(i & 0xFF);
}

Console.WriteLine($"  Allocated {ArrayCount} arrays on the LOH");
Console.WriteLine($"  Even indices: {SmallArraySize:N0} bytes (~100 KB)");
Console.WriteLine($"  Odd indices:  {LargeArraySize:N0} bytes (~500 KB)");
PrintMemoryState("After Phase 1 allocation");

// ---------------------------------------------------------------------------
// Phase 2: Release alternating references to create fragmentation
// ---------------------------------------------------------------------------
PrintPhase("Phase 2: Releasing every other array to create LOH gaps");

// By nulling out the LARGE arrays (odd indices), we create ~500 KB gaps
// between the surviving ~100 KB arrays. The free-list allocator will
// record these gaps, but they are scattered across the LOH.
int releasedCount = 0;
for (int i = 1; i < ArrayCount; i += 2)
{
    arrays[i] = null!;
    releasedCount++;
}

// Force a full GC to actually reclaim the memory and populate the free list.
// Note: this is a Gen2 collection, but WITHOUT LOH compaction by default.
GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true);

Console.WriteLine($"  Released {releasedCount} large arrays (~500 KB each)");
Console.WriteLine($"  Remaining: {ArrayCount - releasedCount} small arrays (~100 KB each)");
Console.WriteLine($"  The LOH now has ~{releasedCount} scattered free gaps of ~500 KB");
PrintMemoryState("After Phase 2 release + GC");

// ---------------------------------------------------------------------------
// Phase 3: Attempt to allocate a large contiguous block in fragmented LOH
// ---------------------------------------------------------------------------
PrintPhase("Phase 3: Attempting large contiguous allocation in fragmented LOH");

// The free-list allocator will try to find a contiguous block of HugeArraySize
// bytes. Even though the TOTAL free space may be sufficient, the allocator
// may need to extend the heap if no single gap is large enough.
// In severe cases (32-bit processes or constrained memory), this can fail
// with OutOfMemoryException even when total free memory seems adequate.
// That is the hallmark of fragmentation: plenty of total space, but no
// single contiguous region large enough.

long memoryBeforeHuge = GC.GetTotalMemory(forceFullCollection: false);

byte[]? hugeArray = null;
try
{
    hugeArray = new byte[HugeArraySize];
    hugeArray[0] = 0xAA; // Touch it
    Console.WriteLine($"  Successfully allocated {HugeArraySize:N0} byte array (~5 MB)");
    Console.WriteLine("  The runtime extended the LOH or found a large enough gap.");
    Console.WriteLine("  In a 64-bit process, the OS can usually provide more address space,");
    Console.WriteLine("  but the LOH still grows — wasting committed memory due to gaps.");
}
catch (OutOfMemoryException)
{
    Console.WriteLine($"  FAILED to allocate {HugeArraySize:N0} byte array!");
    Console.WriteLine("  LOH fragmentation prevented finding a contiguous block.");
}

long memoryAfterHuge = GC.GetTotalMemory(forceFullCollection: false);
Console.WriteLine($"  Memory delta from huge allocation: {memoryAfterHuge - memoryBeforeHuge:N0} bytes");
PrintMemoryState("After Phase 3 allocation attempt");

// ---------------------------------------------------------------------------
// Phase 4: Trigger LOH compaction
// ---------------------------------------------------------------------------
PrintPhase("Phase 4: Requesting LOH compaction via GCSettings");

// GCSettings.LargeObjectHeapCompactionMode = CompactOnce tells the GC:
//   "On the NEXT full (Gen2) blocking GC, compact the LOH."
// After that single compaction, the mode automatically resets to Default.
// This is intentionally a one-shot mechanism — LOH compaction is expensive
// (it must copy all surviving large objects to eliminate gaps) and should
// only be used when you know fragmentation is a problem.

long memoryBeforeCompaction = GC.GetTotalMemory(forceFullCollection: false);
Console.WriteLine($"  Memory before compaction: {memoryBeforeCompaction:N0} bytes");
Console.WriteLine($"  Current LOH compaction mode: {GCSettings.LargeObjectHeapCompactionMode}");

// Request compaction on the next full GC
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
Console.WriteLine($"  Set LOH compaction mode to: {GCSettings.LargeObjectHeapCompactionMode}");

// Now trigger the full GC that will perform the compaction
GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true);

long memoryAfterCompaction = GC.GetTotalMemory(forceFullCollection: false);
Console.WriteLine($"  Memory after compaction:  {memoryAfterCompaction:N0} bytes");
Console.WriteLine($"  Memory reclaimed by compaction: {memoryBeforeCompaction - memoryAfterCompaction:N0} bytes");
Console.WriteLine($"  LOH compaction mode reset to: {GCSettings.LargeObjectHeapCompactionMode}");

// ---------------------------------------------------------------------------
// Phase 5: Verify post-compaction state and retry large allocation
// ---------------------------------------------------------------------------
PrintPhase("Phase 5: Post-compaction memory state and retry");

PrintMemoryState("After LOH compaction");

// After compaction, the surviving objects on the LOH have been moved together,
// eliminating the gaps. The free space is now one large contiguous block at
// the end of the LOH, making large allocations much more likely to succeed
// without extending the heap.

// Release the huge array from Phase 3 if it was allocated
hugeArray = null;
GC.Collect(2, GCCollectionMode.Forced, blocking: true);

try
{
    byte[] retryArray = new byte[HugeArraySize];
    retryArray[0] = 0xBB;
    Console.WriteLine($"  Post-compaction: successfully allocated {HugeArraySize:N0} byte array");
    Console.WriteLine("  Compaction consolidated free space, making large allocations easier.");
}
catch (OutOfMemoryException)
{
    Console.WriteLine("  Post-compaction allocation still failed (very constrained environment).");
}

PrintMemoryState("Final state");

// ---------------------------------------------------------------------------
// Phase 6: Summary and best practices
// ---------------------------------------------------------------------------
PrintPhase("Phase 6: Summary");

Console.WriteLine("""
  Key takeaways:

  1. The LOH is NOT compacted during normal GC — large object movement is
     too expensive (proportional to object size via memcpy).

  2. The LOH uses a free-list allocator that reuses gaps left by freed objects.
     When objects of varying sizes are allocated and freed non-contiguously,
     the free list becomes fragmented — many small gaps, none large enough.

  3. GCSettings.LargeObjectHeapCompactionMode = CompactOnce is a one-shot
     escape hatch. It triggers compaction on the NEXT full blocking GC,
     then automatically resets to Default.

  4. BEST PRACTICE: Use System.Buffers.ArrayPool<T> to rent and return
     large arrays. This avoids LOH allocations entirely and eliminates
     fragmentation at the source. Example:
       var pool = ArrayPool<byte>.Shared;
       byte[] buffer = pool.Rent(500_000);
       try { /* use buffer */ }
       finally { pool.Return(buffer); }

  5. If you must allocate on the LOH, prefer uniform sizes — the free-list
     allocator handles uniform gaps much better than mixed sizes.
""");

// =============================================================================
// Helper methods
// =============================================================================

/// <summary>
/// Prints a formatted phase header to clearly separate output sections.
/// </summary>
static void PrintPhase(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 72));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 72));
}

/// <summary>
/// Reports current memory statistics including LOH-specific information
/// from GC.GetGCMemoryInfo().
/// </summary>
static void PrintMemoryState(string label)
{
    // GC.GetTotalMemory gives a quick approximation of total managed bytes.
    long totalMemory = GC.GetTotalMemory(forceFullCollection: false);

    // GCMemoryInfo provides detailed heap information including per-generation data.
    GCMemoryInfo memInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);

    Console.WriteLine();
    Console.WriteLine($"  --- Memory State: {label} ---");
    Console.WriteLine($"  Total managed memory:    {totalMemory:N0} bytes ({totalMemory / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"  Heap size (total):       {memInfo.HeapSizeBytes:N0} bytes ({memInfo.HeapSizeBytes / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"  Fragmented bytes:        {memInfo.FragmentedBytes:N0} bytes ({memInfo.FragmentedBytes / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"  GC generation counts:    Gen0={GC.CollectionCount(0)}, Gen1={GC.CollectionCount(1)}, Gen2={GC.CollectionCount(2)}");

    // Report per-generation heap sizes — generation 3 is the LOH, generation 4 is the POH
    // (Pinned Object Heap, .NET 5+).
    ReadOnlySpan<GCGenerationInfo> genInfo = memInfo.GenerationInfo;
    if (genInfo.Length > 3)
    {
        // Index 3 = LOH (Large Object Heap)
        Console.WriteLine($"  LOH size after GC:       {genInfo[3].SizeAfterBytes:N0} bytes ({genInfo[3].SizeAfterBytes / 1024.0 / 1024.0:F2} MB)");
        Console.WriteLine($"  LOH fragmentation:       {genInfo[3].FragmentationAfterBytes:N0} bytes ({genInfo[3].FragmentationAfterBytes / 1024.0 / 1024.0:F2} MB)");
    }
    if (genInfo.Length > 4)
    {
        // Index 4 = POH (Pinned Object Heap)
        Console.WriteLine($"  POH size after GC:       {genInfo[4].SizeAfterBytes:N0} bytes ({genInfo[4].SizeAfterBytes / 1024.0 / 1024.0:F2} MB)");
    }

    Console.WriteLine();
}