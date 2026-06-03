using System.Collections.Concurrent;
using System.Text.Json;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Hosting;

public sealed class WorkerSupervisor : IAsyncDisposable
{
    readonly IWorkerProcessLauncher _launcher;
    readonly int _maximumConcurrentStreams;
    readonly ConcurrentDictionary<int, ActiveStream> _activeStreams = new();
    int _nextStreamId;
    bool _disposed;

    public event EventHandler<StreamEndedEventArguments>? StreamEnded;

    public event EventHandler<StreamStartedEventArguments>? StreamStarted;

    public WorkerSupervisor(IWorkerProcessLauncher launcher, int maximumConcurrentStreams)
    {
        _launcher = launcher;
        _maximumConcurrentStreams = maximumConcurrentStreams;
    }

    public async Task<StreamHandle> StartStreamAsync(
        ulong windowId,
        long hwnd,
        EncoderOptions encoderOptions,
        CancellationToken cancellationToken)
    {
        if (_activeStreams.Count >= _maximumConcurrentStreams)
        {
            throw new EncoderCapacityException(_maximumConcurrentStreams);
        }

        var streamId = Interlocked.Increment(ref _nextStreamId);
        var pipeName = $"windowstream-{Environment.ProcessId}-{streamId}";

        var launchArguments = new WorkerLaunchArguments(
            hwnd,
            streamId,
            pipeName,
            JsonSerializer.Serialize(encoderOptions));
        var handle = await _launcher.LaunchAsync(launchArguments, cancellationToken).ConfigureAwait(false);

        var record = new ActiveStream(streamId, windowId, handle);
        _activeStreams[streamId] = record;

        StreamStarted?.Invoke(this, new StreamStartedEventArguments(streamId, windowId, handle.Pipe, handle.ProcessId));

        _ = MonitorAsync(record);
        return new StreamHandle(streamId, windowId);
    }

    public async Task StopStreamAsync(int streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var record))
        {
            return;
        }
        record.Expected = ExpectedExit.ClosedByViewer;
        record.Handle.Kill();
        await record.Handle.DisposeAsync().ConfigureAwait(false);
    }

    public Stream? GetPipe(int streamId)
        => _activeStreams.TryGetValue(streamId, out var record) ? record.Handle.Pipe : null;

    async Task MonitorAsync(ActiveStream record)
    {
        var exitCode = await record.Handle.WaitForExitAsync().ConfigureAwait(false);
        _activeStreams.TryRemove(record.StreamId, out _);
        var reason = record.Expected switch
        {
            ExpectedExit.ClosedByViewer => StreamStoppedReason.ClosedByViewer,
            _ => exitCode switch
            {
                0 => StreamStoppedReason.ClosedByViewer,
                1 => StreamStoppedReason.EncoderFailed,
                2 => StreamStoppedReason.CaptureFailed,
                _ => StreamStoppedReason.EncoderFailed
            }
        };
        StreamEnded?.Invoke(this, new StreamEndedEventArguments(record.StreamId, reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var pair in _activeStreams)
        {
            pair.Value.Expected = ExpectedExit.ClosedByViewer;
            pair.Value.Handle.Kill();
            await pair.Value.Handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    enum ExpectedExit
    {
        Unset,
        ClosedByViewer
    }

    sealed class ActiveStream
    {
        public ActiveStream(int streamId, ulong windowId, IWorkerHandle handle)
        {
            StreamId = streamId;
            WindowId = windowId;
            Handle = handle;
            Expected = ExpectedExit.Unset;
        }

        public int StreamId { get; }

        // Stream-to-window association retained for diagnostics and symmetry with StreamId.
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public ulong WindowId { get; }

        public IWorkerHandle Handle { get; }

        public ExpectedExit Expected { get; set; }
    }
}
