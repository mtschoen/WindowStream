using System;
using Xunit;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        byte[] pixels = new byte[32];
        CapturedFrame frame = new CapturedFrame(4, 4, 8, PixelFormat.Bgra32, 500, pixels);

        Assert.Equal(4, frame.widthPixels);
        Assert.Equal(4, frame.heightPixels);
        Assert.Equal(8, frame.rowStrideBytes);
        Assert.Equal(PixelFormat.Bgra32, frame.pixelFormat);
        Assert.Equal(500L, frame.presentationTimestampMicroseconds);
        Assert.Equal(32, frame.pixelBuffer.Length);
    }

    [Theory]
    [InlineData(0, 4, 8)]
    [InlineData(-1, 4, 8)]
    public void Constructor_InvalidWidth_Throws(int width, int height, int stride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(width, height, stride, PixelFormat.Bgra32, 0, new byte[16]));
    }

    [Theory]
    [InlineData(4, 0, 8)]
    [InlineData(4, -1, 8)]
    public void Constructor_InvalidHeight_Throws(int width, int height, int stride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(width, height, stride, PixelFormat.Bgra32, 0, new byte[16]));
    }

    [Theory]
    [InlineData(4, 4, 0)]
    [InlineData(4, 4, -1)]
    public void Constructor_InvalidStride_Throws(int width, int height, int stride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(width, height, stride, PixelFormat.Bgra32, 0, new byte[16]));
    }

    [Fact]
    public void Constructor_NegativeTimestamp_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(4, 4, 8, PixelFormat.Bgra32, -1, new byte[16]));
    }

    [Fact]
    public void Constructor_EmptyPixelBuffer_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new CapturedFrame(4, 4, 8, PixelFormat.Bgra32, 0, Array.Empty<byte>()));
    }
}
