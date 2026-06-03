using WindowStream.Core.Capture;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameNv12Tests
{
    [Fact]
    public void Constructor_Nv12_RequiresStrideAtLeastWidth()
    {
        // NV12: minimum stride = width (1 byte per pixel for luma plane)
        // buffer must be stride * height * 3/2
        var width = 4;
        var height = 2;
        var stride = 4;
        var buffer = new byte[stride * height * 3 / 2]; // 12 bytes

        var frame = new CapturedFrame(
            widthPixels: width,
            heightPixels: height,
            rowStrideBytes: stride,
            pixelFormat: PixelFormat.Nv12,
            presentationTimestampMicroseconds: 0,
            pixelBuffer: buffer);

        Assert.Equal(PixelFormat.Nv12, frame.PixelFormat);
        Assert.Equal(width, frame.WidthPixels);
    }

    [Fact]
    public void Constructor_Nv12_RejectsStrideSmallerThanWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                widthPixels: 4,
                heightPixels: 2,
                rowStrideBytes: 2,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                pixelBuffer: new byte[24]));
    }

    [Fact]
    public void Constructor_InvalidPixelFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CapturedFrame(
                widthPixels: 1,
                heightPixels: 1,
                rowStrideBytes: 1,
                pixelFormat: (PixelFormat)99,
                presentationTimestampMicroseconds: 0,
                pixelBuffer: new byte[4]));
    }
}
