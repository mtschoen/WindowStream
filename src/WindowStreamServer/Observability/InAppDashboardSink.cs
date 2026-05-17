using System;
using System.Collections.Generic;
using Serilog.Core;
using Serilog.Events;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

public sealed class InAppDashboardSink : ILogEventSink
{
    private readonly int capacity;
    private readonly Queue<LogEntry> buffer;
    private readonly object syncRoot = new();

    public event Action<LogEntry>? OnEvent;

    public InAppDashboardSink(int capacity = 500)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        buffer = new Queue<LogEntry>(capacity);
    }

    public void Emit(LogEvent logEvent)
    {
        LogEntry entry = MapToEntry(logEvent);
        lock (syncRoot)
        {
            if (buffer.Count == capacity) buffer.Dequeue();
            buffer.Enqueue(entry);
        }
        OnEvent?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (syncRoot) return buffer.ToArray();
    }

    private static LogEntry MapToEntry(LogEvent logEvent)
    {
        Severity severity = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug or LogEventLevel.Information => Severity.Info,
            LogEventLevel.Warning => Severity.Warning,
            _ => Severity.Error,
        };

        string eventType = logEvent.Properties.TryGetValue("EventType", out LogEventPropertyValue? eventTypeValue)
            ? eventTypeValue.ToString().Trim('"')
            : "Log";

        int? streamId = null;
        if (logEvent.Properties.TryGetValue("StreamId", out LogEventPropertyValue? streamIdValue) &&
            streamIdValue is ScalarValue { Value: int streamIdInt })
        {
            streamId = streamIdInt;
        }

        return new LogEntry(
            Timestamp: logEvent.Timestamp,
            Severity: severity,
            EventType: eventType,
            StreamId: streamId,
            Message: logEvent.RenderMessage(),
            Exception: logEvent.Exception);
    }
}
