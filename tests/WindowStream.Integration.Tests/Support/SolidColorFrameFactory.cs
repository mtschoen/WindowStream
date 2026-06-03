#if WINDOWS
using WindowStream.Core.Capture;

namespace WindowStream.Integration.Tests.Support;

/// <summary>
/// Creates synthetic <see cref="CapturedFrame"/> instances filled with a solid BGRA colour.
/// Used by NVENC smoke tests to supply a valid frame without a live window capture.
/// </summary>
static class SolidColorFrameFactory
{
    /// <summary>
    /// Creates a <see cref="CapturedFrame"/> in <see cref="PixelFormat.Bgra32"/> format
    /// filled uniformly with the specified colour components.
    /// </summary>
    internal static CapturedFrame CreateSolidColorBgra(
        int widthPixels,
        int heightPixels,
        byte red,
        byte green,
        byte blue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);

        var rowStrideBytes = widthPixels * 4;
        var pixelBuffer = new byte[rowStrideBytes * heightPixels];
        for (var pixelIndex = 0; pixelIndex < widthPixels * heightPixels; pixelIndex++)
        {
            var offset = pixelIndex * 4;
            pixelBuffer[offset + 0] = blue;
            pixelBuffer[offset + 1] = green;
            pixelBuffer[offset + 2] = red;
            pixelBuffer[offset + 3] = 255; // fully opaque
        }

        return new CapturedFrame(
            widthPixels: widthPixels,
            heightPixels: heightPixels,
            rowStrideBytes: rowStrideBytes,
            pixelFormat: PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 0,
            pixelBuffer: pixelBuffer);
    }
}
#endif
