using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class StreamRouterTests
{
    [Fact]
    public async Task RoutesChunksFromPipe_TaggedWithStreamId()
    {
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        StreamRouter router = new StreamRouter(output);

        MemoryStream pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(100UL, true, new byte[] { 0xAA }), CancellationToken.None);
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(200UL, false, new byte[] { 0xBB }), CancellationToken.None);
        pipe.Position = 0;

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task readerTask = router.ReadFromPipeAsync(streamId: 7, pipe, cancellation.Token);

        TaggedChunk first = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, first.StreamId);
        Assert.Equal(100UL, first.Frame.PresentationTimestampMicroseconds);
        Assert.True(first.Frame.IsKeyframe);

        TaggedChunk second = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, second.StreamId);
        Assert.False(second.Frame.IsKeyframe);

        cancellation.Cancel();
        try { await readerTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PipeClosed_StopsReader_DoesNotThrow()
    {
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        StreamRouter router = new StreamRouter(output);
        MemoryStream emptyPipe = new MemoryStream();
        await router.ReadFromPipeAsync(streamId: 1, emptyPipe, CancellationToken.None);
        Assert.False(output.Reader.TryRead(out _));
    }
}
