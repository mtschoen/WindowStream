#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
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

    public CapturedFrame Convert(Direct3D11CaptureFrame frame, long startTicks)
    {
        IDirect3DDxgiInterfaceAccess access =
            frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid id = iidId3D11Texture2D;
        IntPtr texturePointer = access.GetInterface(ref id);
        try
        {
            unsafe
            {
                ID3D11Texture2D* texture = (ID3D11Texture2D*)texturePointer;
                Texture2DDesc description = default;
                texture->GetDesc(ref description);

                ID3D11Device* device = null;
                texture->GetDevice(&device);
                ID3D11DeviceContext* context = null;
                device->GetImmediateContext(&context);

                Texture2DDesc stagingDescription = description;
                stagingDescription.Usage = Usage.Staging;
                stagingDescription.BindFlags = 0;
                stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                stagingDescription.MiscFlags = 0;

                ID3D11Texture2D* staging = null;
                device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref staging);
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);

                MappedSubresource mapped = default;
                context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped);

                int width = (int)description.Width;
                int height = (int)description.Height;
                int stride = (int)mapped.RowPitch;
                byte[] managed = new byte[stride * height];
                Marshal.Copy((IntPtr)mapped.PData, managed, 0, managed.Length);
                context->Unmap((ID3D11Resource*)staging, 0);
                staging->Release();
                context->Release();
                device->Release();

                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                long timestampMicroseconds = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);

                return new CapturedFrame(
                    widthPixels: width,
                    heightPixels: height,
                    rowStrideBytes: stride,
                    pixelFormat: PixelFormat.Bgra32,
                    presentationTimestampMicroseconds: timestampMicroseconds,
                    pixelBuffer: managed);
            }
        }
        finally
        {
            Marshal.Release(texturePointer);
        }
    }
}
#endif
