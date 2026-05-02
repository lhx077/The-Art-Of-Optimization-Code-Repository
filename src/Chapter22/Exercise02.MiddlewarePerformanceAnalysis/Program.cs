// ============================================================================
// Exercise 02 — Middleware Performance Analysis
// ============================================================================
// Builds an ASP.NET Core pipeline with timing probes so each middleware stage can
// measure its own cost and contribute structured timing data to the final response.
// ============================================================================

using System.Diagnostics;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddRouting();

WebApplication app = builder.Build();

app.UseMiddlewareTiming("ExceptionShield", async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync(ex.Message);
    }
});

app.UseMiddlewareTiming("Correlation", async (context, next) =>
{
    context.Items["CorrelationId"] = Guid.NewGuid().ToString("N");
    await next();
});

app.UseMiddlewareTiming("SimulatedAuth", async (context, next) =>
{
    await Task.Delay(2);
    context.Items["User"] = "demo-user";
    await next();
});

app.UseMiddlewareTiming("SimulatedBusinessLogic", async (context, next) =>
{
    if (context.Request.Path == "/slow")
    {
        await Task.Delay(15);
    }

    await next();
});

app.MapGet("/", (HttpContext context) =>
{
    return Results.Json(new
    {
        Message = "Middleware diagnostics ready.",
        CorrelationId = context.Items["CorrelationId"],
        Timings = context.GetTimingSnapshot()
    });
});

app.MapGet("/slow", (HttpContext context) =>
{
    return Results.Json(new
    {
        Message = "Slow path completed.",
        Timings = context.GetTimingSnapshot()
    });
});

await app.RunAsync();

public static class MiddlewareTimingExtensions
{
    private const string Key = "__timings";

    public static IApplicationBuilder UseMiddlewareTiming(this IApplicationBuilder app, string name, Func<HttpContext, Func<Task>, Task> body)
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Items.ContainsKey(Key))
            {
                context.Items[Key] = new List<MiddlewareTimingEntry>();
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            await body(context, async () =>
            {
                await next();
            });

            stopwatch.Stop();
            ((List<MiddlewareTimingEntry>)context.Items[Key]!).Add(
                new MiddlewareTimingEntry(name, stopwatch.ElapsedTicks, Stopwatch.GetElapsedTime(0, stopwatch.ElapsedTicks).TotalMilliseconds));
        });
    }

    public static IReadOnlyList<MiddlewareTimingEntry> GetTimingSnapshot(this HttpContext context)
    {
        return context.Items.TryGetValue(Key, out object? value) && value is List<MiddlewareTimingEntry> timings
            ? timings
            : [];
    }
}

public readonly record struct MiddlewareTimingEntry(string Name, long ElapsedTicks, double ElapsedMilliseconds);
