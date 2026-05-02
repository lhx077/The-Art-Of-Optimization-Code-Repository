// ============================================================================
// Exercise 02 — CAS Token Bucket Rate Limiter
// ============================================================================
// Implements a lock-free token bucket using a single packed state word updated
// through Interlocked.CompareExchange. Supports sync and async acquisition.
// ============================================================================

using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<TokenBucketBenchmarks>();
    return;
}

CasTokenBucketLimiter limiter = new(tokensPerPeriod: 10, period: TimeSpan.FromSeconds(1), burstCapacity: 10);
Console.WriteLine($"Immediate acquire: {limiter.TryAcquire(3)}");
Console.WriteLine($"Remaining tokens: {limiter.GetSnapshot().AvailableTokens}");

// Core implementation used by the demo and benchmark code below.
public sealed class CasTokenBucketLimiter
{
    private readonly long _tokensPerPeriod;
    private readonly long _burstCapacity;
    private readonly long _ticksPerPeriod;
    private long _state;

    public CasTokenBucketLimiter(long tokensPerPeriod, TimeSpan period, long burstCapacity)
    {
        if (tokensPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(tokensPerPeriod));
        if (burstCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(burstCapacity));

        _tokensPerPeriod = tokensPerPeriod;
        _burstCapacity = burstCapacity;
        _ticksPerPeriod = Math.Max(1, period.Ticks);
        _state = Pack(burstCapacity, DateTime.UtcNow.Ticks);
    }

    // Use an optimistic CAS loop so uncontended acquisitions stay lock-free.
    public bool TryAcquire(int tokenCount = 1)
    {
        if (tokenCount <= 0 || tokenCount > _burstCapacity)
        {
            return false;
        }

        while (true)
        {
            // Read the shared state with visibility guarantees before making decisions.
            long observed = Volatile.Read(ref _state);
            Unpack(observed, out int available, out int stamp);
            long nowTicks = DateTime.UtcNow.Ticks;
            int refreshed = Refill(available, stamp, nowTicks, out int newStamp);
            if (refreshed < tokenCount)
            {
                return false;
            }

            long updated = Pack(refreshed - tokenCount, newStamp);
            // Publish the updated state only if no other thread changed it first.
            if (Interlocked.CompareExchange(ref _state, updated, observed) == observed)
            {
                return true;
            }
        }
    }

    // Back off asynchronously when the bucket is empty instead of blocking a thread.
    public async ValueTask AcquireAsync(int tokenCount = 1, CancellationToken cancellationToken = default)
    {
        while (!TryAcquire(tokenCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay = EstimateDelay(tokenCount);
            await Task.Delay(delay, cancellationToken);
        }
    }

    public TokenBucketSnapshot GetSnapshot()
    {
        // Read the shared state with visibility guarantees before making decisions.
        long observed = Volatile.Read(ref _state);
        Unpack(observed, out int available, out int stamp);
        long nowTicks = DateTime.UtcNow.Ticks;
        int refreshed = Refill(available, stamp, nowTicks, out _);
        return new TokenBucketSnapshot(refreshed, _burstCapacity, _tokensPerPeriod);
    }

    private TimeSpan EstimateDelay(int requestedTokens)
    {
        // Read the shared state with visibility guarantees before making decisions.
        long observed = Volatile.Read(ref _state);
        Unpack(observed, out int available, out int stamp);
        long nowTicks = DateTime.UtcNow.Ticks;
        int refreshed = Refill(available, stamp, nowTicks, out _);
        int deficit = Math.Max(0, requestedTokens - refreshed);
        if (deficit == 0)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        long ticksPerToken = Math.Max(1, _ticksPerPeriod / _tokensPerPeriod);
        return TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, deficit * ticksPerToken));
    }

    private int Refill(int available, int stamp, long nowTicks, out int newStamp)
    {
        int nowStamp = TicksToStamp(nowTicks);
        int elapsedStamps = unchecked(nowStamp - stamp);
        long elapsedTicks = elapsedStamps < 0 ? 0 : (uint)elapsedStamps;

        long replenished = elapsedTicks * _tokensPerPeriod / _ticksPerPeriod;
        long next = Math.Min(_burstCapacity, available + replenished);

        long consumedTicks = replenished == 0 ? 0 : replenished * _ticksPerPeriod / _tokensPerPeriod;
        newStamp = unchecked(stamp + (int)consumedTicks);
        return (int)next;
    }

    private static long Pack(long tokens, long ticksOrStamp)
    {
        return (tokens << 32) | (ticksOrStamp & 0xFFFF_FFFFL);
    }

    private static void Unpack(long state, out int tokens, out int stamp)
    {
        tokens = (int)(state >> 32);
        stamp = (int)(state & 0xFFFF_FFFFL);
    }

    private static int TicksToStamp(long ticks) => unchecked((int)ticks);
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct TokenBucketSnapshot(long AvailableTokens, long Capacity, long RefillRatePerPeriod);

[MemoryDiagnoser]
// Benchmark scenarios compare the custom implementation with a useful baseline.
public class TokenBucketBenchmarks
{
    private CasTokenBucketLimiter _limiter = null!;

    [Params(4, 16)]
    public int WorkerCount;

    [Params(50_000)]
    public int AttemptsPerWorker;

    [GlobalSetup]
    public void Setup()
    {
        _limiter = new CasTokenBucketLimiter(tokensPerPeriod: 1_000_000, period: TimeSpan.FromSeconds(1), burstCapacity: 1_000_000);
    }

    [Benchmark]
    public int TryAcquireBurst()
    {
        int success = 0;
        // Use parallel workers here so the benchmark stresses coordination costs.
        Parallel.For(0, WorkerCount, _ =>
        {
            int local = 0;
            for (int i = 0; i < AttemptsPerWorker; i++)
            {
                if (_limiter.TryAcquire())
                {
                    local++;
                }
            }

            Interlocked.Add(ref success, local);
        });

        return success;
    }
}
