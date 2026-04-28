#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
internal sealed class FakeWorkerProcessLauncher : IWorkerProcessLauncher
{
    private readonly ConcurrentDictionary<int, FakeWorkerHandle> handlesByStreamId = new();

    public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
    {
        FakeWorkerHandle handle = new FakeWorkerHandle(arguments);
        handlesByStreamId[arguments.StreamId] = handle;
        return Task.FromResult<IWorkerHandle>(handle);
    }

    /// <summary>
    /// Returns the test-side endpoints (chunk writer + command reader) for a
    /// previously-launched worker. Returns <c>null</c> if no worker was launched
    /// for the supplied stream id.
    /// </summary>
    public FakeWorkerHandle? GetFakeWorker(int streamId)
        => handlesByStreamId.TryGetValue(streamId, out FakeWorkerHandle? handle) ? handle : null;
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
internal sealed class FakeWorkerHandle : IWorkerHandle
{
    private readonly DuplexPipePair pipePair;
    private readonly TaskCompletionSource<int> exitSource = new TaskCompletionSource<int>();
    private bool disposed;

    public FakeWorkerHandle(WorkerLaunchArguments arguments)
    {
        Arguments = arguments;
        pipePair = new DuplexPipePair();
    }

    public WorkerLaunchArguments Arguments { get; }

    /// <summary>The supervisor-facing pipe (mirrors what a NamedPipeServerStream provides).</summary>
    public Stream Pipe => pipePair.SupervisorSide;

    /// <summary>The test-facing pipe; write encoded chunks here, read commands from here.</summary>
    public Stream WorkerSidePipe => pipePair.WorkerSide;

    public Task<int> WaitForExitAsync() => exitSource.Task;

    public void Kill()
    {
        if (disposed) return;
        exitSource.TrySetResult(0);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        exitSource.TrySetResult(0);
        try { pipePair.SupervisorSide.Dispose(); } catch { /* best-effort */ }
        try { pipePair.WorkerSide.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Pair of bidirectional in-memory streams. Writes on one side appear as reads
/// on the other. Each direction uses an independent <see cref="BlockingByteStream"/>
/// so the test thread and the coordinator thread can concurrently write/read
/// without blocking each other.
/// </summary>
internal sealed class DuplexPipePair
{
    public DuplexPipePair()
    {
        // supervisorWritesPipe: supervisor → worker (commands)
        // workerWritesPipe: worker → supervisor (chunks)
        BlockingByteStream supervisorWritesPipe = new BlockingByteStream();
        BlockingByteStream workerWritesPipe = new BlockingByteStream();

        SupervisorSide = new DuplexStream(readSource: workerWritesPipe, writeSink: supervisorWritesPipe);
        WorkerSide = new DuplexStream(readSource: supervisorWritesPipe, writeSink: workerWritesPipe);
    }

    public Stream SupervisorSide { get; }
    public Stream WorkerSide { get; }
}

/// <summary>
/// Composes two underlying streams into one bidirectional <see cref="Stream"/>:
/// reads pull from <paramref name="readSource"/>, writes push to <paramref name="writeSink"/>.
/// </summary>
internal sealed class DuplexStream : Stream
{
    private readonly Stream readSource;
    private readonly Stream writeSink;
    private bool disposed;

    public DuplexStream(Stream readSource, Stream writeSink)
    {
        this.readSource = readSource;
        this.writeSink = writeSink;
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

    public override void Flush() => writeSink.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => writeSink.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => readSource.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => readSource.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => readSource.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => writeSink.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => writeSink.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => writeSink.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        disposed = true;
        if (disposing)
        {
            try { readSource.Dispose(); } catch { /* best-effort */ }
            try { writeSink.Dispose(); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Thread-safe one-way byte stream backed by a synchronous queue of buffers.
/// Reads block until a writer produces data; writes never block (unbounded).
/// Closing signals EOF to readers (returns 0 once the queue drains).
/// </summary>
internal sealed class BlockingByteStream : Stream
{
    private readonly System.Collections.Generic.Queue<byte[]> chunks = new();
    private readonly object syncRoot = new object();
    private byte[]? currentChunk;
    private int currentChunkOffset;
    private bool closed;

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
        lock (syncRoot)
        {
            while (currentChunk is null || currentChunkOffset >= currentChunk.Length)
            {
                if (chunks.Count > 0)
                {
                    currentChunk = chunks.Dequeue();
                    currentChunkOffset = 0;
                }
                else if (closed)
                {
                    return 0;
                }
                else
                {
                    Monitor.Wait(syncRoot);
                }
            }
            int available = currentChunk.Length - currentChunkOffset;
            int copyLength = Math.Min(count, available);
            Array.Copy(currentChunk, currentChunkOffset, buffer, offset, copyLength);
            currentChunkOffset += copyLength;
            return copyLength;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Off-thread the synchronous read so an awaiting consumer doesn't pin a
        // pool thread while waiting for the producer.
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            lock (syncRoot) { Monitor.PulseAll(syncRoot); }
        });
        return await Task.Run(() =>
        {
            byte[] arrayBuffer = new byte[buffer.Length];
            int read = Read(arrayBuffer, 0, arrayBuffer.Length);
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
        byte[] copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);
        lock (syncRoot)
        {
            if (closed) throw new ObjectDisposedException(nameof(BlockingByteStream));
            chunks.Enqueue(copy);
            Monitor.PulseAll(syncRoot);
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
        byte[] copy = buffer.ToArray();
        lock (syncRoot)
        {
            if (closed) throw new ObjectDisposedException(nameof(BlockingByteStream));
            chunks.Enqueue(copy);
            Monitor.PulseAll(syncRoot);
        }
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        lock (syncRoot)
        {
            closed = true;
            Monitor.PulseAll(syncRoot);
        }
        base.Dispose(disposing);
    }
}
#endif
