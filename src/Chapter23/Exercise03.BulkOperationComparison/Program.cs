// ============================================================================
// Exercise 03 — Bulk Operation Comparison
// ============================================================================
// Compares row-by-row tracked updates with ExecuteUpdate and a raw SQL batch so
// the overhead of change tracking and per-entity mutation is easy to observe.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<BulkOperationBenchmarks>();
    return;
}

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        await using BulkBenchmarkDatabase database = await BulkBenchmarkDatabase.CreateAsync(1_000);

        int tracked = await BulkSamples.RunTrackedRaiseAsync(database.Options);
        await database.ResetPricesAsync();

        int executeUpdate = await BulkSamples.RunExecuteUpdateRaiseAsync(database.Options);
        await database.ResetPricesAsync();

        int rawSql = await BulkSamples.RunRawSqlRaiseAsync(database.Options);

        Console.WriteLine("=== Bulk Operation Demo ===");
        Console.WriteLine($"Tracked updated rows:      {tracked}");
        Console.WriteLine($"ExecuteUpdate rows:        {executeUpdate}");
        Console.WriteLine($"Raw SQL batch updated rows:{rawSql}");
    }
}

public static class BulkSamples
{
    public static async Task<int> RunTrackedRaiseAsync(DbContextOptions<BulkContext> options)
    {
        await using BulkContext context = new(options);

        List<InventoryItem> items = await context.InventoryItems
            .Where(item => item.Category == "Hardware")
            .ToListAsync();

        foreach (InventoryItem item in items)
        {
            // Each entity is materialized, tracked, modified, and later translated into updates.
            item.Price *= 1.05m;
            item.LastUpdatedUtc = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
        return items.Count;
    }

    public static async Task<int> RunExecuteUpdateRaiseAsync(DbContextOptions<BulkContext> options)
    {
        await using BulkContext context = new(options);

        return await context.InventoryItems
            .Where(item => item.Category == "Hardware")
            // The database computes the new values directly, so no entity graph is materialized.
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Price, item => item.Price * 1.05m)
                .SetProperty(item => item.LastUpdatedUtc, _ => DateTime.UtcNow));
    }

    public static async Task<int> RunRawSqlRaiseAsync(DbContextOptions<BulkContext> options)
    {
        await using BulkContext context = new(options);

        // Raw SQL is useful for hot paths, but it also pushes more correctness responsibility onto the caller.
        return await context.Database.ExecuteSqlRawAsync(
            "UPDATE InventoryItems SET Price = Price * 1.05, LastUpdatedUtc = CURRENT_TIMESTAMP WHERE Category = {0}",
            "Hardware");
    }
}

[MemoryDiagnoser]
public class BulkOperationBenchmarks
{
    private BulkBenchmarkDatabase _database = null!;

    [Params(1_000, 10_000, 50_000)]
    public int RowCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _database = await BulkBenchmarkDatabase.CreateAsync(RowCount);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _database.DisposeAsync();
    }

    [IterationSetup]
    public async Task ResetData()
    {
        await _database.ResetPricesAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> TrackedEntities()
    {
        return await BulkSamples.RunTrackedRaiseAsync(_database.Options);
    }

    [Benchmark]
    public async Task<int> ExecuteUpdate()
    {
        return await BulkSamples.RunExecuteUpdateRaiseAsync(_database.Options);
    }

    [Benchmark]
    public async Task<int> RawSqlBatch()
    {
        return await BulkSamples.RunRawSqlRaiseAsync(_database.Options);
    }
}

public sealed class BulkBenchmarkDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private BulkBenchmarkDatabase(SqliteConnection connection, DbContextOptions<BulkContext> options)
    {
        _connection = connection;
        Options = options;
    }

    public DbContextOptions<BulkContext> Options { get; }

    public static async Task<BulkBenchmarkDatabase> CreateAsync(int rowCount)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<BulkContext> options = new DbContextOptionsBuilder<BulkContext>()
            .UseSqlite(connection)
            .Options;

        await using BulkContext context = new(options);
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, rowCount);

        return new BulkBenchmarkDatabase(connection, options);
    }

    public async Task ResetPricesAsync()
    {
        await using BulkContext context = new(Options);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE InventoryItems SET Price = BasePrice, LastUpdatedUtc = '2026-01-01 00:00:00'");
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private static async Task SeedAsync(BulkContext context, int rowCount)
    {
        if (await context.InventoryItems.AnyAsync())
        {
            return;
        }

        const int batchSize = 1_000;
        List<InventoryItem> batch = new(batchSize);

        for (int i = 0; i < rowCount; i++)
        {
            decimal price = 100m + (i % 50);

            batch.Add(new InventoryItem
            {
                Sku = $"SKU-{i:D6}",
                Category = (i & 1) == 0 ? "Hardware" : "Office",
                BasePrice = price,
                Price = price,
                LastUpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            if (batch.Count == batchSize)
            {
                context.InventoryItems.AddRange(batch);
                await context.SaveChangesAsync();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            context.InventoryItems.AddRange(batch);
            await context.SaveChangesAsync();
        }
    }
}

public sealed class BulkContext : DbContext
{
    public BulkContext(DbContextOptions<BulkContext> options)
        : base(options)
    {
    }

    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
}

public sealed class InventoryItem
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public decimal Price { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}
