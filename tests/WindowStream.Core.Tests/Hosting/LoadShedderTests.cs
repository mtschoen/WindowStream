using System.Threading.Channels;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class LoadShedderTests
{
    static TaggedChunk Chunk(int streamId, ulong pts, bool keyframe = false)
        => new TaggedChunk(streamId, new WorkerChunkFrame(pts, keyframe, new byte[] { 0xFF }));

    [Fact]
    public async Task UnderThreshold_PassesAllChunks()
    {
        var input = Channel.CreateUnbounded<TaggedChunk>();
        var output = Channel.CreateUnbounded<TaggedChunk>();
        var shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 4);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100));
        await input.Writer.WriteAsync(Chunk(1, 200));
        await input.Writer.WriteAsync(Chunk(1, 300));

        Assert.Equal(100UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(200UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(300UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);

        await cancellation.CancelAsync();
        try { await task; } catch (OperationCanceledException) { }
    }

    // The threshold-trip behavior is implementation-detail (how exactly we
    // detect "pressure"). Spec leaves the trigger mechanism to implementation.
    // The KEYFRAME-NEVER-DROPPED invariant is the non-negotiable test.
    [Fact]
    public async Task KeyframesAreNeverDropped()
    {
        // Bounded output of size 1 + producer that blocks until consumer drains.
        var input = Channel.CreateUnbounded<TaggedChunk>();
        var output = Channel.CreateBounded<TaggedChunk>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        var shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100, keyframe: false));
        await input.Writer.WriteAsync(Chunk(1, 200, keyframe: false));
        await input.Writer.WriteAsync(Chunk(1, 300, keyframe: true));

        var emittedChunks = new List<TaggedChunk>();
        var nextPresentationTimestampMicroseconds = 400UL;
        while (emittedChunks.Count < 3)
        {
            var emittedChunk = await output.Reader.ReadAsync(cancellation.Token);
            emittedChunks.Add(emittedChunk);
            if (emittedChunk.Frame.IsKeyframe)
            {
                break;
            }

            await input.Writer.WriteAsync(
                Chunk(1, nextPresentationTimestampMicroseconds),
                cancellation.Token);
            nextPresentationTimestampMicroseconds += 100;
        }

        Assert.Contains(emittedChunks, chunk =>
            chunk.Frame.IsKeyframe && chunk.Frame.PresentationTimestampMicroseconds == 300);

        await cancellation.CancelAsync();
        try { await task; } catch (OperationCanceledException) { }
    }
}
