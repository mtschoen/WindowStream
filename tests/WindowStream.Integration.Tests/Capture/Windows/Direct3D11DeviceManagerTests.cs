#if WINDOWS
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed class Direct3D11DeviceManagerTests
{
    private static readonly Guid iidId3D11VideoDevice =
        new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

    [Fact]
    public void CreateDefault_Constructs_With_NonNull_Pointers_And_WinRt_Wrapper()
    {
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();

        Assert.NotEqual(IntPtr.Zero, manager.NativeDevicePointer);
        Assert.NotEqual(IntPtr.Zero, manager.NativeContextPointer);
        Assert.NotNull(manager.WinRtDevice);
    }

    [Fact]
    public void Native_Device_Supports_ID3D11VideoDevice_QueryInterface()
    {
        // Proves the D3D11_CREATE_DEVICE_VIDEO_SUPPORT flag took effect.
        // Required for M3's ID3D11VideoProcessor / VideoProcessorBlt path.
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();

        unsafe
        {
            ID3D11Device* device = (ID3D11Device*)manager.NativeDevicePointer;
            ID3D11VideoDevice* videoDevice = null;
            Guid iid = iidId3D11VideoDevice;
            int hr = device->QueryInterface(ref iid, (void**)&videoDevice);
            try
            {
                Assert.True(hr >= 0, $"QueryInterface(ID3D11VideoDevice) failed: HRESULT 0x{(uint)hr:X8}");
                Assert.True(videoDevice != null);
            }
            finally
            {
                if (videoDevice != null) videoDevice->Release();
            }
        }
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        manager.Dispose();
        manager.Dispose(); // must not throw
    }

    [Fact]
    public void After_Dispose_Property_Access_Throws()
    {
        Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => manager.WinRtDevice);
        Assert.Throws<ObjectDisposedException>(() => manager.NativeDevicePointer);
        Assert.Throws<ObjectDisposedException>(() => manager.NativeContextPointer);
    }
}
#endif
