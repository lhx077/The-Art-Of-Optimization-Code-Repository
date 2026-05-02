// ---------------------------------------------------------------------------
// HighLatencyEventListener.cs — In-Process Listener for Slow Operations
// ---------------------------------------------------------------------------
// EventListener allows in-process consumption of EventSource events without
// requiring any external tooling. This listener monitors for operations that
// exceed a configurable latency threshold, enabling real-time detection of
// performance anomalies directly within the application process. In a
// production deployment, the OnHighLatencyDetected callback could trigger
// alerts via an APM system, write to a dedicated slow-query log, or
// increment a Prometheus counter for SLO tracking.
// ---------------------------------------------------------------------------

using System.Diagnostics.Tracing;

namespace Exercise04.CustomEventSource;

/// <summary>
/// Subscribes to <see cref="ApiEventSource"/> and filters for completion
/// events whose elapsed time exceeds a configurable threshold. Only
/// "completed" events (RequestCompleted and DatabaseQueryCompleted) carry
/// timing data, so all other events are ignored to minimize processing
/// overhead inside the listener callback.
/// </summary>
public sealed class HighLatencyEventListener : EventListener
{
    private readonly long _thresholdMs;

    // -----------------------------------------------------------------------
    // The threshold is injected at construction time, making it easy to
    // configure different thresholds for different environments. A typical
    // production value might be 200ms for HTTP requests and 100ms for
    // database queries; for this exercise we use a single threshold for
    // simplicity, but the pattern extends naturally to per-event-type
    // thresholds by inspecting the EventId in OnEventWritten.
    // -----------------------------------------------------------------------

    public HighLatencyEventListener(long thresholdMs)
    {
        _thresholdMs = thresholdMs;
    }

    // -----------------------------------------------------------------------
    // OnEventSourceCreated is called by the runtime for every EventSource
    // that exists in the process, including system sources like
    // System.Runtime and Microsoft.AspNetCore.Hosting. We filter by name
    // to enable only our custom source, and we subscribe at Verbose level
    // with all keywords so that both request-level and query-level
    // completion events are delivered to OnEventWritten.
    // -----------------------------------------------------------------------

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "TAOO-Exercise04-ApiEvents")
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    // -----------------------------------------------------------------------
    // OnEventWritten is the hot path of the listener. We check the EventId
    // to determine whether this is a completion event (IDs 2 and 4), then
    // extract the elapsed milliseconds from the payload. The payload array
    // layout is determined by the parameter order in the EventSource method
    // signature: for RequestCompleted it is [requestId, elapsedMs, statusCode],
    // and for DatabaseQueryCompleted it is [requestId, elapsedMs, rowCount].
    // -----------------------------------------------------------------------

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Only process completion events that carry timing information.
        if (eventData.EventId is not (2 or 4))
        {
            return;
        }

        if (eventData.Payload is null || eventData.Payload.Count < 2)
        {
            return;
        }

        string requestId = eventData.Payload[0]?.ToString() ?? "unknown";
        long elapsedMs = Convert.ToInt64(eventData.Payload[1]);

        if (elapsedMs < _thresholdMs)
        {
            return;
        }

        // ---------------------------------------------------------------
        // When a high-latency event is detected, we log it to the console
        // with contextual details. In a real system this would be replaced
        // with a structured log entry, a metric emission, or an alert
        // trigger. The console output here serves as a visible indicator
        // during the exercise that the listener is functioning correctly.
        // ---------------------------------------------------------------

        string eventType = eventData.EventId switch
        {
            2 => "HTTP Request",
            4 => "Database Query",
            _ => "Unknown"
        };

        int detail = eventData.Payload.Count >= 3 ? Convert.ToInt32(eventData.Payload[2]) : 0;
        string detailLabel = eventData.EventId == 2 ? "status" : "rows";

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(
            $"[HIGH LATENCY] {eventType} exceeded threshold: " +
            $"requestId={requestId}, elapsed={elapsedMs}ms (threshold={_thresholdMs}ms), " +
            $"{detailLabel}={detail}");
        Console.ResetColor();
    }
}
