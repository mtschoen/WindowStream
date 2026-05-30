using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace WindowStream.Core.Observability;

// Façade that translates PipelineEvent into a structured ILogger call.
// This is the one place that decides how an event maps to a log record.
public sealed class Diagnostics
{
    private readonly ILogger logger;
    private Action<PipelineEvent>? subscriber;

    public Diagnostics(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Subscribe(Action<PipelineEvent> handler)
    {
        subscriber = handler;
    }

    public void Report(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);

        LogLevel logLevel = pipelineEvent.Severity switch
        {
            Severity.Warning => LogLevel.Warning,
            Severity.Error => LogLevel.Error,
            _ => LogLevel.Information,
        };

        Dictionary<string, object?> scopeProperties = new()
        {
            ["EventType"] = pipelineEvent.GetType().Name,
            ["StreamId"] = pipelineEvent.StreamId,
        };

        Exception? exception = pipelineEvent switch
        {
            PipelineEvent.WorkerSpawnFailed workerSpawnFailed => workerSpawnFailed.Exception,
            PipelineEvent.CaptureFailed captureFailed => captureFailed.Exception,
            PipelineEvent.EncodeFailed encodeFailed => encodeFailed.Exception,
            PipelineEvent.ProbeFailed probeFailed => probeFailed.Exception,
            PipelineEvent.EnumerationFailed enumerationFailed => enumerationFailed.Exception,
            _ => null,
        };

        using (logger.BeginScope(scopeProperties))
        {
            logger.Log(logLevel, default, pipelineEvent, exception,
                static (state, _) => state.GetType().Name + ": " + state.ToString());
        }

        subscriber?.Invoke(pipelineEvent);
    }
}
