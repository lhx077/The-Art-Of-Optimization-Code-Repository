// ============================================================================
// Exercise 02 — Change Tracking Benchmarks
// ============================================================================
// Measures tracked and no-tracking EF Core queries across different result sizes
// so the cost of materialization and change tracker population becomes visible.
// ============================================================================

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run<ChangeTrackingBenchmarks>();
    return;
}

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        await using BenchmarkDatabase database = await BenchmarkDatabase.CreateAsync(1_000);

        int trackedCount = await QuerySamples.RunTrackedAsync(database.Options);
        int noTrackingCount = await QuerySamples.RunNoTrackingAsync(database.Options);

        Console.WriteLine("=== Change Tracking Demo ===");
        Console.WriteLine($"Tracked rows:    {trackedCount}");
        Console.WriteLine($"No-tracking rows:{noTrackingCount}");
    }
}

public static class QuerySamples
{
    public static async Task<int> RunTrackedAsync(DbContextOptions<TrackingContext> options)
    {
        await using TrackingContext context = new(options);

        List<Post> posts = await context.Posts
            .Where(post => post.IsPublished)
            // Include a bounded reference so the benchmark measures realistic materialization work.
            .Include(post => post.Author)
            .OrderBy(post => post.Id)
            .ToListAsync();

        // Touch every loaded entity so the JIT cannot treat the result as unused.
        return posts.Sum(post => post.Author.Name.Length + post.Title.Length);
    }

    public static async Task<int> RunNoTrackingAsync(DbContextOptions<TrackingContext> options)
    {
        await using TrackingContext context = new(options);

        List<Post> posts = await context.Posts
            .AsNoTracking()
            .Where(post => post.IsPublished)
            // The query shape stays the same so the main difference is change tracker overhead.
            .Include(post => post.Author)
            .OrderBy(post => post.Id)
            .ToListAsync();

        return posts.Sum(post => post.Author.Name.Length + post.Title.Length);
    }
}

[MemoryDiagnoser]
public class ChangeTrackingBenchmarks
{
    private BenchmarkDatabase _database = null!;

    [Params(1_000, 10_000, 50_000)]
    public int RowCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _database = await BenchmarkDatabase.CreateAsync(RowCount);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _database.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> TrackingQuery()
    {
        return await QuerySamples.RunTrackedAsync(_database.Options);
    }

    [Benchmark]
    public async Task<int> NoTrackingQuery()
    {
        return await QuerySamples.RunNoTrackingAsync(_database.Options);
    }
}

public sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private BenchmarkDatabase(SqliteConnection connection, DbContextOptions<TrackingContext> options)
    {
        _connection = connection;
        Options = options;
    }

    public DbContextOptions<TrackingContext> Options { get; }

    public static async Task<BenchmarkDatabase> CreateAsync(int rowCount)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<TrackingContext> options = new DbContextOptionsBuilder<TrackingContext>()
            .UseSqlite(connection)
            .Options;

        await using TrackingContext context = new(options);
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, rowCount);

        return new BenchmarkDatabase(connection, options);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private static async Task SeedAsync(TrackingContext context, int rowCount)
    {
        if (await context.Posts.AnyAsync())
        {
            return;
        }

        Author[] authors =
        [
            new() { Name = "Alice" },
            new() { Name = "Bob" },
            new() { Name = "Carol" },
            new() { Name = "David" }
        ];

        context.Authors.AddRange(authors);
        await context.SaveChangesAsync();

        const int batchSize = 1_000;
        List<Post> batch = new(batchSize);

        for (int i = 0; i < rowCount; i++)
        {
            batch.Add(new Post
            {
                AuthorId = authors[i % authors.Length].Id,
                Title = $"Post {i}",
                Body = $"Payload for row {i}",
                IsPublished = (i & 1) == 0
            });

            // Flush in batches so seeding large benchmarks does not build one giant tracked graph.
            if (batch.Count == batchSize)
            {
                context.Posts.AddRange(batch);
                await context.SaveChangesAsync();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            context.Posts.AddRange(batch);
            await context.SaveChangesAsync();
        }
    }
}

public sealed class TrackingContext : DbContext
{
    public TrackingContext(DbContextOptions<TrackingContext> options)
        : base(options)
    {
    }

    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>()
            .HasOne(post => post.Author)
            .WithMany(author => author.Posts)
            .HasForeignKey(post => post.AuthorId);
    }
}

public sealed class Author
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Post> Posts { get; set; } = [];
}

public sealed class Post
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
}
