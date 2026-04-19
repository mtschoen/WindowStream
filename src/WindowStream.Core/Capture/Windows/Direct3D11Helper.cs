#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WinRT;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WindowStream.Core.Capture.Windows;

internal static class Direct3D11Helper
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid iidIdxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    public static WinRtDirect3DDevice CreateDevice()
    {
        const uint driverTypeHardware = 1;
        const uint sdkVersion = 7;
        const uint createBgraSupport = 0x20;

        int result = D3D11CreateDevice(
            IntPtr.Zero,
            driverTypeHardware,
            IntPtr.Zero,
            createBgraSupport,
            IntPtr.Zero,
            0,
            sdkVersion,
            out IntPtr d3dDevice,
            out _,
            out IntPtr immediateContext);

        if (result < 0)
        {
            throw new WindowCaptureException("Failed to create D3D11 device. HRESULT: 0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }

        try
        {
            unsafe
            {
                ID3D11Device* device = (ID3D11Device*)d3dDevice;
                IDXGIDevice* dxgiDevice = null;
                Guid iid = iidIdxgiDevice;
                device->QueryInterface(ref iid, (void**)&dxgiDevice);

                int hr = CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out IntPtr graphicsDevice);
                dxgiDevice->Release();

                if (hr < 0)
                {
                    throw new WindowCaptureException("Failed to create IDirect3DDevice wrapper. HRESULT: 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                WinRtDirect3DDevice wrapped = MarshalInterface<WinRtDirect3DDevice>.FromAbi(graphicsDevice);
                Marshal.Release(graphicsDevice);
                return wrapped;
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
            Marshal.Release(immediateContext);
        }
    }
}
#endif
