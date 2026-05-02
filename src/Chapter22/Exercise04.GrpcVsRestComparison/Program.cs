// ============================================================================
// Exercise 04 — gRPC vs REST Performance Comparison
// ============================================================================
// Hosts equivalent REST and gRPC endpoints for the same checksum workload so the
// user can compare protocol overhead, throughput, and latency with external load
// generators or dedicated benchmark clients.
// ============================================================================

using Exercise04.GrpcVsRestComparison;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5080, listen => listen.Protocols = HttpProtocols.Http1);
    options.ListenLocalhost(5081, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();

WebApplication app = builder.Build();

app.MapPost("/rest/process", (PayloadRequestDto request) =>
{
    PayloadResult result = PayloadProcessor.Process(request.Payload, request.Repeat);
    return Results.Json(result);
});

app.MapGrpcService<PayloadGrpcService>();
app.MapGet("/", () => "Use REST on port 5080 and gRPC on port 5081.");

await app.RunAsync();

public static class PayloadProcessor
{
    public static PayloadResult Process(string payload, int repeat)
    {
        int checksum = 0;
        repeat = Math.Max(repeat, 1);

        for (int r = 0; r < repeat; r++)
        {
            foreach (char c in payload)
            {
                checksum = unchecked((checksum * 31) + c);
            }
        }

        return new PayloadResult(checksum, payload.Length);
    }
}

public sealed class PayloadGrpcService : PayloadService.PayloadServiceBase
{
    public override Task<PayloadReply> Process(PayloadRequest request, ServerCallContext context)
    {
        PayloadResult result = PayloadProcessor.Process(request.Payload, request.Repeat);
        return Task.FromResult(new PayloadReply
        {
            Checksum = result.Checksum,
            Length = result.Length
        });
    }
}

public readonly record struct PayloadRequestDto(string Payload, int Repeat);
public readonly record struct PayloadResult(int Checksum, int Length);
