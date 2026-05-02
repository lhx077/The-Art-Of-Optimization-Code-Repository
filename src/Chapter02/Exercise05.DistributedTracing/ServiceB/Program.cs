// ---------------------------------------------------------------------------
// Program.cs — ServiceB: Inventory Service with OpenTelemetry Distributed Tracing
// ---------------------------------------------------------------------------
// ServiceB demonstrates the downstream half of a distributed trace. When an
// incoming request carries a W3C traceparent header (injected by ServiceA's
// HttpClient instrumentation), the ASP.NET Core instrumentation automatically
// extracts the TraceId and parent SpanId from the header and creates a new
// child span. This preserves the causal chain: ServiceA's outgoing HTTP span
// becomes the parent of ServiceB's incoming HTTP span, and any custom spans
// we create inside ServiceB become grandchildren of ServiceA's root span.
//
// The result is a single, unified trace that shows the complete journey of a
// user request across both services, with accurate timing for each operation.
// This is invaluable for identifying which service contributes the most
// latency and where errors originate in a multi-service architecture.
// ---------------------------------------------------------------------------

using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ---------------------------------------------------------------------------
// ActivitySource for ServiceB. Each service should have its own ActivitySource
// with a distinct name. This allows telemetry backends to attribute spans to
// the correct service and enables filtering by source name when analyzing
// traces. The naming convention "TAOO.Exercise05.ServiceB" mirrors the
// namespace structure, making it easy to locate the corresponding code.
// ---------------------------------------------------------------------------

var activitySource = new ActivitySource("TAOO.Exercise05.ServiceB");

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// OpenTelemetry configuration for ServiceB. The setup is intentionally
// identical to ServiceA's, differing only in the service name and the
// ActivitySource name. This symmetry is a design feature of OpenTelemetry:
// every service in the mesh uses the same SDK, the same configuration
// pattern, and the same exporters. The only thing that changes is the
// service identity metadata. This uniformity dramatically reduces the
// cognitive overhead of adding tracing to new services.
// ---------------------------------------------------------------------------

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: "ServiceB",
            serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddSource("TAOO.Exercise05.ServiceB")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

// ---------------------------------------------------------------------------
// GET /api/inventory/check
// Simulates a bulk inventory availability check. The random delay between
// 50ms and 300ms models the variability of a real database or external API
// call. A 20% error rate is injected to demonstrate how span error status
// propagates through the trace — when this span is marked as Error, the
// parent spans in ServiceA remain unaffected (they record the HTTP 500
// status code instead), allowing operators to pinpoint the exact service
// and operation that failed.
// ---------------------------------------------------------------------------

app.MapGet("/api/inventory/check", async () =>
{
    using var activity = activitySource.StartActivity("CheckInventory");

    int itemsChecked = Random.Shared.Next(10, 100);
    int available = Random.Shared.Next(1, itemsChecked);

    // Span tags provide the structured data that makes traces searchable.
    // In Jaeger, you can query for all traces where "inventory.available"
    // is below a threshold, instantly finding requests that encountered
    // low stock conditions without grepping through log files.
    activity?.SetTag("inventory.items_checked", itemsChecked);
    activity?.SetTag("inventory.available", available);

    // Simulate variable processing time representing database queries,
    // cache lookups, and business rule evaluation.
    int delayMs = Random.Shared.Next(50, 300);
    await Task.Delay(delayMs);

    activity?.SetTag("inventory.processing_time_ms", delayMs);

    // ---------------------------------------------------------------------------
    // Simulate a 20% error rate. In a real system, errors might come from
    // database timeouts, connection pool exhaustion, or upstream service
    // failures. When we set the span status to Error, the OpenTelemetry
    // exporter marks this span accordingly. In Jaeger or Zipkin's UI, error
    // spans are highlighted in red within the trace waterfall, making it
    // immediately obvious which service and which operation caused the failure.
    // The error event (AddEvent) provides additional diagnostic context that
    // appears as a log entry attached to the span.
    // ---------------------------------------------------------------------------

    if (Random.Shared.Next(100) < 20)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Inventory check failed");
        activity?.AddEvent(new ActivityEvent("InventoryCheckError", tags:
            new ActivityTagsCollection
            {
                { "error.type", "SimulatedFailure" },
                { "error.message", "Random failure injected for demonstration" }
            }));

        return Results.Json(
            new { Error = "Inventory check failed", ItemsChecked = itemsChecked },
            statusCode: 500);
    }

    return Results.Ok(new
    {
        Status = "available",
        ItemsChecked = itemsChecked,
        Available = available,
        ProcessingTimeMs = delayMs
    });
});

