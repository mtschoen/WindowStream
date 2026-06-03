using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

public sealed class InAppDashboardSink : ILogEventSink
{
    readonly int _capacity;
    readonly Queue<LogEntry> _buffer;
    readonly object _syncRoot = new();

    public event Action<LogEntry>? OnEvent;

    public InAppDashboardSink(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _buffer = new Queue<LogEntry>(capacity);
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = MapToEntry(logEvent);
        lock (_syncRoot)
        {
            if (_buffer.Count == _capacity) _buffer.Dequeue();
            _buffer.Enqueue(entry);
        }
        OnEvent?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_syncRoot) return _buffer.ToArray();
    }

    static LogEntry MapToEntry(LogEvent logEvent)
    {
        var severity = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug or LogEventLevel.Information => Severity.Info,
            LogEventLevel.Warning => Severity.Warning,
            _ => Severity.Error,
        };

        var eventType = logEvent.Properties.TryGetValue("EventType", out var eventTypeValue)
            ? eventTypeValue.ToString().Trim('"')
            : "Log";

        int? streamId = null;
        if (logEvent.Properties.TryGetValue("StreamId", out var streamIdValue) &&
            streamIdValue is ScalarValue { Value: int streamIdInt })
        {
            streamId = streamIdInt;
        }

        return new LogEntry(
            Timestamp: logEvent.Timestamp,
            Severity: severity,
            EventType: eventType,
            StreamId: streamId,
            Message: logEvent.RenderMessage(CultureInfo.InvariantCulture),
            Exception: logEvent.Exception);
    }
}
