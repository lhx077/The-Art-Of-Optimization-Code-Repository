// ---------------------------------------------------------------------------
// Program.cs — ServiceA: Gateway Service with OpenTelemetry Distributed Tracing
// ---------------------------------------------------------------------------
// In a microservice architecture, a single user request often traverses
// multiple services before a response is returned. Without distributed
// tracing, diagnosing latency or errors requires correlating logs across
// services manually — a tedious and error-prone process. OpenTelemetry
// solves this by assigning a unique TraceId to the entire call chain and
// breaking it into Spans, each representing a unit of work in one service.
//
// In .NET, the System.Diagnostics.Activity class is the native representation
// of a Span. The OpenTelemetry .NET SDK bridges Activity to the OTel data
// model seamlessly, so we get full distributed tracing without abandoning
// the built-in diagnostics API. This means any library that already emits
// Activity data (HttpClient, ASP.NET Core, EF Core) is automatically
// captured — no wrapper code required.
// ---------------------------------------------------------------------------

using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ---------------------------------------------------------------------------
// Define an ActivitySource for this service. An ActivitySource is the .NET
// equivalent of an OpenTelemetry Tracer — it is the factory through which
// we create custom spans. The name should be globally unique and follow a
// reverse-DNS or dot-separated convention so that it can be filtered
// unambiguously in telemetry backends like Jaeger or Zipkin.
// ---------------------------------------------------------------------------

var activitySource = new ActivitySource("TAOO.Exercise05.ServiceA");

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Register a typed HttpClient that ServiceA will use to call ServiceB.
// The HttpClient instrumentation layer (added below) automatically injects
// the W3C traceparent header into every outgoing request made through any
// HttpClient resolved from the DI container. This is the mechanism that
// propagates trace context across service boundaries without any manual
// header manipulation on our part.
// ---------------------------------------------------------------------------

builder.Services.AddHttpClient("ServiceB", client =>
{
    client.BaseAddress = new Uri("http://localhost:5002");
});

// ---------------------------------------------------------------------------
// Configure the OpenTelemetry tracing pipeline. The order of the calls
// matters conceptually but not technically:
//
//   1. SetResourceBuilder — attaches metadata (service name, version) to
//      every span emitted by this process. Telemetry backends use this to
//      group spans by service in the trace waterfall view.
//
//   2. AddSource — subscribes to our custom ActivitySource so that spans
//      we create manually (e.g., "ProcessOrder") are exported. Without
//      this call, our custom spans would be silently dropped.
//
//   3. AddAspNetCoreInstrumentation — automatically creates a span for
//      every incoming HTTP request, capturing method, route, status code,
//      and duration. This is the entry point of the trace in ServiceA.
//
//   4. AddHttpClientInstrumentation — automatically creates a child span
//      for every outgoing HTTP request, capturing the target URL, status
//      code, and duration. This is the bridge that connects ServiceA's
//      trace to ServiceB's trace.
//
//   5. AddConsoleExporter — writes completed spans to stdout in a human-
//      readable format. In production, you would replace this with an
//      OTLP exporter pointing at Jaeger, Zipkin, or a managed service.
// ---------------------------------------------------------------------------

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: "ServiceA",
            serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddSource("TAOO.Exercise05.ServiceA")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

// ---------------------------------------------------------------------------
// GET /api/orders
// Simulates processing a new order. This endpoint creates a custom span
// named "ProcessOrder" to represent the business logic, then makes an
// outgoing HTTP call to ServiceB's inventory check endpoint. The trace
// for this request will contain at least three spans:
//
//   1. The ASP.NET Core span for the incoming GET /api/orders request.
//   2. Our custom "ProcessOrder" span (child of #1).
//   3. The HttpClient span for the outgoing call to ServiceB (child of #2).
//
// ServiceB will then create its own spans under the same TraceId, forming
// a complete distributed trace across both services.
// ---------------------------------------------------------------------------

