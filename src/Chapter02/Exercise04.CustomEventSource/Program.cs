// ---------------------------------------------------------------------------
// Program.cs — ASP.NET Core Web API with Custom EventSource Instrumentation
// ---------------------------------------------------------------------------
// This program demonstrates the integration of a custom EventSource into an
// ASP.NET Core minimal API pipeline. Every request is instrumented with
// structured, high-performance events that track request lifecycle, simulated
// database queries, and cache interactions. The key insight is that all of
// this instrumentation has near-zero cost when no listener is attached —
// the EventSource infrastructure short-circuits at the IsEnabled() check,
// avoiding string formatting, payload serialization, and any I/O entirely.
// ---------------------------------------------------------------------------

using Exercise04.CustomEventSource;

// ---------------------------------------------------------------------------
// The HighLatencyEventListener is created before the application starts so
// that it is ready to receive events from the moment the first request
// arrives. The 200ms threshold is deliberately low for demonstration
// purposes; in production you would tune this based on your SLO targets
// (e.g., p99 latency budget). The listener must be kept alive for the
// lifetime of the application — if it is garbage collected, it will
// automatically unsubscribe from all event sources.
// ---------------------------------------------------------------------------

using var latencyListener = new HighLatencyEventListener(thresholdMs: 200);

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ---------------------------------------------------------------------------
// Simple in-memory cache to demonstrate cache hit/miss event tracking.
// In a real application this would be backed by IMemoryCache or a
// distributed cache like Redis. The static dictionary is sufficient here
// because we are focused on the eventing pattern, not cache semantics.
// ---------------------------------------------------------------------------

var cache = new Dictionary<string, object>();

// ---------------------------------------------------------------------------
// GET /api/products
// Simulates fetching a product catalog. On the first call the cache is
// empty, so a "cache miss" event is emitted followed by a simulated
// database query. Subsequent calls find the data in the cache and emit
// a "cache hit" event instead, skipping the database entirely. This
// pattern clearly shows how EventSource events can reveal caching
// effectiveness in production without adding logging overhead.
// ---------------------------------------------------------------------------

app.MapGet("/api/products", async (HttpContext context) =>
{
    string requestId = Guid.NewGuid().ToString("N")[..12];
    int statusCode = 200;

    using var scope = new RequestScope(
        context.Request.Method,
        context.Request.Path,
        requestId,
        () => statusCode);

    const string cacheKey = "products:all";

    if (cache.TryGetValue(cacheKey, out object? cached))
    {
        ApiEventSource.Instance.CacheHit(requestId, cacheKey);
        return Results.Ok(cached);
    }

    ApiEventSource.Instance.CacheMiss(requestId, cacheKey);

    // Simulate a database query with moderate latency.
    int rowCount = 0;
    object[] products;

    using (var dbScope = new DatabaseQueryScope(
        requestId,
        "SELECT id, name, price FROM products ORDER BY name",
        () => rowCount))
    {
        await Task.Delay(Random.Shared.Next(50, 250));

        products = new object[]
        {
            new { Id = 1, Name = "Widget A", Price = 9.99 },
            new { Id = 2, Name = "Widget B", Price = 19.99 },
            new { Id = 3, Name = "Widget C", Price = 29.99 },
            new { Id = 4, Name = "Gadget X", Price = 49.99 },
            new { Id = 5, Name = "Gadget Y", Price = 99.99 },
        };

        rowCount = products.Length;
    }

    cache[cacheKey] = products;
    return Results.Ok(products);
});

// ---------------------------------------------------------------------------
// GET /api/products/{id}
// Simulates fetching a single product by ID. The random delay between
// 10ms and 500ms is intentional — it ensures that some requests will
// exceed the 200ms high-latency threshold, triggering the listener's
// warning output. This makes it easy to observe the listener in action
// during the exercise without needing to generate artificial load.
// ---------------------------------------------------------------------------