// ---------------------------------------------------------------------------
// GET /api/inventory/{id}
// Looks up a specific inventory item by ID. The delay range (10-500ms) is
// deliberately wide to demonstrate timeout detection via span status. When
// the simulated processing time exceeds 400ms, we treat it as a timeout
// condition and mark the span with an Error status. This pattern is common
// in production systems where you want to distinguish between hard errors
// (exceptions, 5xx responses) and soft errors (timeouts, degraded
// performance) at the tracing level.
// ---------------------------------------------------------------------------

app.MapGet("/api/inventory/{id}", async (string id) =>
{
    using var activity = activitySource.StartActivity("GetInventoryItem");

    activity?.SetTag("inventory.item_id", id);

    int delayMs = Random.Shared.Next(10, 500);
    await Task.Delay(delayMs);

    activity?.SetTag("inventory.lookup_time_ms", delayMs);

    // ---------------------------------------------------------------------------
    // Timeout detection: if the simulated lookup takes longer than 400ms, we
    // flag the span as an error with a "Timeout" description. In a real system,
    // this threshold would come from configuration (e.g., an SLO target) rather
    // than a hardcoded constant. The key insight is that the span status is
    // independent of the HTTP status code — we can return a 200 response while
    // still flagging the span as problematic, alerting operators to degraded
    // performance before it escalates into user-visible failures.
    // ---------------------------------------------------------------------------

    if (delayMs > 400)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Timeout: lookup exceeded 400ms threshold");
        activity?.AddEvent(new ActivityEvent("SlowLookup", tags:
            new ActivityTagsCollection
            {
                { "threshold_ms", 400 },
                { "actual_ms", delayMs }
            }));
    }

    return Results.Ok(new
    {
        ItemId = id,
        Name = $"Item-{id}",
        Quantity = Random.Shared.Next(0, 500),
        Warehouse = "WH-" + Random.Shared.Next(1, 5),
        LookupTimeMs = delayMs
    });
});

// ---------------------------------------------------------------------------
// GET /api/health
// Health-check endpoint for ServiceB. In a containerized deployment, the
// orchestrator would poll this endpoint to determine service readiness.
// The span generated by this endpoint serves as a baseline for the minimum
// overhead of the tracing pipeline itself.
// ---------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    Service = "ServiceB",
    Status = "healthy",
    Timestamp = DateTime.UtcNow
}));

// ---------------------------------------------------------------------------
// ServiceB listens on port 5002. The port separation from ServiceA (5001)
// simulates the network boundary that exists between services in production.
// Even though both services run on localhost in this exercise, the trace
// context propagation works identically to a cross-network deployment
// because it relies solely on HTTP headers, not shared memory or IPC.
// ---------------------------------------------------------------------------

app.Run("http://localhost:5002");

// ---------------------------------------------------------------------------
// Key Takeaways
// ---------------------------------------------------------------------------
//
// 1. Automatic parent-child linking: ServiceB never explicitly reads the
//    traceparent header or sets a parent span ID. The ASP.NET Core
//    instrumentation handles this transparently. When you examine the
//    console output, you will see that ServiceB's spans share the same
//    TraceId as ServiceA's spans, and their ParentSpanId points back to
//    ServiceA's HttpClient span.
//
// 2. Error propagation semantics: Setting a span's status to Error does
//    not automatically propagate the error to parent spans. Each span
//    records its own status independently. This is by design — it allows
//    the trace to show exactly where the error occurred without polluting
//    upstream spans with misleading error markers.
//
// 3. Events vs. tags: Tags (SetTag) are indexed key-value pairs meant for
//    searching and filtering. Events (AddEvent) are timestamped log entries
//    attached to a span, meant for recording discrete occurrences within
//    the span's lifetime. Use tags for attributes you want to query by;
//    use events for diagnostic details you want to inspect after finding
//    the relevant trace.
//
// 4. Performance characteristics: The OpenTelemetry SDK uses a sampling
//    strategy to control overhead. The default AlwaysOnSampler captures
//    every trace, which is appropriate for development and low-traffic
//    services. For high-throughput production services, switch to a
//    TraceIdRatioBasedSampler (e.g., 10% sampling) or a parent-based
//    sampler that respects the upstream service's sampling decision.
// ---------------------------------------------------------------------------
