#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WinRT;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Owns a single <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c> created
/// with BGRA + video support flags, and the matching WinRT
/// <see cref="WinRtDirect3DDevice"/> wrapper. Designed to be shared across
/// the capture pipeline (WGC consumes the WinRT wrapper; the M3 video
/// processor and the M4 FFmpeg hwaccel device context consume the raw
/// pointers). M1 lifetime is per-capture; M4 hoists this to per-worker
/// scope so the encoder and capture share a single device.
/// </summary>
public sealed class Direct3D11DeviceManager : IDisposable
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

    private const uint DriverTypeHardware = 1;
    private const uint SdkVersion = 7;
    private const uint CreateBgraSupport = 0x20;
    private const uint CreateVideoSupport = 0x800;

    private bool disposed;
    private nint nativeDevicePointer;
    private nint nativeContextPointer;
    private WinRtDirect3DDevice? winRtDevice;

    public WinRtDirect3DDevice WinRtDevice =>
        winRtDevice ?? throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));

    public nint NativeDevicePointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeDevicePointer;
        }
    }

    public nint NativeContextPointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeContextPointer;
        }
    }

    public Direct3D11DeviceManager()
    {
        uint flags = CreateBgraSupport | CreateVideoSupport;
        int result = D3D11CreateDevice(
            adapter: IntPtr.Zero,
            driverType: DriverTypeHardware,
            software: IntPtr.Zero,
            flags: flags,
            featureLevels: IntPtr.Zero,
            featureLevelCount: 0,
            sdkVersion: SdkVersion,
            device: out IntPtr devicePointer,
            featureLevel: out _,
            immediateContext: out IntPtr contextPointer);

        if (result < 0)
        {
            throw new WindowCaptureException(
                "Failed to create D3D11 device. HRESULT: 0x"
                + ((uint)result).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }

        try
        {
            unsafe
            {
                ID3D11Device* device = (ID3D11Device*)devicePointer;
                IDXGIDevice* dxgiDevice = null;
                Guid iid = iidIdxgiDevice;
                device->QueryInterface(ref iid, (void**)&dxgiDevice);

                int hr = CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out IntPtr graphicsDevice);
                dxgiDevice->Release();

                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "Failed to create IDirect3DDevice wrapper. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                winRtDevice = MarshalInterface<WinRtDirect3DDevice>.FromAbi(graphicsDevice);
                Marshal.Release(graphicsDevice);
            }

            nativeDevicePointer = devicePointer;
            nativeContextPointer = contextPointer;
        }
        catch
        {
            Marshal.Release(devicePointer);
            Marshal.Release(contextPointer);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        winRtDevice = null;

        if (nativeContextPointer != 0)
        {
            Marshal.Release(nativeContextPointer);
            nativeContextPointer = 0;
        }
        if (nativeDevicePointer != 0)
        {
            Marshal.Release(nativeDevicePointer);
            nativeDevicePointer = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));
        }
    }
}
#endif
