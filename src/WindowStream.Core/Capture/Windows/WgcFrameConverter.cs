#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Windows.Graphics.Capture;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

internal sealed class WgcFrameConverter
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid iidId3D11Texture2D =
        new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>
    /// Delegate called by <see cref="Convert"/> to obtain the next NV12 destination
    /// texture from the producer's ring (M3) or hw_frames_ctx pool (M4+).
    /// </summary>
    /// <param name="width">Source frame width in pixels.</param>
    /// <param name="height">Source frame height in pixels.</param>
    /// <returns>
    /// A tuple of the NV12 texture's COM pointer, the array-slice index within that
    /// texture (always 0 for the M3 hand-rolled ring), and the active colour converter.
    /// </returns>
    internal delegate (nint texturePointer, int arrayIndex, D3D11VideoProcessorColorConverter converter)
        AcquireNv12SlotDelegate(int width, int height);

    private readonly AcquireNv12SlotDelegate acquireNv12Slot;

    internal WgcFrameConverter(AcquireNv12SlotDelegate acquireNv12Slot)
    {
        this.acquireNv12Slot = acquireNv12Slot ?? throw new ArgumentNullException(nameof(acquireNv12Slot));
    }

    public CapturedFrame Convert(Direct3D11CaptureFrame frame, long startTicks)
    {
        IDirect3DDxgiInterfaceAccess access =
            frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid id = iidId3D11Texture2D;
        IntPtr sourceTexturePointer = access.GetInterface(ref id);
        try
        {
            unsafe
            {
                ID3D11Texture2D* sourceTexture = (ID3D11Texture2D*)sourceTexturePointer;
                Texture2DDesc description = default;
                sourceTexture->GetDesc(ref description);

                int width = (int)description.Width;
                int height = (int)description.Height;

                (nint destinationNv12Pointer, int arrayIndex, D3D11VideoProcessorColorConverter converter) =
                    acquireNv12Slot(width, height);

                converter.Convert(sourceTexturePointer, destinationNv12Pointer, arrayIndex);

                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                long timestampMicroseconds = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);
                long wallClockMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                Console.Error.WriteLine(
                    $"[FRAMECOUNT] stage=convert ptsUs={timestampMicroseconds} wallMs={wallClockMilliseconds}");

                return CapturedFrame.FromTexture(
                    widthPixels: width,
                    heightPixels: height,
                    rowStrideBytes: width,
                    pixelFormat: PixelFormat.Nv12,
                    presentationTimestampMicroseconds: timestampMicroseconds,
                    nativeTexturePointer: destinationNv12Pointer,
                    textureArrayIndex: arrayIndex);
            }
        }
        finally
        {
            Marshal.Release(sourceTexturePointer);
        }
    }
}
#endif
