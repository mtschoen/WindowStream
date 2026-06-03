using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncodedChunkTests
{
    [Fact]
    public void Constructor_PopulatesProperties()
    {
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 };
        var chunk = new EncodedChunk(
            payload,
            isKeyframe: true,
            presentationTimestampMicroseconds: 1234);
        Assert.True(chunk.IsKeyframe);
        Assert.Equal(5, chunk.Payload.Length);
        Assert.Equal(1234L, chunk.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void Constructor_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new EncodedChunk(Array.Empty<byte>(), false, 0));
    }

    [Fact]
    public void Constructor_NegativeTimestamp_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncodedChunk(new byte[] { 1 }, false, -1));
    }

    [Fact]
    public void Constructor_ZeroTimestamp_IsValid()
    {
        var chunk = new EncodedChunk(new byte[] { 1 }, false, 0);
        Assert.Equal(0L, chunk.PresentationTimestampMicroseconds);
    }
}
