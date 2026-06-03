#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed unsafe class D3D11VideoProcessorColorConverterTests
{
    const int TextureWidth = 64;
    const int TextureHeight = 64;

    [Fact]
    public void Constructor_Succeeds_With_Valid_Device_And_Dimensions()
    {
        using var manager = new Direct3D11DeviceManager();
        using var converter =
            new D3D11VideoProcessorColorConverter(manager, TextureWidth, TextureHeight);
        // Reaching here without exception is the success criterion.
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var manager = new Direct3D11DeviceManager();
        var converter =
            new D3D11VideoProcessorColorConverter(manager, TextureWidth, TextureHeight);

        converter.Dispose();
        converter.Dispose(); // must not throw
    }

    [Fact]
    public void Convert_AfterDispose_ThrowsObjectDisposedException()
    {
        using var manager = new Direct3D11DeviceManager();
        var converter =
            new D3D11VideoProcessorColorConverter(manager, TextureWidth, TextureHeight);

        converter.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            converter.Convert(1, 2, 0));
    }

    [Fact]
    public void Convert_BgraToNv12_RoundTrip_WithinColorSpaceTolerance()
    {
        using var manager = new Direct3D11DeviceManager();
        using var converter =
            new D3D11VideoProcessorColorConverter(manager, TextureWidth, TextureHeight);

        var device = (ID3D11Device*)manager.NativeDevicePointer;
        var context = (ID3D11DeviceContext*)manager.NativeContextPointer;

        // Build a 64×64 BGRA texture with a known per-quadrant colour pattern.
        var bgraSource = BuildQuadrantPattern(TextureWidth, TextureHeight);

        ID3D11Texture2D* bgraTexture = null;
        ID3D11Texture2D* nv12Texture = null;
        ID3D11Texture2D* stagingTexture = null;

        try
        {
            bgraTexture = CreateBgraTexture(device, TextureWidth, TextureHeight, bgraSource);
            nv12Texture = CreateNv12RenderTexture(device, TextureWidth, TextureHeight);

            converter.Convert((nint)bgraTexture, (nint)nv12Texture, arrayIndex: 0);

            stagingTexture = CreateNv12CpuReadbackTexture(device, TextureWidth, TextureHeight);
            context->CopyResource((ID3D11Resource*)stagingTexture, (ID3D11Resource*)nv12Texture);

            var nv12Bytes = ReadNv12TextureAsBytes(context, stagingTexture, TextureWidth, TextureHeight);
            var roundTripped = DecodeNv12ToBgra(nv12Bytes, TextureWidth, TextureHeight);

            // Tolerance of 12 per channel: GPU VideoProcessor uses BT.709 for NV12 output;
            // the CPU reference uses BT.601 — small per-channel drift is expected.
            AssertBgraWithinTolerance(bgraSource, roundTripped, TextureWidth, TextureHeight, tolerance: 12);
        }
        finally
        {
            if (stagingTexture != null) stagingTexture->Release();
            if (nv12Texture != null) nv12Texture->Release();
            if (bgraTexture != null) bgraTexture->Release();
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    static byte[] BuildQuadrantPattern(int width, int height)
    {
        // Four quadrants, each a distinct colour (BGRA byte order):
        //   top-left:     red   (0, 0, 255, 255)
        //   top-right:    green (0, 255, 0, 255)
        //   bottom-left:  blue  (255, 0, 0, 255)
        //   bottom-right: white (255, 255, 255, 255)
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var rightHalf = x >= width / 2;
                var bottomHalf = y >= height / 2;

                byte blue, green, red;
                if (!rightHalf && !bottomHalf) { blue = 0; green = 0; red = 255; } // red
                else if (rightHalf && !bottomHalf) { blue = 0; green = 255; red = 0; } // green
                else if (!rightHalf) { blue = 255; green = 0; red = 0; } // blue
                else { blue = 255; green = 255; red = 255; } // white

                var offset = (y * width + x) * 4;
                data[offset + 0] = blue;
                data[offset + 1] = green;
                data[offset + 2] = red;
                data[offset + 3] = 255;
            }
        }
        return data;
    }

    static ID3D11Texture2D* CreateBgraTexture(
        ID3D11Device* device,
        int width,
        int height,
        byte[] bgraPixels)
    {
        var description = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            // Mirror the bind flags that WGC uses when creating its capture textures
            // so that CreateVideoProcessorInputView sees the same resource type.
            BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.RenderTarget),
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        fixed (byte* pixelData = bgraPixels)
        {
            var initialData = new SubresourceData
            {
                PSysMem = pixelData,
                SysMemPitch = (uint)(width * 4),
                SysMemSlicePitch = 0,
            };

            ID3D11Texture2D* texture = null;
            var hr = device->CreateTexture2D(in description, in initialData, ref texture);
            if (hr < 0)
            {
                throw new InvalidOperationException(
                    $"CreateTexture2D (BGRA) failed: HRESULT 0x{hr:X8}");
            }
            return texture;
        }
    }

    static ID3D11Texture2D* CreateNv12RenderTexture(ID3D11Device* device, int width, int height)
    {
        var description = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatNV12,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.RenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        ID3D11Texture2D* texture = null;
        var hr = device->CreateTexture2D(in description, null, ref texture);
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"CreateTexture2D (NV12 render target) failed: HRESULT 0x{hr:X8}");
        }
        return texture;
    }

    static ID3D11Texture2D* CreateNv12CpuReadbackTexture(ID3D11Device* device, int width, int height)
    {
        var description = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatNV12,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Staging,
            BindFlags = 0,
            CPUAccessFlags = (uint)CpuAccessFlag.Read,
            MiscFlags = 0,
        };

        ID3D11Texture2D* texture = null;
        var hr = device->CreateTexture2D(in description, null, ref texture);
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"CreateTexture2D (NV12 staging) failed: HRESULT 0x{hr:X8}");
        }
        return texture;
    }

    static byte[] ReadNv12TextureAsBytes(
        ID3D11DeviceContext* context,
        ID3D11Texture2D* stagingTexture,
        int width,
        int height)
    {
        // NV12 layout: Y plane (width×height bytes) followed by interleaved UV plane (width×height/2 bytes).
        var yPlaneSize = width * height;
        var uvPlaneSize = width * height / 2;
        var result = new byte[yPlaneSize + uvPlaneSize];

        MappedSubresource mapped = default;
        var hr = context->Map(
            (ID3D11Resource*)stagingTexture,
            Subresource: 0,
            MapType: Map.Read,
            MapFlags: 0,
            pMappedResource: ref mapped);

        if (hr < 0)
        {
            throw new InvalidOperationException($"Map failed: HRESULT 0x{hr:X8}");
        }

        try
        {
            var source = (byte*)mapped.PData;
            var rowPitch = mapped.RowPitch;

            // Copy Y plane row by row (pitch may be wider than width).
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    (nint)(source + row * rowPitch),
                    result,
                    row * width,
                    width);
            }

            // Copy UV plane row by row (height/2 rows, each row = width bytes interleaved UV pairs).
            var uvPlaneStart = source + height * rowPitch;
            for (var row = 0; row < height / 2; row++)
            {
                Marshal.Copy(
                    (nint)(uvPlaneStart + row * rowPitch),
                    result,
                    yPlaneSize + row * width,
                    width);
            }
        }
        finally
        {
            context->Unmap((ID3D11Resource*)stagingTexture, Subresource: 0);
        }

        return result;
    }

    /// <summary>
    /// CPU BT.601 reference decoding of NV12 → BGRA.
    /// NV12: Y plane at [0, width*height), UV plane at [width*height, end).
    /// </summary>
    static byte[] DecodeNv12ToBgra(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var yPlaneSize = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int yValue = nv12[y * width + x];
                var uvRow = y / 2;
                var uvBase = yPlaneSize + uvRow * width + (x & ~1); // align to even column for chroma subsampling
                int uValue = nv12[uvBase];
                int vValue = nv12[uvBase + 1];

                // BT.601 studio swing (limited range: Y=[16,235], UV=[16,240]).
                var yScaled = yValue - 16.0;
                var uScaled = uValue - 128.0;
                var vScaled = vValue - 128.0;

                var red = Clamp((int)(1.164 * yScaled + 1.596 * vScaled));
                var green = Clamp((int)(1.164 * yScaled - 0.392 * uScaled - 0.813 * vScaled));
                var blue = Clamp((int)(1.164 * yScaled + 2.018 * uScaled));

                var offset = (y * width + x) * 4;
                bgra[offset + 0] = (byte)blue;
                bgra[offset + 1] = (byte)green;
                bgra[offset + 2] = (byte)red;
                bgra[offset + 3] = 255;
            }
        }
        return bgra;
    }

    static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    static void AssertBgraWithinTolerance(
        byte[] expected,
        byte[] actual,
        int width,
        int height,
        int tolerance)
    {
        // Skip a 2-pixel border to avoid VideoProcessor edge-handling artefacts.
        const int border = 2;
        for (var y = border; y < height - border; y++)
        {
            for (var x = border; x < width - border; x++)
            {
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++) // B, G, R (skip A)
                {
                    var diff = Math.Abs(expected[offset + channel] - actual[offset + channel]);
                    Assert.True(diff <= tolerance,
                        $"Pixel ({x},{y}) channel {channel} diff {diff} exceeds tolerance {tolerance}. " +
                        $"Expected {expected[offset + channel]}, got {actual[offset + channel]}.");
                }
            }
        }
    }
}
#endif
