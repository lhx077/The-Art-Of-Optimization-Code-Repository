// ============================================================================
// Exercise 04 — Dataflow Log Analysis System
// ============================================================================
// Builds a real-time log analysis pipeline with parsing, conditional routing,
// batching, and graceful completion using TPL Dataflow.
// ============================================================================

using System.Threading.Tasks.Dataflow;

await Demo.RunAsync();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        LogAnalysisPipeline pipeline = new();
        await pipeline.PostAsync("INFO|startup complete");
        await pipeline.PostAsync("WARN|cache pressure detected");
        await pipeline.PostAsync("ERROR|database unavailable");
        await pipeline.CompleteAsync();
        Console.WriteLine("Log pipeline drained successfully.");
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class LogAnalysisPipeline
{
    private readonly BufferBlock<string> _input = new(new DataflowBlockOptions { BoundedCapacity = 128 });
    // Give parsing or transformation its own stage so CPU work can scale independently.
    private readonly TransformBlock<string, LogEntry> _parse;
    private readonly BatchBlock<LogEntry> _infoBatch = new(4);
    private readonly BatchBlock<LogEntry> _warnBatch = new(4);
    private readonly BatchBlock<LogEntry> _errorBatch = new(4);
    // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
    private readonly ActionBlock<LogEntry[]> _writeInfo;
    // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
    private readonly ActionBlock<LogEntry[]> _writeWarn;
    // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
    private readonly ActionBlock<LogEntry[]> _writeError;

    public LogAnalysisPipeline()
    {
        // Give parsing or transformation its own stage so CPU work can scale independently.
        _parse = new TransformBlock<string, LogEntry>(Parse, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, BoundedCapacity = 128 });
        _writeInfo = CreateWriter("INFO");
        _writeWarn = CreateWriter("WARN");
        _writeError = CreateWriter("ERROR");

        _input.LinkTo(_parse, new DataflowLinkOptions { PropagateCompletion = true });
        _parse.LinkTo(_infoBatch, new DataflowLinkOptions { PropagateCompletion = true }, entry => entry.Level == LogLevel.Info);
        _parse.LinkTo(_warnBatch, new DataflowLinkOptions { PropagateCompletion = true }, entry => entry.Level == LogLevel.Warn);
        _parse.LinkTo(_errorBatch, new DataflowLinkOptions { PropagateCompletion = true }, entry => entry.Level == LogLevel.Error);
        _parse.LinkTo(DataflowBlock.NullTarget<LogEntry>());

        _infoBatch.LinkTo(_writeInfo, new DataflowLinkOptions { PropagateCompletion = true });
        _warnBatch.LinkTo(_writeWarn, new DataflowLinkOptions { PropagateCompletion = true });
        _errorBatch.LinkTo(_writeError, new DataflowLinkOptions { PropagateCompletion = true });
    }

    public Task<bool> PostAsync(string raw) => _input.SendAsync(raw);

    public async Task CompleteAsync()
    {
        _input.Complete();
        await Task.WhenAll(_writeInfo.Completion, _writeWarn.Completion, _writeError.Completion);
    }

    // Parse incrementally so the sample can demonstrate the intended hot path clearly.
    private static LogEntry Parse(string raw)
    {
        string[] parts = raw.Split('|', 2);
        LogLevel level = parts[0] switch
        {
            "INFO" => LogLevel.Info,
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info
        };

        return new LogEntry(level, parts.Length > 1 ? parts[1] : string.Empty, DateTime.UtcNow);
    }

    // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
    private static ActionBlock<LogEntry[]> CreateWriter(string bucket)
    {
        // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
        return new ActionBlock<LogEntry[]>(async entries =>
        {
            await Task.Delay(5);
            Console.WriteLine($"{bucket} batch size: {entries.Length}");
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 16 });
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct LogEntry(LogLevel Level, string Message, DateTime TimestampUtc);
public enum LogLevel { Info, Warn, Error }