app.MapGet("/api/products/{id:int}", async (int id, HttpContext context) =>
{
    string requestId = Guid.NewGuid().ToString("N")[..12];
    int statusCode = 200;

    using var scope = new RequestScope(
        context.Request.Method,
        context.Request.Path,
        requestId,
        () => statusCode);

    string cacheKey = $"products:{id}";

    if (cache.TryGetValue(cacheKey, out object? cached))
    {
        ApiEventSource.Instance.CacheHit(requestId, cacheKey);
        return Results.Ok(cached);
    }

    ApiEventSource.Instance.CacheMiss(requestId, cacheKey);

    int rowCount = 0;
    object? product;

    using (var dbScope = new DatabaseQueryScope(
        requestId,
        $"SELECT id, name, price FROM products WHERE id = @id",
        () => rowCount))
    {
        // Random latency: some calls will be fast, others will breach the threshold.
        await Task.Delay(Random.Shared.Next(10, 500));

        product = id switch
        {
            1 => new { Id = 1, Name = "Widget A", Price = 9.99 },
            2 => new { Id = 2, Name = "Widget B", Price = 19.99 },
            3 => new { Id = 3, Name = "Widget C", Price = 29.99 },
            _ => null
        };

        rowCount = product is not null ? 1 : 0;
    }

    if (product is null)
    {
        statusCode = 404;
        return Results.NotFound(new { Error = "Product not found", Id = id });
    }

    cache[cacheKey] = product;
    return Results.Ok(product);
});

// ---------------------------------------------------------------------------
// GET /api/health
// A lightweight health-check endpoint that performs no database or cache
// operations. This endpoint is useful for verifying that the EventSource
// instrumentation adds negligible overhead to trivial requests — you can
// compare its latency profile against the database-backed endpoints to
// see the cost of the simulated I/O versus the cost of eventing alone.
// ---------------------------------------------------------------------------

app.MapGet("/api/health", (HttpContext context) =>
{
    string requestId = Guid.NewGuid().ToString("N")[..12];

    using var scope = new RequestScope(
        context.Request.Method,
        context.Request.Path,
        requestId,
        () => 200);

    return Results.Ok(new
    {
        Status = "healthy",
        Timestamp = DateTime.UtcNow,
        RequestId = requestId
    });
});

app.Run();

// ---------------------------------------------------------------------------
// Performance Overhead Analysis
// ---------------------------------------------------------------------------
//
// 1. Zero-cost when unobserved: Every event method in ApiEventSource is
//    guarded by an IsEnabled() check. When no EventListener or ETW session
//    is attached, this check returns false in a single branch instruction
//    and the method returns immediately — no string formatting, no payload
//    array allocation, no I/O. This is fundamentally different from
//    ILogger.LogInformation(), which always evaluates its message template
//    and arguments even when the configured log level would discard them.
//
// 2. Struct-based scopes avoid heap allocation: RequestScope and
//    DatabaseQueryScope are readonly structs, so they live on the stack.
//    Combined with Stopwatch.GetTimestamp() (which returns a raw long),
//    the entire timing mechanism is allocation-free. In contrast, a
//    class-based IDisposable would allocate on the heap for every request,
//    adding GC pressure in high-throughput scenarios.
//
// 3. Collecting events externally: These events can be captured without
//    modifying the application by using standard .NET diagnostic tools:
//
//      dotnet-trace collect --process-id <PID> \
//          --providers TAOO-Exercise04-ApiEvents:Verbose:0xFFFFFFFF
//
//    This command attaches to the running process via EventPipe and writes
//    a .nettrace file that can be analyzed in PerfView or Visual Studio's
//    diagnostic tools. The provider string format is Name:Level:Keywords.
//
// 4. PerfView integration: Open the .nettrace file in PerfView and navigate
//    to Events view. Filter by "TAOO-Exercise04-ApiEvents" to see all
//    emitted events with their timestamps and payloads. PerfView can also
//    compute duration between start/stop event pairs automatically when
//    they follow the EventSource Start/Stop naming convention.
//
// 5. Production considerations: For real-world services, consider adding
//    EventCounter or IncrementingEventCounter instances to the EventSource
//    for aggregate metrics (requests/sec, average latency, cache hit ratio).
//    These counters are polled at configurable intervals and have even lower
//    per-event overhead than individual event writes.
// ---------------------------------------------------------------------------
