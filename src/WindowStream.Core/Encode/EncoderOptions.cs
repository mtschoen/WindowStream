using System;

namespace WindowStream.Core.Encode;

public sealed class EncoderOptions
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int framesPerSecond { get; }
    public int bitrateBitsPerSecond { get; }
    public int groupOfPicturesLength { get; }
    public int safetyKeyframeIntervalSeconds { get; }

    public EncoderOptions(
        int widthPixels,
        int heightPixels,
        int framesPerSecond,
        int bitrateBitsPerSecond,
        int groupOfPicturesLength,
        int safetyKeyframeIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrateBitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupOfPicturesLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(safetyKeyframeIntervalSeconds);

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.framesPerSecond = framesPerSecond;
        this.bitrateBitsPerSecond = bitrateBitsPerSecond;
        this.groupOfPicturesLength = groupOfPicturesLength;
        this.safetyKeyframeIntervalSeconds = safetyKeyframeIntervalSeconds;
    }
}
