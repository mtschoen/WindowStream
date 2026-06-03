#if WINDOWS
using System.Globalization;
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int D3D11CreateDevice(
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    static readonly Guid IidIdxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    const uint DriverTypeHardware = 1;
    const uint SdkVersion = 7;
    const uint CreateBgraSupport = 0x20;
    const uint CreateVideoSupport = 0x800;

    bool _disposed;
    nint _nativeDevicePointer;
    nint _nativeContextPointer;
    WinRtDirect3DDevice? _winRtDevice;

    public WinRtDirect3DDevice WinRtDevice =>
        _winRtDevice ?? throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));

    public nint NativeDevicePointer
    {
        get
        {
            ThrowIfDisposed();
            return _nativeDevicePointer;
        }
    }

    public nint NativeContextPointer
    {
        get
        {
            ThrowIfDisposed();
            return _nativeContextPointer;
        }
    }

    public Direct3D11DeviceManager()
    {
        var flags = CreateBgraSupport | CreateVideoSupport;
        var result = D3D11CreateDevice(
            adapter: IntPtr.Zero,
            driverType: DriverTypeHardware,
            software: IntPtr.Zero,
            flags: flags,
            featureLevels: IntPtr.Zero,
            featureLevelCount: 0,
            sdkVersion: SdkVersion,
            device: out var devicePointer,
            featureLevel: out _,
            immediateContext: out var contextPointer);

        if (result < 0)
        {
            throw new WindowCaptureException(
                "Failed to create D3D11 device. HRESULT: 0x"
                + result.ToString("X8", CultureInfo.InvariantCulture));
        }

        try
        {
            unsafe
            {
                var device = (ID3D11Device*)devicePointer;
                IDXGIDevice* dxgiDevice = null;
                var iid = IidIdxgiDevice;
                device->QueryInterface(ref iid, (void**)&dxgiDevice);

                var hr = CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out var graphicsDevice);
                dxgiDevice->Release();

                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "Failed to create IDirect3DDevice wrapper. HRESULT: 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture));
                }

                _winRtDevice = MarshalInterface<WinRtDirect3DDevice>.FromAbi(graphicsDevice);
                Marshal.Release(graphicsDevice);
            }

            _nativeDevicePointer = devicePointer;
            _nativeContextPointer = contextPointer;
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
        if (_disposed) return;
        _disposed = true;

        _winRtDevice?.Dispose();
        _winRtDevice = null;

        if (_nativeContextPointer != 0)
        {
            Marshal.Release(_nativeContextPointer);
            _nativeContextPointer = 0;
        }
        if (_nativeDevicePointer != 0)
        {
            Marshal.Release(_nativeDevicePointer);
            _nativeDevicePointer = 0;
        }
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
#endif
