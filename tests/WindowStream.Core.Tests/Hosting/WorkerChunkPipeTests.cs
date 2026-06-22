using WindowStream.Core.Capture.Detection;
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
        var frame = await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None);
        var read = Assert.IsType<WorkerPipeFrame.ChunkPayload>(frame).Frame;

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
        var frame = await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None);
        var read = Assert.IsType<WorkerPipeFrame.ChunkPayload>(frame).Frame;
        Assert.False(read.IsKeyframe);
    }

    [Fact]
    public async Task EmptyPayloadRoundTrips()
    {
        var original = new WorkerChunkFrame(0UL, false, Array.Empty<byte>());
        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        var frame = await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None);
        var read = Assert.IsType<WorkerPipeFrame.ChunkPayload>(frame).Frame;
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
    public async Task ReadFrame_OnTruncatedHeader_Throws()
    {
        // Write a valid ChunkTag (0x00) followed by partial header bytes to trigger EndOfStreamException
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01 }); // tag + partial length
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None));
    }
}

public sealed class WorkerPipeFrameTests
{
    [Fact]
    public async Task Chunk_round_trips_through_tagged_frame()
    {
        using var stream = new MemoryStream();
        var chunk = new WorkerChunkFrame(1234UL, IsKeyframe: true, Payload: new byte[] { 1, 2, 3, 4 });
        await WorkerChunkPipe.WriteChunkAsync(stream, chunk, CancellationToken.None);
        stream.Position = 0;
        var frame = await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None);
        var payload = Assert.IsType<WorkerPipeFrame.ChunkPayload>(frame);
        Assert.Equal(1234UL, payload.Frame.PresentationTimestampMicroseconds);
        Assert.True(payload.Frame.IsKeyframe);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.Frame.Payload);
    }

    [Fact]
    public async Task Status_round_trips_through_tagged_frame()
    {
        using var stream = new MemoryStream();
        var status = new WorkerStatusFrame(
            WorkerStatusKind.SourceStalled, StallCause.SourceStalled, 250U, "throttle cliff");
        await WorkerChunkPipe.WriteStatusAsync(stream, status, CancellationToken.None);
        stream.Position = 0;
        var frame = await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None);
        var payload = Assert.IsType<WorkerPipeFrame.StatusPayload>(frame);
        Assert.Equal(WorkerStatusKind.SourceStalled, payload.Status.Kind);
        Assert.Equal(StallCause.SourceStalled, payload.Status.Cause);
        Assert.Equal(250U, payload.Status.LastFrameAgeMilliseconds);
        Assert.Equal("throttle cliff", payload.Status.Message);
    }

    [Fact]
    public async Task Interleaved_chunk_then_status_decode_in_order()
    {
        using var stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(
            stream, new WorkerChunkFrame(1UL, false, Array.Empty<byte>()), CancellationToken.None);
        await WorkerChunkPipe.WriteStatusAsync(
            stream, new WorkerStatusFrame(WorkerStatusKind.SourceResumed, StallCause.SourceStalled, 0U, ""),
            CancellationToken.None);
        stream.Position = 0;
        Assert.IsType<WorkerPipeFrame.ChunkPayload>(await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None));
        Assert.IsType<WorkerPipeFrame.StatusPayload>(await WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrameAsync_OnUnknownTag_ThrowsInvalidDataException()
    {
        // Feed an unknown tag byte (0x7F) to ReadFrameAsync - should throw InvalidDataException
        using var stream = new MemoryStream(new byte[] { 0x7F });
        await Assert.ThrowsAsync<InvalidDataException>(
            () => WorkerChunkPipe.ReadFrameAsync(stream, CancellationToken.None));
    }
}
