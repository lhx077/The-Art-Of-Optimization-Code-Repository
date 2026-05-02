// ---------------------------------------------------------------------------
// ApiEventScope.cs — Disposable Scope Structs for Automatic Event Pairing
// ---------------------------------------------------------------------------
// The IDisposable pattern provides a clean, exception-safe way to measure
// elapsed time for operations. By recording the start timestamp in the
// constructor and emitting the completion event in Dispose(), we guarantee
// that timing is always captured even when exceptions occur, and the
// calling code remains concise thanks to C#'s "using" statement. Because
// these types are declared as readonly structs, they live entirely on the
// stack and produce zero heap allocations — a critical property for
// instrumentation that runs on every single request.
// ---------------------------------------------------------------------------

using System.Diagnostics;

namespace Exercise04.CustomEventSource;

/// <summary>
/// Measures the full lifetime of an HTTP request. Create this scope at the
/// start of request processing; when it is disposed, it automatically emits
/// a <see cref="ApiEventSource.RequestCompleted"/> event carrying the
/// elapsed time and the HTTP status code. The status code is provided via
/// a <see cref="Func{Int32}"/> delegate so that it can be resolved lazily
/// at disposal time, after the handler has finished executing.
/// </summary>
public readonly struct RequestScope : IDisposable
{
    private readonly string _requestId;
    private readonly long _startTimestamp;
    private readonly Func<int> _statusCodeProvider;

    // -----------------------------------------------------------------------
    // The constructor captures a high-resolution timestamp via Stopwatch and
    // immediately fires the RequestStarted event. Using Stopwatch.GetTimestamp
    // instead of DateTime.UtcNow avoids the ~15ms resolution limitation of
    // the system clock and provides nanosecond-level precision on modern
    // hardware, which is essential for measuring sub-millisecond operations.
    // -----------------------------------------------------------------------

    public RequestScope(string method, string path, string requestId, Func<int> statusCodeProvider)
    {
        _requestId = requestId;
        _statusCodeProvider = statusCodeProvider;
        _startTimestamp = Stopwatch.GetTimestamp();

        ApiEventSource.Instance.RequestStarted(method, path, requestId);
    }

    // -----------------------------------------------------------------------
    // Dispose computes the elapsed time by diffing the current timestamp
    // against the one captured in the constructor, then emits the completion
    // event. Because this struct is readonly, the compiler enforces that no
    // field mutations occur after construction, making the timing logic
    // straightforward and free of subtle state-related bugs.
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        long elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).Milliseconds
                       + (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds * 1000;

        // Use the more precise TotalMilliseconds conversion.
        elapsedMs = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        ApiEventSource.Instance.RequestCompleted(_requestId, elapsedMs, _statusCodeProvider());
    }
}

/// <summary>
/// Measures the lifetime of a single database query. Analogous to
/// <see cref="RequestScope"/> but tailored for database operations: it
/// captures the SQL query text at start and the result row count at
/// completion. The row count is supplied via a delegate so that it can
/// be set after the query has actually executed.
/// </summary>
public readonly struct DatabaseQueryScope : IDisposable
{
    private readonly string _requestId;
    private readonly long _startTimestamp;
    private readonly Func<int> _rowCountProvider;

    // -----------------------------------------------------------------------
    // Fires DatabaseQueryStarted immediately upon construction. The query
    // parameter should contain a sanitized or parameterized version of the
    // SQL statement — never include raw user input in event payloads, as
    // ETW traces may be collected by infrastructure teams who should not
    // have access to end-user data.
    // -----------------------------------------------------------------------

    public DatabaseQueryScope(string requestId, string query, Func<int> rowCountProvider)
    {
        _requestId = requestId;
        _rowCountProvider = rowCountProvider;
        _startTimestamp = Stopwatch.GetTimestamp();

        ApiEventSource.Instance.DatabaseQueryStarted(requestId, query);
    }

    // -----------------------------------------------------------------------
    // On disposal, the elapsed time is computed and DatabaseQueryCompleted is
    // emitted along with the row count. This pattern ensures that even if the
    // query throws an exception, the completion event is still recorded (with
    // whatever row count the provider returns, typically zero in error cases),
    // giving operators visibility into failed queries and their latency.
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        long elapsedMs = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        ApiEventSource.Instance.DatabaseQueryCompleted(_requestId, elapsedMs, _rowCountProvider());
    }
}
