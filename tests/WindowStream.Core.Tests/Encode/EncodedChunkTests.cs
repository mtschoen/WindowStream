using System;
using Xunit;
using WindowStream.Core.Encode;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncodedChunkTests
{
    [Fact]
    public void Constructor_PopulatesProperties()
    {
        byte[] payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 };
        EncodedChunk chunk = new EncodedChunk(
            payload,
            isKeyframe: true,
            presentationTimestampMicroseconds: 1234);
        Assert.True(chunk.isKeyframe);
        Assert.Equal(5, chunk.payload.Length);
    }

    [Fact]
    public void Constructor_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new EncodedChunk(System.Array.Empty<byte>(), false, 0));
    }

    [Fact]
    public void Constructor_NegativeTimestamp_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncodedChunk(new byte[] { 1 }, false, -1));
    }
}
