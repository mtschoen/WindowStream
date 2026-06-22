using System.Collections.Concurrent;
using System.Threading.Channels;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Hosting.Detection;

namespace WindowStream.Core.Hosting;

public sealed class StreamRouter
{
    readonly Channel<TaggedChunk> _sink;
    readonly Action<int, WorkerStatusFrame> _onStatus;
    readonly Action<int, StallCause> _onWatchdogStalled;
    readonly Action<int> _onWatchdogResumed;
    readonly TimeProvider _timeProvider;
    readonly ConcurrentDictionary<int, ChunkCadenceWatchdog> _watchdogs = new();

    public StreamRouter(
        Channel<TaggedChunk> sink,
        Action<int, WorkerStatusFrame> onStatus,
        Action<int, StallCause> onWatchdogStalled,
        Action<int> onWatchdogResumed,
        TimeProvider timeProvider)
    {
        _sink = sink;
        _onStatus = onStatus;
        _onWatchdogStalled = onWatchdogStalled;
        _onWatchdogResumed = onWatchdogResumed;
        _timeProvider = timeProvider;
    }

    // Transitional: keeps CoordinatorLauncher and the loopback harness building until
    // Task 9 wires real callbacks. Remove when all callers migrate to the 5-arg constructor.
    public StreamRouter(Channel<TaggedChunk> sink)
        : this(sink, (_, _) => { }, (_, _) => { }, _ => { }, TimeProvider.System) { }

    ChunkCadenceWatchdog WatchdogFor(int streamId) =>
        _watchdogs.GetOrAdd(streamId, _ => new ChunkCadenceWatchdog(_timeProvider, ChunkCadenceWatchdogOptions.Default));

    internal void RecordChunkForTest(int streamId) => WatchdogFor(streamId).RecordChunk();

    public async Task ReadFromPipeAsync(int streamId, Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await WorkerChunkPipe.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                switch (frame)
                {
                    case WorkerPipeFrame.ChunkPayload chunk:
                        WatchdogFor(streamId).RecordChunk();
                        await _sink.Writer.WriteAsync(new TaggedChunk(streamId, chunk.Frame), cancellationToken).ConfigureAwait(false);
                        break;
                    case WorkerPipeFrame.StatusPayload status:
                        ApplyStatusSuppression(streamId, status.Status);
                        _onStatus(streamId, status.Status);
                        break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            _watchdogs.TryRemove(streamId, out _);
        }
        catch (OperationCanceledException)
        {
            // normal cancellation
        }
    }

    void ApplyStatusSuppression(int streamId, WorkerStatusFrame status)
    {
        if (status.Kind == WorkerStatusKind.SourceStalled)
        {
            WatchdogFor(streamId).SetWorkerReportedStalled(true);
        }
        else if (status.Kind == WorkerStatusKind.SourceResumed)
        {
            WatchdogFor(streamId).SetWorkerReportedStalled(false);
        }
    }

    public void EvaluateWatchdogs()
    {
        foreach (var (streamId, watchdog) in _watchdogs)
        {
            switch (watchdog.Evaluate())
            {
                case StallTransition.Stalled:
                    _onWatchdogStalled(streamId, watchdog.LastStallCause);
                    break;
                case StallTransition.Resumed:
                    _onWatchdogResumed(streamId);
                    break;
                case StallTransition.None:
                    break;
            }
        }
    }
}
