using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class LoadShedderTests
{
    private static TaggedChunk Chunk(int streamId, ulong pts, bool keyframe = false)
        => new TaggedChunk(streamId, new WorkerChunkFrame(pts, keyframe, new byte[] { 0xFF }));

    [Fact]
    public async Task UnderThreshold_PassesAllChunks()
    {
        Channel<TaggedChunk> input = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        LoadShedder shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 4);

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100));
        await input.Writer.WriteAsync(Chunk(1, 200));
        await input.Writer.WriteAsync(Chunk(1, 300));

        Assert.Equal(100UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(200UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(300UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);

        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }

    // The threshold-trip behavior is implementation-detail (how exactly we
    // detect "pressure"). Spec leaves the trigger mechanism to implementation.
    // The KEYFRAME-NEVER-DROPPED invariant is the non-negotiable test.
    [Fact]
    public async Task KeyframesAreNeverDropped()
    {
        // Bounded output of size 1 + producer that blocks until consumer drains.
        Channel<TaggedChunk> input = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> output = Channel.CreateBounded<TaggedChunk>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        LoadShedder shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 1);
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100, keyframe: false)); // fills bounded(1) output
        await input.Writer.WriteAsync(Chunk(1, 200, keyframe: false)); // queued internally — backpressured
        await input.Writer.WriteAsync(Chunk(1, 300, keyframe: true));  // keyframe — must survive

        // Let the shedder process all three inputs before we start draining.
        await Task.Delay(150, cancellation.Token);

        // Drain output one slot at a time. After each read, send another
        // non-keyframe to re-trigger the shedder's drain step (the shedder
        // pushes-to-output only on input arrival in this implementation).
        TaggedChunk first = await output.Reader.ReadAsync(cancellation.Token);
        await input.Writer.WriteAsync(Chunk(1, 400, keyframe: false), cancellation.Token);
        await Task.Delay(50, cancellation.Token);
        TaggedChunk second = await output.Reader.ReadAsync(cancellation.Token);

        Assert.True(first.Frame.IsKeyframe || second.Frame.IsKeyframe,
            "keyframe (pts=300) must appear in output");

        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }
}
