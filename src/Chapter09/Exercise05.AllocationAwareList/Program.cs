// ============================================================================
// Exercise 05 — Allocation-Aware List Wrapper
// ============================================================================
// AllocationAwareList<T> wraps a standard List<T> and provides additional
// allocation-awareness features:
//
//   1. Records initial capacity at construction time.
//   2. CapacityChanges property reports the full history of capacity changes,
//      including the old capacity, new capacity, and estimated bytes allocated
//      for each resize event.
//   3. EstimatedAllocationBytes property reports the cumulative bytes
//      allocated by internal array resizing over the list's lifetime.
//   4. OptimalInitialCapacity() method analyzes usage history and suggests
//      the best initial capacity to minimize resize allocations.
//
// This exercise demonstrates the hidden allocation cost of List<T> resizing
// and helps build intuition for choosing initial capacities.
// ============================================================================

using System.Collections;
using System.Runtime.CompilerServices;

// --- Demonstration ---
Console.WriteLine("=== Allocation-Aware List Demo ===");
Console.WriteLine();

// Scenario 1: Default capacity (0), add 100 items
Console.WriteLine("--- Scenario 1: Default capacity, 100 items ---");
var list1 = new AllocationAwareList<int>();
for (int i = 0; i < 100; i++)
    list1.Add(i);

PrintReport(list1);

// Scenario 2: Pre-sized to 100, add 100 items
Console.WriteLine("--- Scenario 2: Capacity 100, 100 items ---");
var list2 = new AllocationAwareList<int>(100);
for (int i = 0; i < 100; i++)
    list2.Add(i);

PrintReport(list2);

// Scenario 3: Pre-sized to 50, add 100 items (one resize needed)
Console.WriteLine("--- Scenario 3: Capacity 50, 100 items ---");
var list3 = new AllocationAwareList<int>(50);
for (int i = 0; i < 100; i++)
    list3.Add(i);

PrintReport(list3);

// Scenario 4: Reference types — larger per-element cost
Console.WriteLine("--- Scenario 4: Default capacity, 1000 strings ---");
var list4 = new AllocationAwareList<string>();
for (int i = 0; i < 1000; i++)
    list4.Add($"item-{i}");

PrintReport(list4);

// Scenario 5: Multiple usage cycles to show OptimalInitialCapacity learning
Console.WriteLine("--- Scenario 5: Usage history analysis ---");
var tracker = new UsageHistoryTracker<int>();
for (int cycle = 0; cycle < 10; cycle++)
{
    var list = new AllocationAwareList<int>(tracker: tracker);
    int itemCount = 50 + cycle * 20; // Varying sizes: 50, 70, 90, ..., 230
    for (int i = 0; i < itemCount; i++)
        list.Add(i);
    tracker.RecordUsage(list);
}

Console.WriteLine($"  Usage cycles recorded: {tracker.CycleCount}");
Console.WriteLine($"  Max count observed:    {tracker.MaxCountObserved}");
Console.WriteLine($"  Avg count observed:    {tracker.AverageCountObserved:F0}");
Console.WriteLine($"  Optimal initial cap:   {tracker.OptimalInitialCapacity()}");
Console.WriteLine($"  (P90 of observed counts, rounded to next power of 2)");

static void PrintReport<T>(AllocationAwareList<T> list)
{
    Console.WriteLine($"  Initial Capacity:    {list.InitialCapacity}");
    Console.WriteLine($"  Final Count:         {list.Count}");
    Console.WriteLine($"  Final Capacity:      {list.Capacity}");
    Console.WriteLine($"  Resize Count:        {list.CapacityChanges.Count}");
    Console.WriteLine($"  Est. Total Alloc:    {list.EstimatedAllocationBytes:N0} bytes");

    if (list.CapacityChanges.Count > 0)
    {
        Console.WriteLine($"  Capacity Changes:");
        foreach (var change in list.CapacityChanges)
        {
            Console.WriteLine($"    {change.OldCapacity,6} -> {change.NewCapacity,6}  " +
                              $"(+{change.EstimatedBytesAllocated:N0} bytes)");
        }
    }

    Console.WriteLine($"  Optimal Initial Cap: {list.OptimalInitialCapacity()}");
    Console.WriteLine();
}

// ============================================================================
// Capacity change record
// ============================================================================
public readonly record struct CapacityChange(int OldCapacity, int NewCapacity, long EstimatedBytesAllocated);

