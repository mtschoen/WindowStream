using WindowStream.Core.Capture;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        var buffer = new byte[3 * 2 * 4];
        var frame = new CapturedFrame(
            widthPixels: 3,
            heightPixels: 2,
            rowStrideBytes: 12,
            pixelFormat: PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 1_234_567,
            pixelBuffer: buffer);

        Assert.Equal(3, frame.WidthPixels);
        Assert.Equal(2, frame.HeightPixels);
        Assert.Equal(12, frame.RowStrideBytes);
        Assert.Equal(PixelFormat.Bgra32, frame.PixelFormat);
        Assert.Equal(1_234_567L, frame.PresentationTimestampMicroseconds);
        Assert.Equal(buffer.Length, frame.PixelBuffer.Length);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                0, 1, 4, PixelFormat.Bgra32, 0, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                1, 0, 4, PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                10, 2, 4, PixelFormat.Bgra32, 0, new byte[80]));
    }

    [Fact]
    public void Constructor_RejectsBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            new CapturedFrame(
                10, 2, 40, PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_AllowsZeroTimestamp()
    {
        var frame = new CapturedFrame(
            1, 1, 4, PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(0L, frame.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void Constructor_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                1, 1, 4, PixelFormat.Bgra32, -1, new byte[4]));
    }

    [Fact]
    public void Constructor_BytesPath_SetsRepresentationToBytes()
    {
        var frame = new CapturedFrame(
            1, 1, 4, PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(FrameRepresentation.Bytes, frame.Representation);
        Assert.Equal(0, frame.NativeTexturePointer);
        Assert.Equal(0, frame.TextureArrayIndex);
    }

    [Fact]
    public void FromBytes_IsEquivalentToConstructor()
    {
        var buffer = new byte[8];
        var frame = CapturedFrame.FromBytes(
            widthPixels: 2,
            heightPixels: 1,
            rowStrideBytes: 8,
            pixelFormat: PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 42,
            pixelBuffer: buffer);
        Assert.Equal(FrameRepresentation.Bytes, frame.Representation);
        Assert.Equal(buffer.Length, frame.PixelBuffer.Length);
        Assert.Equal(42L, frame.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void FromTexture_PopulatesAllProperties()
    {
        var frame = CapturedFrame.FromTexture(
            widthPixels: 1920,
            heightPixels: 1080,
            rowStrideBytes: 1920,
            pixelFormat: PixelFormat.Nv12,
            presentationTimestampMicroseconds: 1_000_000,
            nativeTexturePointer: 0x12345678,
            textureArrayIndex: 3);

        Assert.Equal(FrameRepresentation.Texture, frame.Representation);
        Assert.Equal(1920, frame.WidthPixels);
        Assert.Equal(1080, frame.HeightPixels);
        Assert.Equal(1920, frame.RowStrideBytes);
        Assert.Equal(PixelFormat.Nv12, frame.PixelFormat);
        Assert.Equal(1_000_000L, frame.PresentationTimestampMicroseconds);
        Assert.Equal(0x12345678, frame.NativeTexturePointer);
        Assert.Equal(3, frame.TextureArrayIndex);
        Assert.Equal(0, frame.PixelBuffer.Length);
    }

    [Fact]
    public void FromTexture_RejectsZeroPointer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                1, 1, 4, PixelFormat.Bgra32, 0, 0, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeArrayIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                1, 1, 4, PixelFormat.Bgra32, 0, 1, -1));
    }

    [Fact]
    public void FromTexture_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                0, 1, 4, PixelFormat.Bgra32, 0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                1, 0, 4, PixelFormat.Bgra32, 0, 1, 0));
    }

    [Fact]
    public void FromTexture_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                10, 2, 4, PixelFormat.Bgra32, 0, 1, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFrame.FromTexture(
                1, 1, 4, PixelFormat.Bgra32, -1, 1, 0));
    }
}