app.MapGet("/api/orders", async (IHttpClientFactory httpClientFactory) =>
{
    using var activity = activitySource.StartActivity("ProcessOrder");

    // Tags (attributes in OTel terminology) are key-value pairs attached to
    // a span. They provide searchable, structured metadata that telemetry
    // backends can index. Use semantic conventions where possible (e.g.,
    // "enduser.id") to enable cross-service correlation in the UI.
    activity?.SetTag("user.id", "user-42");
    activity?.SetTag("order.type", "standard");

    var client = httpClientFactory.CreateClient("ServiceB");

    // The HttpClient instrumentation automatically injects the W3C traceparent
    // header into this request. The header format is:
    //   traceparent: 00-<trace-id>-<parent-span-id>-<trace-flags>
    // ServiceB's ASP.NET Core instrumentation reads this header and creates
    // a child span under the same trace, preserving the causal relationship.
    var response = await client.GetAsync("/api/inventory/check");
    var content = await response.Content.ReadAsStringAsync();

    activity?.SetTag("inventory.response_status", (int)response.StatusCode);

    return Results.Ok(new
    {
        OrderId = Guid.NewGuid().ToString("N")[..8],
        Status = "created",
        InventoryCheck = content
    });
});

// ---------------------------------------------------------------------------
// GET /api/orders/{id}
// Retrieves a specific order and queries ServiceB for the corresponding
// inventory item. This endpoint demonstrates how path parameters can be
// captured as span tags, enabling you to search for traces by order ID
// in your telemetry backend. The ability to find the exact trace for a
// specific business entity (order, user, transaction) is one of the most
// valuable capabilities of distributed tracing in production debugging.
// ---------------------------------------------------------------------------

app.MapGet("/api/orders/{id}", async (string id, IHttpClientFactory httpClientFactory) =>
{
    using var activity = activitySource.StartActivity("GetOrder");

    activity?.SetTag("order.id", id);

    var client = httpClientFactory.CreateClient("ServiceB");
    var response = await client.GetAsync($"/api/inventory/{id}");
    var content = await response.Content.ReadAsStringAsync();

    activity?.SetTag("inventory.response_status", (int)response.StatusCode);

    return Results.Ok(new
    {
        OrderId = id,
        Status = "found",
        InventoryDetail = content
    });
});

// ---------------------------------------------------------------------------
// GET /api/health
// A minimal health-check endpoint with no downstream calls. Useful for
// verifying that the OpenTelemetry pipeline is active — even this trivial
// endpoint will produce a span in the console output, confirming that the
// ASP.NET Core instrumentation is wired up correctly.
// ---------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    Service = "ServiceA",
    Status = "healthy",
    Timestamp = DateTime.UtcNow
}));

// ---------------------------------------------------------------------------
// ServiceA listens on port 5001. In a real deployment, ports would be
// assigned by the orchestrator (Kubernetes, Docker Compose, etc.), but
// for this exercise we use fixed ports so that the two services can
// discover each other without a service registry.
// ---------------------------------------------------------------------------

app.Run("http://localhost:5001");

// ---------------------------------------------------------------------------
// Key Takeaways
// ---------------------------------------------------------------------------
//
// 1. Automatic context propagation: The HttpClient instrumentation injects
//    the traceparent header into every outgoing request. ServiceB receives
//    the same TraceId and creates child spans automatically. No manual
//    header passing is needed — this is the W3C Trace Context standard
//    (https://www.w3.org/TR/trace-context/) in action.
//
// 2. Custom spans for business logic: While automatic instrumentation
//    captures HTTP-level details, custom spans (via ActivitySource) let
//    you model domain-specific operations like "ProcessOrder" or
//    "ValidatePayment". This gives you a richer, more meaningful trace
//    that maps directly to your business processes.
//
// 3. Minimal code overhead: The entire tracing setup is roughly 15 lines
//    of configuration code. The per-request cost is a few microseconds
//    for span creation and tag assignment — negligible compared to any
//    real I/O operation. This is why distributed tracing should be
//    enabled by default in production, not added as an afterthought.
//
// 4. Exporter flexibility: Replacing AddConsoleExporter() with
//    AddOtlpExporter() is a one-line change that sends spans to any
//    OpenTelemetry-compatible backend (Jaeger, Zipkin, Grafana Tempo,
//    AWS X-Ray, Azure Monitor, etc.) without modifying any business code.
// ---------------------------------------------------------------------------
