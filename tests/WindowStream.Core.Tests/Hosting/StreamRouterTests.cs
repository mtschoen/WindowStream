using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

// A stream that serves bytes from an inner MemoryStream then blocks on further reads until cancelled.
// Used to keep ReadFromPipeAsync alive (no EOF) so we can verify watchdog state after status frames.
sealed class BlockingTailStream : Stream
{
    readonly MemoryStream _inner;
    public BlockingTailStream(byte[] data) => _inner = new MemoryStream(data);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_inner.Position < _inner.Length)
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        // Block indefinitely until cancelled - simulates a pipe with no more data yet (not EOF)
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class StreamRouterTests
{
    [Fact]
    public async Task Chunk_frames_route_to_sink()
    {
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var router = new StreamRouter(sink, (_, _) => { }, (_, _) => { }, _ => { }, new FakeTimeProvider());
        using var pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe, new WorkerChunkFrame(9UL, false, new byte[] { 7 }), CancellationToken.None);
        pipe.Position = 0;
        var cancellation = new CancellationTokenSource();
        var read = router.ReadFromPipeAsync(5, pipe, cancellation.Token);
        var tagged = await sink.Reader.ReadAsync();
        Assert.Equal(5, tagged.StreamId);
        Assert.Equal(9UL, tagged.Frame.PresentationTimestampMicroseconds);
        await cancellation.CancelAsync();
        await read;
        cancellation.Dispose();
    }

    [Fact]
    public async Task Status_frames_route_to_callback()
    {
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var statuses = new List<(int, WorkerStatusFrame)>();
        var router = new StreamRouter(sink, (id, s) => statuses.Add((id, s)), (_, _) => { }, _ => { }, new FakeTimeProvider());
        using var pipe = new MemoryStream();
        await WorkerChunkPipe.WriteStatusAsync(pipe,
            new WorkerStatusFrame(WorkerStatusKind.SourceStalled, StallCause.SourceStalled, 200U, ""), CancellationToken.None);
        pipe.Position = 0;
        var cancellation = new CancellationTokenSource();
        var read = router.ReadFromPipeAsync(5, pipe, cancellation.Token);
        await read; // EndOfStream after the single frame
        cancellation.Dispose();
        Assert.Single(statuses);
        Assert.Equal(WorkerStatusKind.SourceStalled, statuses[0].Item2.Kind);
    }

    [Fact]
    public void Watchdog_fires_worker_silent_when_no_status_and_chunks_stop()
    {
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int, StallCause)>();
        var router = new StreamRouter(sink, (_, _) => { }, (id, cause) => stalls.Add((id, cause)), _ => { }, time);
        router.RecordChunkForTest(5);
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();
        Assert.Equal((5, StallCause.WorkerSilent), Assert.Single(stalls));
    }

    [Fact]
    public async Task Status_source_stalled_suppresses_watchdog()
    {
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int, StallCause)>();
        var statusSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Use the onStatus callback to signal when the SourceStalled frame has been processed
        var router = new StreamRouter(sink, (_, _) => statusSeen.TrySetResult(), (id, cause) => stalls.Add((id, cause)), _ => { }, time);
        router.RecordChunkForTest(3);
        // Write the status frame into a buffer, then wrap in BlockingTailStream so
        // ReadFromPipeAsync blocks after reading the frame (no EOF = no watchdog removal)
        var buffer = new MemoryStream();
        await WorkerChunkPipe.WriteStatusAsync(buffer,
            new WorkerStatusFrame(WorkerStatusKind.SourceStalled, StallCause.SourceStalled, 100U, ""), CancellationToken.None);
        using var blockingStream = new BlockingTailStream(buffer.ToArray());
        var cancellation = new CancellationTokenSource();
        var readTask = router.ReadFromPipeAsync(3, blockingStream, cancellation.Token);
        // Wait until the status frame has been processed by the router, then cancel
        await statusSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await readTask;
        cancellation.Dispose();
        // Advance past the silent floor - watchdog should be suppressed so no stall fires
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();
        Assert.Empty(stalls);
    }

    [Fact]
    public async Task Status_source_resumed_clears_suppression()
    {
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int, StallCause)>();
        var statusCount = 0;
        var bothStatusesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Use the onStatus callback to signal when both status frames have been processed
        var router = new StreamRouter(sink, (_, _) =>
        {
            if (Interlocked.Increment(ref statusCount) == 2) bothStatusesSeen.TrySetResult();
        }, (id, cause) => stalls.Add((id, cause)), _ => { }, time);
        router.RecordChunkForTest(6);
        var buffer = new MemoryStream();
        await WorkerChunkPipe.WriteStatusAsync(buffer,
            new WorkerStatusFrame(WorkerStatusKind.SourceStalled, StallCause.SourceStalled, 100U, ""), CancellationToken.None);
        await WorkerChunkPipe.WriteStatusAsync(buffer,
            new WorkerStatusFrame(WorkerStatusKind.SourceResumed, StallCause.SourceStalled, 0U, ""), CancellationToken.None);
        using var blockingStream = new BlockingTailStream(buffer.ToArray());
        var cancellation = new CancellationTokenSource();
        var readTask = router.ReadFromPipeAsync(6, blockingStream, cancellation.Token);
        await bothStatusesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await readTask;
        cancellation.Dispose();
        // After SourceResumed suppression is cleared; advancing past floor now fires the watchdog
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();
        Assert.Single(stalls);
        Assert.Equal((6, StallCause.WorkerSilent), stalls[0]);
    }

    [Fact]
    public void Watchdog_resumed_fires_when_chunks_return_after_stall()
    {
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int, StallCause)>();
        var resumed = new List<int>();
        var router = new StreamRouter(sink, (_, _) => { }, (id, cause) => stalls.Add((id, cause)), id => resumed.Add(id), time);
        // First tick into stall
        router.RecordChunkForTest(7);
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();
        Assert.Single(stalls);
        // Advance time further so RecordChunk sets a fresh timestamp, then evaluate - age is ~0 so resumed
        time.Advance(TimeSpan.FromMilliseconds(3100));
        router.RecordChunkForTest(7);
        router.EvaluateWatchdogs();
        Assert.Single(resumed);
        Assert.Equal(7, resumed[0]);
    }

    [Fact]
    public async Task EndOfStream_removes_watchdog_entry()
    {
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int, StallCause)>();
        var router = new StreamRouter(sink, (_, _) => { }, (id, cause) => stalls.Add((id, cause)), _ => { }, time);
        using var emptyPipe = new MemoryStream(); // immediately EOF
        router.RecordChunkForTest(10);
        await router.ReadFromPipeAsync(10, emptyPipe, CancellationToken.None);
        // Watchdog for stream 10 should have been removed; advancing past floor should not fire
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();
        Assert.Empty(stalls);
    }

    [Fact]
    public async Task Cancellation_stops_reader_without_throw()
    {
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var router = new StreamRouter(sink, (_, _) => { }, (_, _) => { }, _ => { }, new FakeTimeProvider());
        using var pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe, new WorkerChunkFrame(1UL, false, new byte[] { 0x01 }), CancellationToken.None);
        pipe.Position = 0;
        var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        // Should complete without throwing even when token is already cancelled
        await router.ReadFromPipeAsync(9, pipe, cancellation.Token);
        cancellation.Dispose();
    }

    [Fact]
    public async Task Transitional_constructor_routes_chunks()
    {
        // Exercises the 1-arg transitional constructor (kept for backward compat until Task 9)
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var router = new StreamRouter(sink);
        using var pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe, new WorkerChunkFrame(42UL, true, new byte[] { 0xAB }), CancellationToken.None);
        pipe.Position = 0;
        await router.ReadFromPipeAsync(1, pipe, CancellationToken.None);
        Assert.True(sink.Reader.TryRead(out var tagged));
        Assert.Equal(1, tagged.StreamId);
        Assert.Equal(42UL, tagged.Frame.PresentationTimestampMicroseconds);
    }
}
