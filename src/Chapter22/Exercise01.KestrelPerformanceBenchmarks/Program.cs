// ============================================================================
// Exercise 01 — Kestrel Performance Benchmarks
// ============================================================================
// Starts a minimal ASP.NET Core app with configurable protocol, connection caps,
// and body size limits so external tools such as wrk or bombardier can compare
// throughput and latency under different Kestrel settings.
// ============================================================================

using Microsoft.AspNetCore.Server.Kestrel.Core;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

KestrelScenario scenario = KestrelScenario.Parse(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(scenario.Port, listenOptions =>
    {
        listenOptions.Protocols = scenario.UseHttp2 ? HttpProtocols.Http2 : HttpProtocols.Http1;
    });

    options.Limits.MaxConcurrentConnections = scenario.MaxConnections;
    options.Limits.MaxConcurrentUpgradedConnections = scenario.MaxUpgradedConnections;
    options.Limits.MaxRequestBodySize = scenario.MaxRequestBodySize;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});

WebApplication app = builder.Build();

app.MapGet("/config", () => Results.Json(scenario));
app.MapGet("/ping", () => Results.Text("pong"));
app.MapPost("/echo", async context =>
{
    using MemoryStream stream = new(capacity: scenario.InitialBufferSize);
    await context.Request.Body.CopyToAsync(stream);
    byte[] payload = stream.ToArray();
    context.Response.ContentType = "application/octet-stream";
    await context.Response.Body.WriteAsync(payload);
});

Console.WriteLine($"Listening on http://localhost:{scenario.Port}");
Console.WriteLine("Use --http2 to enable HTTP/2.");
Console.WriteLine("Example load test: bombardier -c 64 -n 200000 http://localhost:5005/ping");

await app.RunAsync();

public sealed record KestrelScenario(
    int Port,
    bool UseHttp2,
    long? MaxConnections,
    long? MaxUpgradedConnections,
    long MaxRequestBodySize,
    int InitialBufferSize)
{
    public static KestrelScenario Parse(string[] args)
    {
        int port = 5005;
        bool http2 = args.Contains("--http2", StringComparer.OrdinalIgnoreCase);
        long? maxConnections = ReadLong(args, "--max-connections");
        long? maxUpgradedConnections = ReadLong(args, "--max-upgraded");
        long maxRequestBodySize = ReadLong(args, "--max-body") ?? 1_048_576;
        int initialBufferSize = (int)(ReadLong(args, "--buffer-size") ?? 16 * 1024);
        int? requestedPort = (int?)ReadLong(args, "--port");

        if (requestedPort.HasValue)
        {
            port = requestedPort.Value;
        }

        return new KestrelScenario(port, http2, maxConnections, maxUpgradedConnections, maxRequestBodySize, initialBufferSize);
    }

    private static long? ReadLong(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(args[i + 1], out long value))
            {
                return value;
            }
        }

        return null;
    }
}
