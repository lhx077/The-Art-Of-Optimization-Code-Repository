// ============================================================================
// Exercise 04 — Custom Fixed-Size Pinned Memory Pool
// ============================================================================
// Uses the pinned object heap as backing storage and manages fixed-size blocks
// through a thread-safe free list. BenchmarkDotNet compares pool throughput with
// repeated new byte[] allocations.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<PinnedMemoryPoolBenchmarks>();
    return;
}

using var pool = new FixedSizePinnedMemoryPool(blockSize: 256, blockCount: 8);
using FixedMemoryLease lease = pool.Rent();
lease.Span.Fill(0x2A);
Console.WriteLine("=== Fixed-Size Pinned Memory Pool Demo ===");
Console.WriteLine($"BlockSize={pool.BlockSize}, InUse={pool.BlocksInUse}, Free={pool.FreeBlocks}");

// Core implementation used by the demo and benchmark code below.
public sealed class FixedSizePinnedMemoryPool : IDisposable
{
    private readonly byte[] _buffer;
    private readonly ConcurrentStack<int> _freeList = new();
    private int _blocksInUse;
    private int _disposed;
    private long _totalRents;
    private long _totalReturns;

    public FixedSizePinnedMemoryPool(int blockSize, int blockCount)
    {
        if (blockSize <= 0 || blockCount <= 0)
        {
            throw new ArgumentOutOfRangeException();
        }

        BlockSize = blockSize;
        BlockCount = blockCount;
        // Pin the backing storage up front so slices can be handed out without later relocation.
        _buffer = GC.AllocateUninitializedArray<byte>(blockSize * blockCount, pinned: true);

        for (int i = blockCount - 1; i >= 0; i--)
        {
            _freeList.Push(i);
        }
    }

    public int BlockSize { get; }
    public int BlockCount { get; }
    // Read the shared state with visibility guarantees before making decisions.
    public int BlocksInUse => Volatile.Read(ref _blocksInUse);
    public int FreeBlocks => BlockCount - BlocksInUse;
    public long TotalRents => Interlocked.Read(ref _totalRents);
    public long TotalReturns => Interlocked.Read(ref _totalReturns);

    // Prefer a reusable instance before falling back to allocation.
    public FixedMemoryLease Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_freeList.TryPop(out int blockIndex))
        {
            throw new InvalidOperationException("No blocks are available in the pool.");
        }

        // Update counters before handing the lease out so diagnostics reflect active usage.
        Interlocked.Increment(ref _blocksInUse);
        Interlocked.Increment(ref _totalRents);
        return new FixedMemoryLease(this, blockIndex);
    }

    internal Span<byte> GetSpan(int blockIndex)
    {
        return _buffer.AsSpan(blockIndex * BlockSize, BlockSize);
    }

    // Place the instance back into the pool after the caller is done with it.
    internal void Return(int blockIndex)
    {
        // Push first so another renter can observe the block as soon as the lease ends.
        _freeList.Push(blockIndex);
        Interlocked.Decrement(ref _blocksInUse);
        Interlocked.Increment(ref _totalReturns);
    }

    public void Dispose()
    {
        // Mark the pool closed; the pinned backing array is then reclaimed with the object itself.
        _disposed = 1;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class FixedMemoryLease : IDisposable
{
    private FixedSizePinnedMemoryPool? _owner;
    private readonly int _blockIndex;

    internal FixedMemoryLease(FixedSizePinnedMemoryPool owner, int blockIndex)
    {
        _owner = owner;
        _blockIndex = blockIndex;
    }

    public Span<byte> Span => _owner is null
        ? throw new ObjectDisposedException(nameof(FixedMemoryLease))
        : _owner.GetSpan(_blockIndex);

    public void Dispose()
    {
        // Exchange guards against double-return if Dispose is called more than once.
        FixedSizePinnedMemoryPool? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Return(_blockIndex);
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class PinnedMemoryPoolBenchmarks
{
    private FixedSizePinnedMemoryPool _pool = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new FixedSizePinnedMemoryPool(256, 1024);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int NewByteArray()
    {
        int total = 0;
        for (int i = 0; i < 1000; i++)
        {
            byte[] buffer = new byte[256];
            buffer[0] = (byte)i;
            total += buffer[0];
        }

        return total;
    }

    [Benchmark]
    public int PoolRentAndReturn()
    {
        int total = 0;
        for (int i = 0; i < 1000; i++)
        {
            using FixedMemoryLease lease = _pool.Rent();
            lease.Span[0] = (byte)i;
            total += lease.Span[0];
        }

        return total;
    }
}
