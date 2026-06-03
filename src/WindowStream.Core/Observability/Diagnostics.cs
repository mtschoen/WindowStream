using Microsoft.Extensions.Logging;

namespace WindowStream.Core.Observability;

// Façade that translates PipelineEvent into a structured ILogger call.
// This is the one place that decides how an event maps to a log record.
public sealed class Diagnostics
{
    readonly ILogger _logger;
    Action<PipelineEvent>? _subscriber;

    public Diagnostics(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Subscribe(Action<PipelineEvent> handler)
    {
        _subscriber = handler;
    }

    public void Report(PipelineEvent pipelineEvent)
    {
        ArgumentNullException.ThrowIfNull(pipelineEvent);

        var logLevel = pipelineEvent.Severity switch
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

        var exception = pipelineEvent switch
        {
            PipelineEvent.WorkerSpawnFailed workerSpawnFailed => workerSpawnFailed.Exception,
            PipelineEvent.CaptureFailed captureFailed => captureFailed.Exception,
            PipelineEvent.EncodeFailed encodeFailed => encodeFailed.Exception,
            PipelineEvent.ProbeFailed probeFailed => probeFailed.Exception,
            PipelineEvent.EnumerationFailed enumerationFailed => enumerationFailed.Exception,
            _ => null,
        };

        using (_logger.BeginScope(scopeProperties))
        {
            _logger.Log(logLevel, default, pipelineEvent, exception,
                static (state, _) => state.GetType().Name + ": " + state);
        }

        _subscriber?.Invoke(pipelineEvent);
    }
}
