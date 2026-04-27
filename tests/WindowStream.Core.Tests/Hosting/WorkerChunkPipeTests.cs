using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerChunkPipeTests
{
    [Fact]
    public async Task ChunkRoundTripsThroughMemoryStream()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 0xDEADBEEFCAFEUL,
            IsKeyframe: true,
            Payload: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);

        Assert.Equal(original.PresentationTimestampMicroseconds, read.PresentationTimestampMicroseconds);
        Assert.Equal(original.IsKeyframe, read.IsKeyframe);
        Assert.Equal(original.Payload, read.Payload);
    }

    [Fact]
    public async Task NonKeyframeChunkRoundTrips()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 100UL,
            IsKeyframe: false,
            Payload: new byte[] { 0xFF });

        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.False(read.IsKeyframe);
    }

    [Fact]
    public async Task EmptyPayloadRoundTrips()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(0UL, false, System.Array.Empty<byte>());
        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.Empty(read.Payload);
    }

    [Theory]
    [InlineData(WorkerCommandTag.Pause)]
    [InlineData(WorkerCommandTag.Resume)]
    [InlineData(WorkerCommandTag.RequestKeyframe)]
    [InlineData(WorkerCommandTag.Shutdown)]
    public async Task CommandRoundTrips(WorkerCommandTag tag)
    {
        WorkerCommandFrame original = new WorkerCommandFrame(tag);
        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteCommandAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerCommandFrame read = await WorkerChunkPipe.ReadCommandAsync(stream, CancellationToken.None);
        Assert.Equal(tag, read.Tag);
    }

    [Fact]
    public async Task ReadChunk_OnTruncatedHeader_Throws()
    {
        using MemoryStream stream = new MemoryStream(new byte[] { 0x00, 0x01 }); // partial length
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None));
    }
}
