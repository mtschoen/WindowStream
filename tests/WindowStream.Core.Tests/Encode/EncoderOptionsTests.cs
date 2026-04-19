using System;
using Xunit;
using WindowStream.Core.Encode;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncoderOptionsTests
{
    [Fact]
    public void Constructor_AcceptsValidValues()
    {
        EncoderOptions options = new EncoderOptions(
            widthPixels: 1920,
            heightPixels: 1080,
            framesPerSecond: 60,
            bitrateBitsPerSecond: 8_000_000,
            groupOfPicturesLength: 60,
            safetyKeyframeIntervalSeconds: 2);

        Assert.Equal(1920, options.widthPixels);
        Assert.Equal(1080, options.heightPixels);
        Assert.Equal(60, options.framesPerSecond);
        Assert.Equal(8_000_000, options.bitrateBitsPerSecond);
        Assert.Equal(60, options.groupOfPicturesLength);
        Assert.Equal(2, options.safetyKeyframeIntervalSeconds);
    }

    [Theory]
    [InlineData(0, 1080, 60, 1, 60, 2)]
    [InlineData(1920, 0, 60, 1, 60, 2)]
    [InlineData(1920, 1080, 0, 1, 60, 2)]
    [InlineData(1920, 1080, 60, 0, 60, 2)]
    [InlineData(1920, 1080, 60, 1, 0, 2)]
    [InlineData(1920, 1080, 60, 1, 60, 0)]
    [InlineData(-1, 1080, 60, 1, 60, 2)]
    public void Constructor_RejectsNonPositive(
        int width, int height, int fps, int bitrate, int gop, int safety)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncoderOptions(width, height, fps, bitrate, gop, safety));
    }
}
