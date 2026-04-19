using System;

namespace WindowStream.Core.Capture;

public sealed class CapturedFrame
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int rowStrideBytes { get; }
    public PixelFormat pixelFormat { get; }
    public long presentationTimestampMicroseconds { get; }
    public ReadOnlyMemory<byte> pixelBuffer { get; }

    public CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer)
    {
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (rowStrideBytes <= 0) throw new ArgumentOutOfRangeException(nameof(rowStrideBytes));
        if (presentationTimestampMicroseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        if (pixelBuffer.Length == 0)
            throw new ArgumentException("pixelBuffer must not be empty.", nameof(pixelBuffer));

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.pixelBuffer = pixelBuffer;
    }
}
