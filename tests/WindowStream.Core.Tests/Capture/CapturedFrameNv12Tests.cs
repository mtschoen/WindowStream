using System;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameNv12Tests
{
    [Fact]
    public void Constructor_Nv12_RequiresStrideAtLeastWidth()
    {
        // NV12: minimum stride = width (1 byte per pixel for luma plane)
        // buffer must be stride * height * 3/2
        int width = 4;
        int height = 2;
        int stride = 4;
        byte[] buffer = new byte[stride * height * 3 / 2]; // 12 bytes

        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            widthPixels: width,
            heightPixels: height,
            rowStrideBytes: stride,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Nv12,
            presentationTimestampMicroseconds: 0,
            pixelBuffer: buffer);

        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Nv12, frame.pixelFormat);
        Assert.Equal(width, frame.widthPixels);
    }

    [Fact]
    public void Constructor_Nv12_RejectsStrideSmallerThanWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                widthPixels: 4,
                heightPixels: 2,
                rowStrideBytes: 2,
                pixelFormat: WindowStream.Core.Capture.PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                pixelBuffer: new byte[24]));
    }

    [Fact]
    public void Constructor_InvalidPixelFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                widthPixels: 1,
                heightPixels: 1,
                rowStrideBytes: 1,
                pixelFormat: (WindowStream.Core.Capture.PixelFormat)99,
                presentationTimestampMicroseconds: 0,
                pixelBuffer: new byte[4]));
    }
}
