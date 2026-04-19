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
        if (widthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        }
        if (heightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightPixels));
        }

        int minimumStride = pixelFormat switch
        {
            PixelFormat.Bgra32 => widthPixels * 4,
            PixelFormat.Nv12 => widthPixels,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
        if (rowStrideBytes < minimumStride)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStrideBytes));
        }
        if (presentationTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        }

        long expectedLength = pixelFormat == PixelFormat.Nv12
            ? (long)rowStrideBytes * heightPixels * 3 / 2
            : (long)rowStrideBytes * heightPixels;
        if (pixelBuffer.Length < expectedLength)
        {
            throw new ArgumentException(
                "pixelBuffer is smaller than widthPixels * heightPixels for the declared stride and format.",
                nameof(pixelBuffer));
        }

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.pixelBuffer = pixelBuffer;
    }
}
