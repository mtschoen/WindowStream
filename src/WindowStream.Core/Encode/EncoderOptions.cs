namespace WindowStream.Core.Encode;

public sealed class EncoderOptions
{
    public int WidthPixels { get; }
    public int HeightPixels { get; }
    public int FramesPerSecond { get; }
    public int BitrateBitsPerSecond { get; }
    public int GroupOfPicturesLength { get; }
    public int SafetyKeyframeIntervalSeconds { get; }

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

        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        FramesPerSecond = framesPerSecond;
        BitrateBitsPerSecond = bitrateBitsPerSecond;
        GroupOfPicturesLength = groupOfPicturesLength;
        SafetyKeyframeIntervalSeconds = safetyKeyframeIntervalSeconds;
    }
}
