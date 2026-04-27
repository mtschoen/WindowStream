using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Hosting;

public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly IWorkerProcessLauncher launcher;
    private readonly int maximumConcurrentStreams;
    private readonly ConcurrentDictionary<int, ActiveStream> activeStreams = new();
    private int nextStreamId;
    private bool disposed;

    public event EventHandler<StreamEndedEventArguments>? StreamEnded;

    public WorkerSupervisor(IWorkerProcessLauncher launcher, int maximumConcurrentStreams)
    {
        this.launcher = launcher;
        this.maximumConcurrentStreams = maximumConcurrentStreams;
    }

    public async Task<StreamHandle> StartStreamAsync(
        ulong windowId,
        long hwnd,
        EncoderOptions encoderOptions,
        CancellationToken cancellationToken)
    {
        if (activeStreams.Count >= maximumConcurrentStreams)
        {
            throw new EncoderCapacityException(maximumConcurrentStreams);
        }

        int streamId = Interlocked.Increment(ref nextStreamId);
        string pipeName = $"windowstream-{Environment.ProcessId}-{streamId}";

        WorkerLaunchArguments launchArguments = new WorkerLaunchArguments(
            hwnd,
            streamId,
            pipeName,
            JsonSerializer.Serialize(encoderOptions));
        IWorkerHandle handle = await launcher.LaunchAsync(launchArguments, cancellationToken).ConfigureAwait(false);

        ActiveStream record = new ActiveStream(streamId, windowId, handle);
        activeStreams[streamId] = record;

        _ = MonitorAsync(record);
        return new StreamHandle(streamId, windowId);
    }

    public async Task StopStreamAsync(int streamId)
    {
        if (!activeStreams.TryGetValue(streamId, out ActiveStream? record))
        {
            return;
        }
        record.Expected = ExpectedExit.ClosedByViewer;
        record.Handle.Kill();
        await record.Handle.DisposeAsync().ConfigureAwait(false);
    }

    public Stream? GetPipe(int streamId)
        => activeStreams.TryGetValue(streamId, out ActiveStream? record) ? record.Handle.Pipe : null;

    private async Task MonitorAsync(ActiveStream record)
    {
        int exitCode = await record.Handle.WaitForExitAsync().ConfigureAwait(false);
        activeStreams.TryRemove(record.StreamId, out _);
        StreamStoppedReason reason = record.Expected switch
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
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (System.Collections.Generic.KeyValuePair<int, ActiveStream> pair in activeStreams)
        {
            pair.Value.Expected = ExpectedExit.ClosedByViewer;
            pair.Value.Handle.Kill();
            await pair.Value.Handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum ExpectedExit
    {
        Unset,
        ClosedByViewer
    }

    private sealed class ActiveStream
    {
        public ActiveStream(int streamId, ulong windowId, IWorkerHandle handle)
        {
            StreamId = streamId;
            WindowId = windowId;
            Handle = handle;
            Expected = ExpectedExit.Unset;
        }

        public int StreamId { get; }

        public ulong WindowId { get; }

        public IWorkerHandle Handle { get; }

        public ExpectedExit Expected { get; set; }
    }
}
