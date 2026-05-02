// ============================================================================
// Exercise 06 — AsyncLocal Correlation ID Demo
// ============================================================================
// Demonstrates request-scoped correlation IDs that flow through async calls,
// nested logical scopes, Task.Run, and async streams.
// ============================================================================

await Demo.RunAsync();

// Core implementation used by the demo and benchmark code below.
public static class RequestContext
{
    private static readonly AsyncLocal<ScopeState?> s_current = new();

    public static string? CorrelationId => s_current.Value?.CorrelationId;
    public static string? ScopeName => s_current.Value?.ScopeName;

    public static IDisposable BeginRequest(string correlationId) => Push(new ScopeState(correlationId, "request", s_current.Value));
    public static IDisposable BeginScope(string scopeName) => Push(new ScopeState(CorrelationId ?? "unknown", scopeName, s_current.Value));

    // Link the new node to the observed head before attempting to publish it.
    private static IDisposable Push(ScopeState state)
    {
        s_current.Value = state;
        return new PopWhenDisposed(state.Parent);
    }

    private sealed class PopWhenDisposed : IDisposable
    {
        private readonly ScopeState? _parent;
        public PopWhenDisposed(ScopeState? parent) => _parent = parent;
        public void Dispose() => s_current.Value = _parent;
    }

    private sealed record ScopeState(string CorrelationId, string ScopeName, ScopeState? Parent);
}

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        using (RequestContext.BeginRequest(Guid.NewGuid().ToString("N")))
        {
            Log("request-start");

            using (RequestContext.BeginScope("transaction"))
            {
                await Task.Delay(10);
                Log("after-delay");

                await Task.Run(() => Log("inside-task-run"));

                await foreach (int item in StreamAsync())
                {
                    Log($"stream-item={item}");
                }
            }

            Log("request-end");
        }
    }

    private static async IAsyncEnumerable<int> StreamAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(5);
            yield return i;
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{RequestContext.CorrelationId}] [{RequestContext.ScopeName}] {message}");
    }
}