// ============================================================================
// AllocationAwareList<T> — wraps List<T> with allocation tracking
// ============================================================================
public class AllocationAwareList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _inner;
    private readonly List<CapacityChange> _capacityChanges = [];
    private readonly UsageHistoryTracker<T>? _tracker;
    private int _lastKnownCapacity;
    private long _totalAllocatedBytes;

    public int InitialCapacity { get; }

    public AllocationAwareList(int capacity = 0, UsageHistoryTracker<T>? tracker = null)
    {
        _inner = capacity > 0 ? new List<T>(capacity) : [];
        InitialCapacity = capacity;
        _lastKnownCapacity = _inner.Capacity;
        _tracker = tracker;

        // Record the initial array allocation (if capacity > 0)
        if (_inner.Capacity > 0)
        {
            long initialBytes = EstimateArrayBytes(_inner.Capacity);
            _totalAllocatedBytes += initialBytes;
        }
    }

    // -----------------------------------------------------------------------
    // Allocation tracking properties
    // -----------------------------------------------------------------------
    public IReadOnlyList<CapacityChange> CapacityChanges => _capacityChanges;

    public long EstimatedAllocationBytes => _totalAllocatedBytes;

    // Suggests the optimal initial capacity based on the final count.
    // Uses the actual count (not capacity) to avoid over-provisioning,
    // rounded up to the next power of 2 for alignment with List<T>'s
    // internal growth strategy.
    public int OptimalInitialCapacity()
    {
        if (_inner.Count == 0) return 0;
        return RoundUpToPowerOf2(_inner.Count);
    }

    // -----------------------------------------------------------------------
    // IList<T> / ICollection<T> implementation — delegates to inner list
    // with capacity change detection after mutating operations.
    // -----------------------------------------------------------------------
    public T this[int index]
    {
        get => _inner[index];
        set => _inner[index] = value;
    }

    public int Count => _inner.Count;
    public int Capacity => _inner.Capacity;
    public bool IsReadOnly => false;

    public void Add(T item)
    {
        _inner.Add(item);
        DetectCapacityChange();
    }

    public void AddRange(IEnumerable<T> collection)
    {
        _inner.AddRange(collection);
        DetectCapacityChange();
    }

    public void Insert(int index, T item)
    {
        _inner.Insert(index, item);
        DetectCapacityChange();
    }

    public bool Remove(T item) => _inner.Remove(item);
    public void RemoveAt(int index) => _inner.RemoveAt(index);
    public void Clear() => _inner.Clear();
    public bool Contains(T item) => _inner.Contains(item);
    public int IndexOf(T item) => _inner.IndexOf(item);
    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetectCapacityChange()
    {
        int currentCapacity = _inner.Capacity;
        if (currentCapacity != _lastKnownCapacity)
        {
            long newArrayBytes = EstimateArrayBytes(currentCapacity);
            _capacityChanges.Add(new CapacityChange(
                _lastKnownCapacity,
                currentCapacity,
                newArrayBytes));
            _totalAllocatedBytes += newArrayBytes;
            _lastKnownCapacity = currentCapacity;
        }
    }

    // Estimates the heap allocation size for a T[] of the given length.
    // Array overhead: 24 bytes on 64-bit (sync block + method table + length).
    // Element size: Unsafe.SizeOf<T>() for value types, 8 bytes for references.
    private static long EstimateArrayBytes(int length)
    {
        const int ArrayOverhead = 24; // 64-bit: 8 (sync) + 8 (MT) + 8 (length)
        int elementSize = RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? IntPtr.Size
            : Unsafe.SizeOf<T>();
        return ArrayOverhead + (long)length * elementSize;
    }

    private static int RoundUpToPowerOf2(int value)
    {
        if (value <= 0) return 0;
        // Use bit manipulation: subtract 1, fill all lower bits, add 1
        uint v = (uint)(value - 1);
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return (int)(v + 1);
    }
}

// ============================================================================
// Usage history tracker — collects statistics across multiple list lifecycles
// to recommend optimal initial capacity based on observed patterns.
// ============================================================================
public class UsageHistoryTracker<T>
{
    private readonly List<int> _observedCounts = [];

    public int CycleCount => _observedCounts.Count;
    public int MaxCountObserved => _observedCounts.Count > 0 ? _observedCounts.Max() : 0;
    public double AverageCountObserved => _observedCounts.Count > 0 ? _observedCounts.Average() : 0;

    public void RecordUsage(AllocationAwareList<T> list)
    {
        _observedCounts.Add(list.Count);
    }

    // Returns the optimal initial capacity based on P90 of observed counts,
    // rounded to the next power of 2. P90 balances between over-provisioning
    // and avoiding resizes for the majority of usage cycles.
    public int OptimalInitialCapacity()
    {
        if (_observedCounts.Count == 0) return 0;

        var sorted = _observedCounts.OrderBy(x => x).ToList();
        int p90Index = (int)(sorted.Count * 0.9);
        if (p90Index >= sorted.Count) p90Index = sorted.Count - 1;
        int p90Value = sorted[p90Index];

        return RoundUpToPowerOf2(p90Value);
    }

    private static int RoundUpToPowerOf2(int value)
    {
        if (value <= 0) return 0;
        uint v = (uint)(value - 1);
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return (int)(v + 1);
    }
}
