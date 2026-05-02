// ============================================================================
// Exercise 01 — Query Loading Strategies
// ============================================================================
// Builds a small EF Core model and compares projection, eager loading, split
// queries, and explicit loading so the generated SQL shape can be reasoned about.
// ============================================================================

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<OrdersContext> options = new DbContextOptionsBuilder<OrdersContext>()
            .UseSqlite(connection)
            .LogTo(Console.WriteLine, LogLevel.Information)
            .EnableSensitiveDataLogging()
            .Options;

        await using OrdersContext setupContext = new(options);
        await setupContext.Database.EnsureCreatedAsync();
        await SeedData.CreateAsync(setupContext);

        Console.WriteLine("=== Projection for summary screen ===");
        await using OrdersContext projectionContext = new(options);
        List<OrderSummaryRow> summaries = await projectionContext.Orders
            // Select only the fields needed by the screen instead of materializing the full graph.
            .Where(order => order.Status == "Ready")
            .OrderBy(order => order.Id)
            .Select(order => new OrderSummaryRow(
                order.Id,
                order.Customer.Name,
                order.Customer.Address.City,
                order.OrderDetails.Count,
                order.OrderDetails.Sum(detail => detail.Quantity * detail.UnitPrice)))
            .ToListAsync();

        foreach (OrderSummaryRow summary in summaries)
        {
            Console.WriteLine($"Order {summary.OrderId}: customer={summary.CustomerName}, city={summary.City}, items={summary.LineCount}, total={summary.Total:C}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Eager loading with split query for a detail view ===");
        await using OrdersContext eagerContext = new(options);
        List<Order> orders = await eagerContext.Orders
            .Where(order => order.Status == "Ready")
            // Customer and Address are single-reference navigations, so including them is bounded.
            .Include(order => order.Customer)
                .ThenInclude(customer => customer.Address)
            // The collection branch is included because the detail view really needs it.
            .Include(order => order.OrderDetails)
                .ThenInclude(detail => detail.Product)
                    .ThenInclude(product => product.Category)
            // Split queries avoid row multiplication when multiple collections join into one result set.
            .AsSplitQuery()
            .ToListAsync();

        foreach (Order order in orders)
        {
            Console.WriteLine($"Order {order.Id}: {order.Customer.Name} from {order.Customer.Address.City}, details={order.OrderDetails.Count}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Explicit loading for an optional branch ===");
        await using OrdersContext explicitContext = new(options);
        Order selected = await explicitContext.Orders
            .Include(order => order.Customer)
                .ThenInclude(customer => customer.Address)
            .SingleAsync(order => order.Id == 1);

        Console.WriteLine($"Loaded order {selected.Id} without details first.");

        // The first query stays smaller because the optional collection is not always required.
        await explicitContext.Entry(selected)
            .Collection(order => order.OrderDetails)
            .Query()
            .Include(detail => detail.Product)
                .ThenInclude(product => product.Category)
            .LoadAsync();

        Console.WriteLine($"Expanded order {selected.Id} details on demand: {selected.OrderDetails.Count} rows.");
    }
}

public sealed class OrdersContext : DbContext
{
    public OrdersContext(DbContextOptions<OrdersContext> options)
        : base(options)
    {
    }

    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderDetail> OrderDetails => Set<OrderDetail>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>()
            .HasOne(customer => customer.Address)
            .WithMany()
            .HasForeignKey(customer => customer.AddressId);

        modelBuilder.Entity<Order>()
            .HasOne(order => order.Customer)
            .WithMany(customer => customer.Orders)
            .HasForeignKey(order => order.CustomerId);

        modelBuilder.Entity<OrderDetail>()
            .HasOne(detail => detail.Order)
            .WithMany(order => order.OrderDetails)
            .HasForeignKey(detail => detail.OrderId);

        modelBuilder.Entity<OrderDetail>()
            .HasOne(detail => detail.Product)
            .WithMany(product => product.OrderDetails)
            .HasForeignKey(detail => detail.ProductId);

        modelBuilder.Entity<Product>()
            .HasOne(product => product.Category)
            .WithMany(category => category.Products)
            .HasForeignKey(product => product.CategoryId);
    }
}

public static class SeedData
{
    public static async Task CreateAsync(OrdersContext context)
    {
        if (await context.Orders.AnyAsync())
        {
            return;
        }

        Address shanghai = new() { Street = "Nanjing Road", City = "Shanghai" };
        Address beijing = new() { Street = "Chaoyang Road", City = "Beijing" };

        Category hardware = new() { Name = "Hardware" };
        Category office = new() { Name = "Office" };

        Product keyboard = new() { Name = "Mechanical Keyboard", Category = hardware, Price = 399m };
        Product mouse = new() { Name = "Wireless Mouse", Category = hardware, Price = 199m };
        Product notebook = new() { Name = "Notebook", Category = office, Price = 25m };

        Customer alice = new() { Name = "Alice", Address = shanghai };
        Customer bob = new() { Name = "Bob", Address = beijing };

        Order first = new()
        {
            Customer = alice,
            Status = "Ready",
            CreatedAtUtc = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            OrderDetails =
            [
                new OrderDetail { Product = keyboard, Quantity = 1, UnitPrice = keyboard.Price },
                new OrderDetail { Product = mouse, Quantity = 2, UnitPrice = mouse.Price }
            ]
        };

        Order second = new()
        {
            Customer = bob,
            Status = "Ready",
            CreatedAtUtc = new DateTime(2026, 4, 2, 9, 30, 0, DateTimeKind.Utc),
            OrderDetails =
            [
                new OrderDetail { Product = notebook, Quantity = 10, UnitPrice = notebook.Price }
            ]
        };

        Order draft = new()
        {
            Customer = alice,
            Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc),
            OrderDetails =
            [
                new OrderDetail { Product = notebook, Quantity = 1, UnitPrice = notebook.Price }
            ]
        };

        context.AddRange(first, second, draft);
        await context.SaveChangesAsync();
    }
}

public sealed class Address
{
    public int Id { get; set; }
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Product> Products { get; set; } = [];
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AddressId { get; set; }
    public Address Address { get; set; } = null!;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public List<OrderDetail> OrderDetails { get; set; } = [];
}

public sealed class OrderDetail
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public decimal Price { get; set; }
    public List<OrderDetail> OrderDetails { get; set; } = [];
}

public readonly record struct OrderSummaryRow(int OrderId, string CustomerName, string City, int LineCount, decimal Total);
