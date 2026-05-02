// ---------------------------------------------------------------------------
// ApiEventSource.cs — Custom EventSource for Web API Performance Tracking
// ---------------------------------------------------------------------------
// EventSource is a high-performance structured logging mechanism built into
// the .NET runtime, designed specifically for production telemetry with
// minimal overhead. Unlike traditional logging frameworks such as Serilog
// or NLog, EventSource leverages ETW (Event Tracing for Windows) and
// EventPipe on cross-platform targets, enabling kernel-level event
// collection without incurring the cost of string formatting until a
// listener is actually attached. This makes it ideal for always-on
// instrumentation in latency-sensitive services.
// ---------------------------------------------------------------------------

using System.Diagnostics.Tracing;

namespace Exercise04.CustomEventSource;

/// <summary>
/// Provides structured, high-performance events for tracking the lifecycle
/// of HTTP requests, database queries, and cache interactions within a
/// Web API. The singleton pattern ensures a single event stream that all
/// components write to, which simplifies listener configuration and
/// reduces the chance of duplicate event source names at runtime.
/// </summary>
[EventSource(Name = "TAOO-Exercise04-ApiEvents")]
public sealed class ApiEventSource : EventSource
{
    /// <summary>
    /// Singleton instance shared across the entire application. EventSource
    /// instances are expensive to construct because they register with the
    /// runtime's tracing infrastructure, so reusing a single instance is
    /// both a performance best practice and a correctness requirement —
    /// creating two sources with the same name leads to undefined behavior.
    /// </summary>
    public static readonly ApiEventSource Instance = new();

    // Prevent external instantiation; the singleton above is the only entry point.
    private ApiEventSource() { }

    // -------------------------------------------------------------------
    // Keywords — bit flags that allow listeners to subscribe to specific
    // categories of events. A listener can combine flags (e.g.,
    // Keywords.Request | Keywords.Database) to receive only the events
    // it cares about, which further reduces overhead by letting the
    // runtime skip serialization for unsubscribed categories.
    // -------------------------------------------------------------------

    public static class Keywords
    {
        public const EventKeywords Request  = (EventKeywords)1;
        public const EventKeywords Database = (EventKeywords)2;
        public const EventKeywords Cache    = (EventKeywords)4;
    }

    // -------------------------------------------------------------------
    // Event 1 — RequestStarted
    // Fired at the very beginning of request processing, before any
    // middleware or handler logic executes. Capturing the HTTP method,
    // path, and a correlation request ID enables downstream listeners
    // to group all events belonging to a single request and to compute
    // end-to-end latency when paired with RequestCompleted.
    // -------------------------------------------------------------------

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Request,
           Message = "Request started: {0} {1} [{2}]")]
    public void RequestStarted(string method, string path, string requestId)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Request))
        {
            WriteEvent(1, method, path, requestId);
        }
    }

    // -------------------------------------------------------------------
    // Event 2 — RequestCompleted
    // Emitted after the response has been fully written. The elapsed
    // time in milliseconds and the HTTP status code provide the two
    // most critical signals for SLO monitoring. Listeners can use the
    // requestId to correlate this event with the matching RequestStarted
    // and any intermediate database or cache events.
    // -------------------------------------------------------------------

    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Request,
           Message = "Request completed: [{0}] {1}ms status={2}")]
    public void RequestCompleted(string requestId, long elapsedMs, int statusCode)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Request))
        {
            WriteEvent(2, requestId, elapsedMs, statusCode);
        }
    }

    // -------------------------------------------------------------------
    // Event 3 — DatabaseQueryStarted
    // Marks the beginning of a database operation. The query string is
    // included for diagnostic purposes; in production you may want to
    // truncate or parameterize it to avoid leaking sensitive data. This
    // event is emitted at Verbose level because high-throughput services
    // may generate thousands of queries per second and collecting all of
    // them at Informational level could overwhelm storage.
    // -------------------------------------------------------------------

    [Event(3, Level = EventLevel.Verbose, Keywords = Keywords.Database,
           Message = "DB query started: [{0}] {1}")]
    public void DatabaseQueryStarted(string requestId, string query)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Database))
        {
            WriteEvent(3, requestId, query);
        }
    }

    // -------------------------------------------------------------------
    // Event 4 — DatabaseQueryCompleted
    // Pairs with DatabaseQueryStarted to provide query-level latency and
    // result-set size. The row count is useful for detecting N+1 query
    // patterns or unexpectedly large result sets that may indicate a
    // missing WHERE clause. Together with elapsedMs, it gives a complete
    // picture of database interaction cost.
    // -------------------------------------------------------------------

    [Event(4, Level = EventLevel.Verbose, Keywords = Keywords.Database,
           Message = "DB query completed: [{0}] {1}ms rows={2}")]
    public void DatabaseQueryCompleted(string requestId, long elapsedMs, int rowCount)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Database))
        {
            WriteEvent(4, requestId, elapsedMs, rowCount);
        }
    }

    // -------------------------------------------------------------------
    // Event 5 — CacheHit
    // Indicates that a requested value was found in the cache, avoiding
    // a more expensive downstream call. Tracking cache hits alongside
    // cache misses allows you to compute hit ratios over time, which is
    // one of the most important metrics for evaluating caching strategy
    // effectiveness.
    // -------------------------------------------------------------------

    [Event(5, Level = EventLevel.Verbose, Keywords = Keywords.Cache,
           Message = "Cache hit: [{0}] key={1}")]
    public void CacheHit(string requestId, string cacheKey)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Cache))
        {
            WriteEvent(5, requestId, cacheKey);
        }
    }

    // -------------------------------------------------------------------
    // Event 6 — CacheMiss
    // Indicates that the requested key was not present in the cache,
    // meaning the caller must fall through to the underlying data source.
    // A sudden spike in cache misses can signal cache eviction pressure,
    // a deployment that invalidated keys, or a shift in traffic patterns
    // that the current caching strategy does not cover.
    // -------------------------------------------------------------------

    [Event(6, Level = EventLevel.Verbose, Keywords = Keywords.Cache,
           Message = "Cache miss: [{0}] key={1}")]
    public void CacheMiss(string requestId, string cacheKey)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Cache))
        {
            WriteEvent(6, requestId, cacheKey);
        }
    }
}
