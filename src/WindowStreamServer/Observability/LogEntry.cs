using System;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    Severity Severity,
    string EventType,
    int? StreamId,
    string Message,
    Exception? Exception);
