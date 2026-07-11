#if WINDOWS
using System.Collections.Concurrent;
using WindowStream.Core.Hosting;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// In-process replacement for <see cref="WorkerProcessLauncher"/> used by tests
/// that exercise the coordinator/viewer side of the pipeline without spawning
/// real worker processes (which would require NVENC, FFmpeg natives, and a real
/// HWND with active content). Each call to <see cref="LaunchAsync"/> hands back a
/// <see cref="FakeWorkerHandle"/> whose <see cref="IWorkerHandle.Pipe"/> is one
/// half of an in-memory duplex stream pair. Tests drive the other half via
/// <see cref="GetFakeWorker"/> to inject encoded chunks and observe commands.
/// </summary>
sealed class FakeWorkerProcessLauncher : IWorkerProcessLauncher
{
    readonly ConcurrentDictionary<int, FakeWorkerHandle> _handlesByStreamId = new();

    public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
    {
        var handle = new FakeWorkerHandle(arguments);
        _handlesByStreamId[arguments.StreamId] = handle;
        return Task.FromResult<IWorkerHandle>(handle);
    }

    /// <summary>
    /// Returns the test-side endpoints (chunk writer + command reader) for a
    /// previously-launched worker. Returns <c>null</c> if no worker was launched
    /// for the supplied stream id.
    /// </summary>
    public FakeWorkerHandle? GetFakeWorker(int streamId)
        => _handlesByStreamId.TryGetValue(streamId, out var handle) ? handle : null;
}

/// <summary>
/// Handle returned by <see cref="FakeWorkerProcessLauncher"/>. Implements
/// <see cref="IWorkerHandle"/> so the supervisor can drive it as if it were a
/// real worker. The <see cref="WorkerSidePipe"/> property exposes the
/// test-controlled half of the duplex pair: tests call
/// <see cref="WindowStream.Core.Hosting.WorkerChunkPipe.WriteChunkAsync"/> on it
/// to inject encoded NAL units into the coordinator pipeline, and
/// <see cref="WindowStream.Core.Hosting.WorkerChunkPipe.ReadCommandAsync"/> to
/// observe pause/resume/keyframe commands.
/// </summary>
sealed class FakeWorkerHandle : IWorkerHandle
{
    readonly DuplexPipePair _pipePair;
    readonly TaskCompletionSource<int> _exitSource = new TaskCompletionSource<int>();
    bool _disposed;

    public FakeWorkerHandle(WorkerLaunchArguments arguments)
    {
        Arguments = arguments;
        _pipePair = new DuplexPipePair();
    }

    public WorkerLaunchArguments Arguments { get; }

    /// <summary>The supervisor-facing pipe (mirrors what a NamedPipeServerStream provides).</summary>
    public Stream Pipe => _pipePair.SupervisorSide;

    /// <summary>The test-facing pipe; write encoded chunks here, read commands from here.</summary>
    public Stream WorkerSidePipe => _pipePair.WorkerSide;

    public int ProcessId => 0;

    public Task<int> WaitForExitAsync() => _exitSource.Task;

    public void Kill()
    {
        if (_disposed) return;
        _exitSource.TrySetResult(0);
    }

    /// <summary>
    /// Completes the exit task with exit code 1, causing the supervisor's
    /// <see cref="WorkerSupervisor.MonitorAsync"/> to translate to
    /// <see cref="WindowStream.Core.Protocol.StreamStoppedReason.EncoderFailed"/>
    /// and raise <see cref="WindowStream.Core.Hosting.WorkerSupervisor.StreamEnded"/>.
    /// </summary>
    public void SimulateEncoderFailure()
    {
        if (_disposed) return;
        _exitSource.TrySetResult(1);
    }

