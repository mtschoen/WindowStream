using System;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        byte[] buffer = new byte[3 * 2 * 4];
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            widthPixels: 3,
            heightPixels: 2,
            rowStrideBytes: 12,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 1_234_567,
            pixelBuffer: buffer);

        Assert.Equal(3, frame.widthPixels);
        Assert.Equal(2, frame.heightPixels);
        Assert.Equal(12, frame.rowStrideBytes);
        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Bgra32, frame.pixelFormat);
        Assert.Equal(1_234_567L, frame.presentationTimestampMicroseconds);
        Assert.Equal(buffer.Length, frame.pixelBuffer.Length);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                0, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 0, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[80]));
    }

    [Fact]
    public void Constructor_RejectsBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 40, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_AllowsZeroTimestamp()
    {
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(0L, frame.presentationTimestampMicroseconds);
    }

    [Fact]
    public void Constructor_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, -1, new byte[4]));
    }
}
