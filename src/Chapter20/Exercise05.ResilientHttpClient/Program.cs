// ============================================================================
// Exercise 05 — Resilient HTTP Client with Task.WhenAny
// ============================================================================
// Sends requests to multiple mirrors, returns the fastest successful response,
// cancels losers, and records per-server timing information.
// ============================================================================

using System.Diagnostics;

await Demo.RunAsync();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        using HttpClient httpClient = new();
        ResilientMirrorClient client = new(httpClient);
        Uri[] mirrors =
        [
            new("https://example.com"),
            new("https://www.example.com")
        ];

        try
        {
            MirrorResponse response = await client.GetFastestAsync(mirrors, TimeSpan.FromSeconds(5));
            Console.WriteLine($"Winner: {response.Server}, elapsed={response.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ResilientMirrorClient
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<Uri, List<long>> _latencyHistory = new();

    public ResilientMirrorClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MirrorResponse> GetFastestAsync(IEnumerable<Uri> mirrors, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        Dictionary<Task<MirrorResponse>, Uri> taskToServer = new();
        foreach (Uri uri in mirrors)
        {
            taskToServer[SendAsync(uri, cts.Token)] = uri;
        }

        List<Task<MirrorResponse>> pending = new(taskToServer.Keys);
        List<Exception> failures = new();
        long penaltyMs = (long)timeout.TotalMilliseconds;

        while (pending.Count > 0)
        {
            Task<MirrorResponse> completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            try
            {
                MirrorResponse winner = await completed;
                cts.Cancel();
                foreach (Task<MirrorResponse> loser in pending)
                {
                    RecordLatency(taskToServer[loser], penaltyMs);
                }
                await DrainLosersAsync(pending);
                RecordLatency(winner.Server, winner.ElapsedMilliseconds);
                return winner;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        throw new AggregateException("All mirror requests failed.", failures);
    }

    private async Task<MirrorResponse> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        return new MirrorResponse(uri, body, stopwatch.ElapsedMilliseconds);
    }

    private async Task DrainLosersAsync(IEnumerable<Task<MirrorResponse>> losers)
    {
        foreach (Task<MirrorResponse> loser in losers)
        {
            try
            {
                await loser;
            }
            catch
            {
            }
        }
    }

    private void RecordLatency(Uri server, long elapsedMilliseconds)
    {
        if (!_latencyHistory.TryGetValue(server, out List<long>? values))
        {
            values = new List<long>();
            _latencyHistory[server] = values;
        }

        values.Add(elapsedMilliseconds);
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct MirrorResponse(Uri Server, string Content, long ElapsedMilliseconds);