    /// <summary>
    /// Completes the exit task with exit code 2, causing the supervisor's
    /// <see cref="WorkerSupervisor.MonitorAsync"/> to translate to
    /// <see cref="WindowStream.Core.Protocol.StreamStoppedReason.CaptureFailed"/>
    /// and raise <see cref="WindowStream.Core.Hosting.WorkerSupervisor.StreamEnded"/>.
    /// </summary>
    public void SimulateCaptureFailed()
    {
        if (_disposed) return;
        _exitSource.TrySetResult(2);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _exitSource.TrySetResult(0);
#pragma warning disable CA1031 // best-effort pipe disposal — stream may already be closed
        try { _pipePair.SupervisorSide.Dispose(); } catch { /* best-effort */ }
        try { _pipePair.WorkerSide.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Pair of bidirectional in-memory streams. Writes on one side appear as reads
/// on the other. Each direction uses an independent <see cref="BlockingByteStream"/>
/// so the test thread and the coordinator thread can concurrently write/read
/// without blocking each other.
/// </summary>
sealed class DuplexPipePair
{
    public DuplexPipePair()
    {
        // supervisorWritesPipe: supervisor → worker (commands)
        // workerWritesPipe: worker → supervisor (chunks)
        var supervisorWritesPipe = new BlockingByteStream();
        var workerWritesPipe = new BlockingByteStream();

        SupervisorSide = new DuplexStream(readSource: workerWritesPipe, writeSink: supervisorWritesPipe);
        WorkerSide = new DuplexStream(readSource: supervisorWritesPipe, writeSink: workerWritesPipe);
    }

    public Stream SupervisorSide { get; }
    public Stream WorkerSide { get; }
}

/// <summary>
/// Composes two underlying streams into one bidirectional <see cref="Stream"/>:
/// reads pull from <c>readSource</c>, writes push to <c>writeSink</c>.
/// </summary>
sealed class DuplexStream : Stream
{
    readonly Stream _readSource;
    readonly Stream _writeSink;
    bool _disposed;

    public DuplexStream(Stream readSource, Stream writeSink)
    {
        _readSource = readSource;
        _writeSink = writeSink;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _writeSink.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writeSink.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => _readSource.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _readSource.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _readSource.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _writeSink.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _writeSink.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _writeSink.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
#pragma warning disable CA1031 // best-effort stream disposal — underlying stream may already be closed
            try { _readSource.Dispose(); } catch { /* best-effort */ }
            try { _writeSink.Dispose(); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Thread-safe one-way byte stream backed by a synchronous queue of buffers.
/// Reads block until a writer produces data; writes never block (unbounded).
/// Closing signals EOF to readers (returns 0 once the queue drains).
/// </summary>
sealed class BlockingByteStream : Stream
{
    readonly Queue<byte[]> _chunks = new();
    readonly object _syncRoot = new object();
    byte[]? _currentChunk;
    int _currentChunkOffset;
    bool _closed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_syncRoot)
        {
            while (_currentChunk is null || _currentChunkOffset >= _currentChunk.Length)
            {
                if (_chunks.Count > 0)
                {
                    _currentChunk = _chunks.Dequeue();
                    _currentChunkOffset = 0;
                }
                else if (_closed)
                {
                    return 0;
                }
                else
                {
                    Monitor.Wait(_syncRoot);
                }
            }
            var available = _currentChunk.Length - _currentChunkOffset;
            var copyLength = Math.Min(count, available);
            Array.Copy(_currentChunk, _currentChunkOffset, buffer, offset, copyLength);
            _currentChunkOffset += copyLength;
            return copyLength;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Off-thread the synchronous read so an awaiting consumer doesn't pin a
        // pool thread while waiting for the producer.
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(() =>
        {
            lock (_syncRoot) { Monitor.PulseAll(_syncRoot); }
        });
        return await Task.Run(() =>
        {
            var arrayBuffer = new byte[buffer.Length];
            var read = Read(arrayBuffer, 0, arrayBuffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            arrayBuffer.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_closed, nameof(BlockingByteStream));
            _chunks.Enqueue(copy);
            Monitor.PulseAll(_syncRoot);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return ValueTask.CompletedTask;
        var copy = buffer.ToArray();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_closed, nameof(BlockingByteStream));
            _chunks.Enqueue(copy);
            Monitor.PulseAll(_syncRoot);
        }
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        lock (_syncRoot)
        {
            _closed = true;
            Monitor.PulseAll(_syncRoot);
        }
        base.Dispose(disposing);
    }
}
#endif
