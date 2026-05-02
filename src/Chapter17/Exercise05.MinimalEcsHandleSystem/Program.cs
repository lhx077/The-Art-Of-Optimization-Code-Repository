// ============================================================================
// Exercise 05 — Minimal ECS with Generational Handles
// ============================================================================
// Demonstrates a tiny ECS-like storage layer using generational handles and dense
// arrays. Systems process component spans in batches instead of object graphs.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<EcsBenchmarks>();
    return;
}

World world = new(capacity: 16);
EntityHandle entity = world.CreateEntity(new Position(1, 2), new Velocity(0.5f, -0.25f));
world.Update(2f);
Console.WriteLine("=== Minimal ECS Demo ===");
Console.WriteLine($"Handle: index={entity.Index}, generation={entity.Generation}");
Console.WriteLine($"Position after update: {world.GetPosition(entity)}");

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct EntityHandle(int Index, int Generation);
// Small immutable value objects keep measurement and transport code easy to inspect.
public record struct Position(float X, float Y);
// Small immutable value objects keep measurement and transport code easy to inspect.
public record struct Velocity(float X, float Y);

// Core implementation used by the demo and benchmark code below.
public sealed class World
{
    private readonly Position[] _positions;
    private readonly Velocity[] _velocities;
    private readonly int[] _generations;
    private readonly Stack<int> _freeList = new();
    private readonly bool[] _alive;
    private int _count;

    public World(int capacity)
    {
        _positions = new Position[capacity];
        _velocities = new Velocity[capacity];
        _generations = new int[capacity];
        _alive = new bool[capacity];
    }

    public EntityHandle CreateEntity(Position position, Velocity velocity)
    {
        int index = _freeList.Count > 0 ? _freeList.Pop() : _count++;
        _positions[index] = position;
        _velocities[index] = velocity;
        _alive[index] = true;
        return new EntityHandle(index, _generations[index]);
    }

    public void DestroyEntity(EntityHandle handle)
    {
        Validate(handle);
        _alive[handle.Index] = false;
        _generations[handle.Index]++;
        _freeList.Push(handle.Index);
    }

    public Position GetPosition(EntityHandle handle)
    {
        Validate(handle);
        return _positions[handle.Index];
    }

    public void Update(float deltaTime)
    {
        Span<Position> positions = _positions.AsSpan(0, _count);
        ReadOnlySpan<Velocity> velocities = _velocities.AsSpan(0, _count);
        Span<bool> alive = _alive.AsSpan(0, _count);

        const int batchSize = 128;
        for (int start = 0; start < positions.Length; start += batchSize)
        {
            int length = Math.Min(batchSize, positions.Length - start);
            Span<Position> positionBatch = positions.Slice(start, length);
            ReadOnlySpan<Velocity> velocityBatch = velocities.Slice(start, length);
            Span<bool> aliveBatch = alive.Slice(start, length);

            for (int i = 0; i < length; i++)
            {
                if (!aliveBatch[i])
                {
                    continue;
                }

                positionBatch[i].X += velocityBatch[i].X * deltaTime;
                positionBatch[i].Y += velocityBatch[i].Y * deltaTime;
            }
        }
    }

    private void Validate(EntityHandle handle)
    {
        if ((uint)handle.Index >= (uint)_count || !_alive[handle.Index] || _generations[handle.Index] != handle.Generation)
        {
            throw new InvalidOperationException("Invalid or stale entity handle.");
        }
    }
}

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class EcsBenchmarks
{
    private World _world = null!;
    private EntityHandle[] _handles = null!;

    [Params(100_000)]
    public int EntityCount;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(EntityCount);
        _handles = new EntityHandle[EntityCount];

        for (int i = 0; i < EntityCount; i++)
        {
            _handles[i] = _world.CreateEntity(
                new Position(i * 0.1f, i * 0.2f),
                new Velocity(1f + (i % 5), -0.5f));
        }
    }

    [Benchmark]
    public Position UpdateAndReadBack()
    {
        _world.Update(1f / 60f);
        return _world.GetPosition(_handles[^1]);
    }
}
