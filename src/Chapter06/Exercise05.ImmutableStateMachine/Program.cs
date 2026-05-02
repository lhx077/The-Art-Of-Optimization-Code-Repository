using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 05 — Immutable State Machine: Undo/Redo with Structural Sharing
// ============================================================================
//
// Key concepts:
//
// ImmutableList<T> is backed by an AVL tree. When you call .Add(), a new path
// from the root to the inserted leaf is created, but every other node in the
// tree is shared with the previous version. This is "structural sharing" and
// makes each modification O(log n) in both time and memory.
//
// ImmutableArray<T> wraps a plain T[]. Any modification (Add, Remove, Set)
// copies the ENTIRE array. Each modification is therefore O(n), and building
// one element at a time is O(n²) total — catastrophic for large collections.
//
// The Builder pattern lets you construct the collection mutably (like List<T>
// or ArrayBuilder), then freeze it into an immutable snapshot with
// .ToImmutable(). This avoids the per-operation overhead entirely.
//
// Undo/Redo with immutable collections: because old versions of the list are
// never modified, every historical state is automatically preserved. Moving
// through history is just changing an index — O(1), no data copying needed.
//
// Expected benchmark results:
//   Builder >> IndividualAdd for both ImmutableList and ImmutableArray.
//   ImmutableList individual add is O(n log n) total.
//   ImmutableArray individual add is O(n²) total — much worse at large N.
//   UndoRedo push is O(n log n), but undo/redo traversal is O(1) per step.
// ============================================================================

BenchmarkRunner.Run<ImmutableBenchmarks>();

// ---------------------------------------------------------------------------
// UndoRedoHistory<T> — an undo/redo state machine built on ImmutableList<T>
// ---------------------------------------------------------------------------
// All previous states are preserved via structural sharing. Undo and Redo
// simply move an index; they never copy or rebuild data.
// ---------------------------------------------------------------------------
class UndoRedoHistory<T>
{
    // ImmutableList uses an AVL tree internally. Old versions remain intact
    // when new versions are created — this is what makes undo "free".
    private ImmutableList<T> _states;
    private int _currentIndex;

    public UndoRedoHistory(T initialState)
    {
        _states = ImmutableList.Create(initialState);
        _currentIndex = 0;
    }

    /// <summary>Current state in the history timeline.</summary>
    public T Current => _states[_currentIndex];

    /// <summary>Total number of states stored (including redo-able futures).</summary>
    public int HistoryCount => _states.Count;

    public bool CanUndo => _currentIndex > 0;
    public bool CanRedo => _currentIndex < _states.Count - 1;

    /// <summary>
    /// Push a new state. Any "future" states beyond the current position are
    /// discarded (standard undo/redo semantics). The new ImmutableList shares
    /// structure with the old one — only O(log n) new nodes are allocated.
    /// </summary>
    public void Push(T state)
    {
        // Discard any redo states beyond the current position.
        // GetRange returns a new ImmutableList that shares nodes with the
        // original tree — no element-by-element copying.
        if (_currentIndex < _states.Count - 1)
        {
            _states = _states.GetRange(0, _currentIndex + 1);
        }

        // Add the new state. ImmutableList.Add creates a new AVL tree path
        // from root to the new leaf, sharing all other nodes.
        _states = _states.Add(state);
        _currentIndex++;
    }

    /// <summary>
    /// Undo: move back one step. O(1) — just decrements the index.
    /// The old state is still in the list; nothing is copied or rebuilt.
    /// </summary>
    public T Undo()
    {
        if (!CanUndo)
            throw new InvalidOperationException("Nothing to undo.");
        _currentIndex--;
        return _states[_currentIndex];
    }

    /// <summary>
    /// Redo: move forward one step. O(1) — just increments the index.
    /// </summary>
    public T Redo()
    {
        if (!CanRedo)
            throw new InvalidOperationException("Nothing to redo.");
        _currentIndex++;
        return _states[_currentIndex];
    }
}

// ---------------------------------------------------------------------------
// Benchmarks
// ---------------------------------------------------------------------------
[MemoryDiagnoser]
public class ImmutableBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int N;

    // -----------------------------------------------------------------------
    // ImmutableList — individual Add
    // Each .Add() creates a new AVL-tree path (O(log n) nodes).
    // Total: O(n log n) time, O(n log n) extra allocations.
    // -----------------------------------------------------------------------
    [Benchmark]
    public ImmutableList<int> ImmutableList_IndividualAdd()
    {
        var list = ImmutableList<int>.Empty;
        for (int i = 0; i < N; i++)
        {
            list = list.Add(i);
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // ImmutableList — Builder pattern
    // Internally a mutable AVL tree; .Add() is O(log n) but avoids creating
    // a new root snapshot each time. ToImmutable() freezes it in O(1).
    // Much less GC pressure than individual Add.
    // -----------------------------------------------------------------------
    [Benchmark]
    public ImmutableList<int> ImmutableList_Builder()
    {
        var builder = ImmutableList.CreateBuilder<int>();
        for (int i = 0; i < N; i++)
        {
            builder.Add(i);
        }
        return builder.ToImmutable();
    }

    // -----------------------------------------------------------------------
    // ImmutableArray — individual Add
    // ImmutableArray wraps a plain T[]. Every .Add() allocates a brand-new
    // array and copies all existing elements. O(n) per Add, O(n²) total.
    // This is the worst-case scenario — avoid for incremental construction.
    // -----------------------------------------------------------------------
    [Benchmark]
    public ImmutableArray<int> ImmutableArray_IndividualAdd()
    {
        var arr = ImmutableArray<int>.Empty;
        for (int i = 0; i < N; i++)
        {
            arr = arr.Add(i);
        }
        return arr;
    }

    // -----------------------------------------------------------------------
    // ImmutableArray — Builder pattern
    // Builder uses a mutable List<T> internally. Add is O(1) amortized.
    // ToImmutable() copies once into a final T[]. Total: O(n).
    // -----------------------------------------------------------------------
    [Benchmark]
    public ImmutableArray<int> ImmutableArray_Builder()
    {
        var builder = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < N; i++)
        {
            builder.Add(i);
        }
        return builder.ToImmutable();
    }

    // -----------------------------------------------------------------------
    // UndoRedo — Push N states, Undo N/2, Redo N/4
    // Push: O(log n) per operation (ImmutableList.Add under the hood).
    // Undo/Redo: O(1) each — just moves an index. No data is copied because
    // all historical snapshots are preserved via structural sharing.
    // -----------------------------------------------------------------------
    [Benchmark]
    public int UndoRedo_PushAndUndo()
    {
        var history = new UndoRedoHistory<int>(0);

        // Push N states — each push is O(log n) due to ImmutableList.Add
        for (int i = 1; i <= N; i++)
        {
            history.Push(i);
        }

        // Undo N/2 times — O(1) each, just decrementing an index
        int undoSteps = N / 2;
        int state = 0;
        for (int i = 0; i < undoSteps; i++)
        {
            state = history.Undo();
        }

        // Redo N/4 times — O(1) each, just incrementing an index
        int redoSteps = N / 4;
        for (int i = 0; i < redoSteps; i++)
        {
            state = history.Redo();
        }

        return state;
    }
}
