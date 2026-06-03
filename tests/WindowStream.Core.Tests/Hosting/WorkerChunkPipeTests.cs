using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerChunkPipeTests
{
    [Fact]
    public async Task ChunkRoundTripsThroughMemoryStream()
    {
        var original = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 0xDEADBEEFCAFEUL,
            IsKeyframe: true,
            Payload: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        var read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);

        Assert.Equal(original.PresentationTimestampMicroseconds, read.PresentationTimestampMicroseconds);
        Assert.Equal(original.IsKeyframe, read.IsKeyframe);
        Assert.Equal(original.Payload, read.Payload);
    }

    [Fact]
    public async Task NonKeyframeChunkRoundTrips()
    {
        var original = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 100UL,
            IsKeyframe: false,
            Payload: new byte[] { 0xFF });

        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        var read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.False(read.IsKeyframe);
    }

    [Fact]
    public async Task EmptyPayloadRoundTrips()
    {
        var original = new WorkerChunkFrame(0UL, false, Array.Empty<byte>());
        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        var read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.Empty(read.Payload);
    }

    [Theory]
    [InlineData(WorkerCommandTag.Pause)]
    [InlineData(WorkerCommandTag.Resume)]
    [InlineData(WorkerCommandTag.RequestKeyframe)]
    [InlineData(WorkerCommandTag.Shutdown)]
    public async Task CommandRoundTrips(WorkerCommandTag tag)
    {
        var original = new WorkerCommandFrame(tag);
        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteCommandAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        var read = await WorkerChunkPipe.ReadCommandAsync(stream, CancellationToken.None);
        Assert.Equal(tag, read.Tag);
    }

    [Fact]
    public async Task ReadChunk_OnTruncatedHeader_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01 }); // partial length
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None));
    }
}
