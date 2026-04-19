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
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (bitrateBitsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bitrateBitsPerSecond));
        if (groupOfPicturesLength <= 0) throw new ArgumentOutOfRangeException(nameof(groupOfPicturesLength));
        if (safetyKeyframeIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(safetyKeyframeIntervalSeconds));

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.framesPerSecond = framesPerSecond;
        this.bitrateBitsPerSecond = bitrateBitsPerSecond;
        this.groupOfPicturesLength = groupOfPicturesLength;
        this.safetyKeyframeIntervalSeconds = safetyKeyframeIntervalSeconds;
    }
}
