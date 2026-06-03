using System.Threading.Channels;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class StreamRouterTests
{
    [Fact]
    public async Task RoutesChunksFromPipe_TaggedWithStreamId()
    {
        var output = Channel.CreateUnbounded<TaggedChunk>();
        var router = new StreamRouter(output);

        var pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(100UL, true, new byte[] { 0xAA }), CancellationToken.None);
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(200UL, false, new byte[] { 0xBB }), CancellationToken.None);
        pipe.Position = 0;

        // readerTask is awaited inside the using scope (line below the assertions), so
        // the CancellationTokenSource is still live when the task completes.
#pragma warning disable CA2025 // readerTask is awaited before cancellation goes out of scope
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readerTask = router.ReadFromPipeAsync(streamId: 7, pipe, cancellation.Token);
#pragma warning restore CA2025

        var first = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, first.StreamId);
        Assert.Equal(100UL, first.Frame.PresentationTimestampMicroseconds);
        Assert.True(first.Frame.IsKeyframe);

        var second = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, second.StreamId);
        Assert.False(second.Frame.IsKeyframe);

        await cancellation.CancelAsync();
        try { await readerTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PipeClosed_StopsReader_DoesNotThrow()
    {
        var output = Channel.CreateUnbounded<TaggedChunk>();
        var router = new StreamRouter(output);
        var emptyPipe = new MemoryStream();
        await router.ReadFromPipeAsync(streamId: 1, emptyPipe, CancellationToken.None);
        Assert.False(output.Reader.TryRead(out _));
    }
}
