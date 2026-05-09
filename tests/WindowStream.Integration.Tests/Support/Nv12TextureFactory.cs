#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WindowStream.Core.Capture.Windows;

namespace WindowStream.Integration.Tests.Support;

internal static class Nv12TextureFactory
{
    /// <summary>
    /// Builds an NV12 D3D11 texture filled with a known per-quadrant pattern,
    /// uploaded once via a CPU-staging texture. Returned pointer is an
    /// <c>ID3D11Texture2D*</c> the caller must release.
    /// </summary>
    internal static unsafe nint CreateQuadrantPatternTexture(
        Direct3D11DeviceManager deviceManager,
        int widthPixels,
        int heightPixels)
    {
        ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;
        ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;

        Texture2DDesc destinationDescription = default;
        destinationDescription.Width = (uint)widthPixels;
        destinationDescription.Height = (uint)heightPixels;
        destinationDescription.MipLevels = 1;
        destinationDescription.ArraySize = 1;
        destinationDescription.Format = Format.FormatNV12;
        destinationDescription.SampleDesc = new SampleDesc(1, 0);
        destinationDescription.Usage = Usage.Default;
        destinationDescription.BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource);
        destinationDescription.CPUAccessFlags = 0;
        destinationDescription.MiscFlags = 0;

        ID3D11Texture2D* destinationTexture = null;
        int hr = device->CreateTexture2D(ref destinationDescription, (SubresourceData*)null, ref destinationTexture);
        if (hr < 0)
        {
            throw new System.Exception($"CreateTexture2D(NV12 dest) failed: 0x{(uint)hr:X8}");
        }

        // Build CPU-side NV12 plane data: top half luma (Y), bottom half chroma (UV interleaved).
        int yPlaneBytes = widthPixels * heightPixels;
        int uvPlaneBytes = widthPixels * heightPixels / 2;
        byte[] nv12Buffer = new byte[yPlaneBytes + uvPlaneBytes];

        // Y plane: 4 quadrant Y values (BT.601 limited-range encodings of red/green/blue/grey).
        for (int row = 0; row < heightPixels; row++)
        {
            for (int col = 0; col < widthPixels; col++)
            {
                bool top = row < heightPixels / 2;
                bool left = col < widthPixels / 2;
                byte yValue = (top, left) switch
                {
                    (true, true)   => 81,  // BT.601 Y for pure red
                    (true, false)  => 145, // green
                    (false, true)  => 41,  // blue
                    (false, false) => 128, // mid grey
                };
                nv12Buffer[row * widthPixels + col] = yValue;
            }
        }

        // UV plane (chroma subsampled 2x2): interleaved U,V.
        for (int chromaRow = 0; chromaRow < heightPixels / 2; chromaRow++)
        {
            for (int chromaCol = 0; chromaCol < widthPixels / 2; chromaCol++)
            {
                bool top = chromaRow < heightPixels / 4;
                bool left = chromaCol < widthPixels / 4;
                (byte uValue, byte vValue) = (top, left) switch
                {
                    (true, true)   => ((byte)90,  (byte)240), // red
                    (true, false)  => ((byte)54,  (byte)34),  // green
                    (false, true)  => ((byte)240, (byte)110), // blue
                    (false, false) => ((byte)128, (byte)128), // grey
                };
                int uvOffset = yPlaneBytes + chromaRow * widthPixels + chromaCol * 2;
                nv12Buffer[uvOffset]     = uValue;
                nv12Buffer[uvOffset + 1] = vValue;
            }
        }

        // Upload via a staging texture.
        Texture2DDesc stagingDescription = destinationDescription;
        stagingDescription.Usage = Usage.Staging;
        stagingDescription.BindFlags = 0;
        stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Write;
        stagingDescription.MiscFlags = 0;

        ID3D11Texture2D* stagingTexture = null;
        hr = device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref stagingTexture);
        if (hr < 0)
        {
            destinationTexture->Release();
            throw new System.Exception($"CreateTexture2D(NV12 staging) failed: 0x{(uint)hr:X8}");
        }
        try
        {
            MappedSubresource mapped = default;
            // Staging textures require Map.Write (not Map.WriteDiscard, which requires D3D11_USAGE_DYNAMIC).
            hr = context->Map((ID3D11Resource*)stagingTexture, 0, Map.Write, 0, ref mapped);
            if (hr < 0)
            {
                throw new System.Exception($"Map(NV12 staging) failed: 0x{(uint)hr:X8}");
            }

            // Y plane rows
            for (int row = 0; row < heightPixels; row++)
            {
                Marshal.Copy(nv12Buffer, row * widthPixels,
                    (System.IntPtr)((byte*)mapped.PData + row * mapped.RowPitch), widthPixels);
            }
            // UV plane rows (start at heightPixels * RowPitch)
            byte* uvDestination = (byte*)mapped.PData + heightPixels * mapped.RowPitch;
            for (int chromaRow = 0; chromaRow < heightPixels / 2; chromaRow++)
            {
                Marshal.Copy(nv12Buffer, yPlaneBytes + chromaRow * widthPixels,
                    (System.IntPtr)(uvDestination + chromaRow * mapped.RowPitch), widthPixels);
            }
            context->Unmap((ID3D11Resource*)stagingTexture, 0);

            context->CopyResource((ID3D11Resource*)destinationTexture, (ID3D11Resource*)stagingTexture);
        }
        finally
        {
            stagingTexture->Release();
        }

        return (nint)destinationTexture;
    }
}
#endif
