// ============================================================================
// Exercise 04 — Compiled Query Optimization
// ============================================================================
// Compares a normal LINQ query with EF.CompileAsyncQuery for a hot lookup path
// where the shape is stable and only scalar parameters vary between executions.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<CompiledQueryBenchmarks>();
    return;
}

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        await using CompiledQueryDatabase database = await CompiledQueryDatabase.CreateAsync(2_000);

        ProductView? regular = await QueryRoutines.RunRegularAsync(database.Options, 5, "Hardware");
        ProductView? compiled = await QueryRoutines.RunCompiledAsync(database.Options, 5, "Hardware");

        Console.WriteLine("=== Compiled Query Demo ===");
        Console.WriteLine($"Regular:  {regular}");
        Console.WriteLine($"Compiled: {compiled}");
    }
}

public static class QueryRoutines
{
    private static readonly Func<CompiledQueryContext, int, string, IAsyncEnumerable<ProductView>> HotProductQuery =
        EF.CompileAsyncQuery((CompiledQueryContext context, int minimumStock, string category) =>
            context.Products
                .Where(product => product.Stock >= minimumStock && product.Category == category)
                .OrderBy(product => product.Id)
                // Project inside the compiled query so the reused plan also includes the final result shape.
                .Select(product => new ProductView(product.Id, product.Name, product.Category, product.Stock, product.Price))
                .Take(25));

    public static async Task<ProductView?> RunRegularAsync(DbContextOptions<CompiledQueryContext> options, int minimumStock, string category)
    {
        await using CompiledQueryContext context = new(options);

        return await context.Products
            .Where(product => product.Stock >= minimumStock && product.Category == category)
            .OrderBy(product => product.Id)
            .Select(product => new ProductView(product.Id, product.Name, product.Category, product.Stock, product.Price))
            .FirstOrDefaultAsync();
    }

    public static async Task<ProductView?> RunCompiledAsync(DbContextOptions<CompiledQueryContext> options, int minimumStock, string category)
    {
        await using CompiledQueryContext context = new(options);

        await foreach (ProductView row in HotProductQuery(context, minimumStock, category))
        {
            // The benchmark consumes only the first result because the real hot path is a quick lookup.
            return row;
        }

        return null;
    }
}

[MemoryDiagnoser]
public class CompiledQueryBenchmarks
{
    private CompiledQueryDatabase _database = null!;

    [Params(1_000, 10_000, 50_000)]
    public int RowCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _database = await CompiledQueryDatabase.CreateAsync(RowCount);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _database.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<ProductView?> RegularQuery()
    {
        return await QueryRoutines.RunRegularAsync(_database.Options, 5, "Hardware");
    }

    [Benchmark]
    public async Task<ProductView?> CompiledQuery()
    {
        return await QueryRoutines.RunCompiledAsync(_database.Options, 5, "Hardware");
    }
}

public sealed class CompiledQueryDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private CompiledQueryDatabase(SqliteConnection connection, DbContextOptions<CompiledQueryContext> options)
    {
        _connection = connection;
        Options = options;
    }

    public DbContextOptions<CompiledQueryContext> Options { get; }

    public static async Task<CompiledQueryDatabase> CreateAsync(int rowCount)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<CompiledQueryContext> options = new DbContextOptionsBuilder<CompiledQueryContext>()
            .UseSqlite(connection)
            .Options;

        await using CompiledQueryContext context = new(options);
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, rowCount);

        return new CompiledQueryDatabase(connection, options);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private static async Task SeedAsync(CompiledQueryContext context, int rowCount)
    {
        if (await context.Products.AnyAsync())
        {
            return;
        }

        const int batchSize = 1_000;
        List<CatalogProduct> batch = new(batchSize);

        for (int i = 0; i < rowCount; i++)
        {
            batch.Add(new CatalogProduct
            {
                Name = $"Product {i}",
                Category = (i % 3) switch
                {
                    0 => "Hardware",
                    1 => "Office",
                    _ => "Accessories"
                },
                Stock = i % 20,
                Price = 20m + (i % 200)
            });

            if (batch.Count == batchSize)
            {
                context.Products.AddRange(batch);
                await context.SaveChangesAsync();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            context.Products.AddRange(batch);
            await context.SaveChangesAsync();
        }
    }
}

public sealed class CompiledQueryContext : DbContext
{
    public CompiledQueryContext(DbContextOptions<CompiledQueryContext> options)
        : base(options)
    {
    }

    public DbSet<CatalogProduct> Products => Set<CatalogProduct>();
}

public sealed class CatalogProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Stock { get; set; }
    public decimal Price { get; set; }
}

public readonly record struct ProductView(int Id, string Name, string Category, int Stock, decimal Price);
