// ============================================================================
// Exercise 05 — Hybrid Data Access Layer
// ============================================================================
// Uses EF Core for transactional CRUD and Dapper for a reporting query so the
// tradeoff between maintainability and hot-path SQL control is easy to study.
// ============================================================================

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<HybridContext> options = new DbContextOptionsBuilder<HybridContext>()
            .UseSqlite(connection)
            .Options;

        await using HybridContext setupContext = new(options);
        await setupContext.Database.EnsureCreatedAsync();
        await SeedData.CreateAsync(setupContext);

        OrderCommandRepository commands = new(options);
        SalesReportRepository reports = new(connection);
        OrderService service = new(commands, reports);

        await service.MarkOrderAsShippedAsync(1);

        Console.WriteLine("=== Hybrid Data Access Demo ===");
        IReadOnlyList<SalesSummaryRow> rows = await service.GetTopCustomersAsync(5);
        foreach (SalesSummaryRow row in rows)
        {
            Console.WriteLine($"Customer={row.CustomerName}, orders={row.OrderCount}, amount={row.TotalAmount:C}");
        }
    }
}

public sealed class OrderService
{
    private readonly OrderCommandRepository _commands;
    private readonly SalesReportRepository _reports;

    public OrderService(OrderCommandRepository commands, SalesReportRepository reports)
    {
        _commands = commands;
        _reports = reports;
    }

    public async Task MarkOrderAsShippedAsync(int orderId)
    {
        HybridOrder order = await _commands.GetRequiredAsync(orderId);

        // EF Core is a good fit here because this is aggregate-style business logic, not a raw report.
        order.Status = "Shipped";
        order.ShippedAtUtc = DateTime.UtcNow;

        await _commands.SaveChangesAsync();
    }

    public Task<IReadOnlyList<SalesSummaryRow>> GetTopCustomersAsync(int take)
    {
        // Dapper is used for the read-heavy report because the result is already a flat projection.
        return _reports.GetTopCustomersAsync(take);
    }
}

public sealed class OrderCommandRepository
{
    private readonly HybridContext _context;

    public OrderCommandRepository(DbContextOptions<HybridContext> options)
    {
        _context = new HybridContext(options);
    }

    public async Task<HybridOrder> GetRequiredAsync(int orderId)
    {
        HybridOrder? order = await _context.Orders.SingleOrDefaultAsync(order => order.Id == orderId);
        return order ?? throw new InvalidOperationException($"Order {orderId} was not found.");
    }

    public Task<int> SaveChangesAsync() => _context.SaveChangesAsync();
}

public sealed class SalesReportRepository
{
    private readonly SqliteConnection _connection;

    public SalesReportRepository(SqliteConnection connection)
    {
        _connection = connection;
    }

    public async Task<IReadOnlyList<SalesSummaryRow>> GetTopCustomersAsync(int take)
    {
        const string sql = """
            SELECT
                c.Name AS CustomerName,
                COUNT(o.Id) AS OrderCount,
                COALESCE(SUM(oi.Quantity * oi.UnitPrice), 0) AS TotalAmount
            FROM Orders o
            INNER JOIN Customers c ON c.Id = o.CustomerId
            LEFT JOIN OrderItems oi ON oi.OrderId = o.Id
            WHERE o.Status <> @DraftStatus
            GROUP BY c.Id, c.Name
            ORDER BY TotalAmount DESC, CustomerName ASC
            LIMIT @Take;
            """;

        IEnumerable<SalesSummaryRow> rows = await _connection.QueryAsync<SalesSummaryRow>(sql, new
        {
            DraftStatus = "Draft",
            Take = take
        });

        return rows.ToList();
    }
}

public sealed class HybridContext : DbContext
{
    public HybridContext(DbContextOptions<HybridContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<HybridOrder> Orders => Set<HybridOrder>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>()
            .ToTable("Customers");

        modelBuilder.Entity<HybridOrder>()
            .ToTable("Orders")
            .HasOne(order => order.Customer)
            .WithMany(customer => customer.Orders)
            .HasForeignKey(order => order.CustomerId);

        modelBuilder.Entity<OrderItem>()
            .ToTable("OrderItems")
            .HasOne(item => item.Order)
            .WithMany(order => order.Items)
            .HasForeignKey(item => item.OrderId);
    }
}

public static class SeedData
{
    public static async Task CreateAsync(HybridContext context)
    {
        if (await context.Orders.AnyAsync())
        {
            return;
        }

        Customer alice = new() { Name = "Alice" };
        Customer bob = new() { Name = "Bob" };

        HybridOrder first = new()
        {
            Customer = alice,
            Status = "Paid",
            CreatedAtUtc = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new OrderItem { Sku = "KB-001", Quantity = 1, UnitPrice = 399m },
                new OrderItem { Sku = "MS-002", Quantity = 2, UnitPrice = 199m }
            ]
        };

        HybridOrder second = new()
        {
            Customer = bob,
            Status = "Paid",
            CreatedAtUtc = new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new OrderItem { Sku = "MN-003", Quantity = 1, UnitPrice = 1599m }
            ]
        };

        HybridOrder draft = new()
        {
            Customer = alice,
            Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new OrderItem { Sku = "NB-004", Quantity = 3, UnitPrice = 25m }
            ]
        };

        context.AddRange(first, second, draft);
        await context.SaveChangesAsync();
    }
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<HybridOrder> Orders { get; set; } = [];
}

public sealed class HybridOrder
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ShippedAtUtc { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public HybridOrder Order { get; set; } = null!;
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class SalesSummaryRow
{
    public string CustomerName { get; init; } = string.Empty;
    public long OrderCount { get; init; }
    public decimal TotalAmount { get; init; }
}
