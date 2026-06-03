#if WINDOWS
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WindowStream.Core.Encode;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCaptureSource : IWindowCaptureSource
{
    // IGraphicsCaptureItemInterop — GUID from Windows.Graphics.Capture.Interop.h (3628E81B-...)
    // C++ vtable: HRESULT CreateForWindow(HWND window, REFIID riid, void** ppv)
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(
            [In] IntPtr window,
            [In] ref Guid iid,
            out IntPtr result);
    }

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int RoGetActivationFactory(
        IntPtr hstring,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int WindowsDeleteString(IntPtr hstring);

    readonly IWindowEnumerator _enumerator;

    public WgcCaptureSource() : this(new WindowEnumerator(new Win32Api())) { }

    public WgcCaptureSource(IWindowEnumerator enumerator)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    public IEnumerable<WindowInformation> ListWindows() => _enumerator.EnumerateWindows();

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken) =>
        Start(handle, options, sharedDeviceManager: null, sharedFrameTexturePool: null, cancellationToken);

#pragma warning disable CA1822 // CA1822: public capture-source API overload backing IWindowCaptureSource.Start; kept instance for API symmetry
    public IWindowCapture Start(
        WindowHandle handle,
        CaptureOptions options,
        Direct3D11DeviceManager? sharedDeviceManager,
        IFrameTexturePool? sharedFrameTexturePool,
        CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        var item = CreateItemForWindow(new IntPtr(handle.Value), handle);
        var deviceManager = sharedDeviceManager ?? new Direct3D11DeviceManager();
        var ownsDeviceManager = sharedDeviceManager is null;
        try
        {
            return new WgcCapture(handle, options, item, deviceManager, ownsDeviceManager, sharedFrameTexturePool, cancellationToken);
        }
        catch
        {
            if (ownsDeviceManager) deviceManager.Dispose();
            throw;
        }
    }
#pragma warning restore CA1822

    static readonly Guid IidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");

    static readonly Guid IidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    // IInspectable GUID — WinRT objects are requested via IInspectable
    static readonly Guid IidIInspectable = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle, WindowHandle handle)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        _ = WindowsCreateString(classId, (uint)classId.Length, out var hstring);
        try
        {
            // Get activation factory as IUnknown, then QI for IGraphicsCaptureItemInterop
            var iUnknown = IidIUnknown;
            var hr = RoGetActivationFactory(hstring, ref iUnknown, out var factoryPointer);
            if (hr < 0)
            {
                throw new WindowCaptureException("RoGetActivationFactory failed. HRESULT: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
            }

            // QueryInterface for IGraphicsCaptureItemInterop
            unsafe
            {
                var vtable = *(void***)factoryPointer;
                // QueryInterface is slot 0 of IUnknown vtable
                var qi =
                    (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)vtable[0];
                var interopIid = IidInterop;
                IntPtr interopPointer;
                var qiHr = qi(factoryPointer, &interopIid, &interopPointer);
                Marshal.Release(factoryPointer);
                if (qiHr < 0)
                {
                    throw new WindowCaptureException("Failed to obtain IGraphicsCaptureItemInterop via QI. HRESULT: 0x" + qiHr.ToString("X8", CultureInfo.InvariantCulture));
                }

                var interop = (IGraphicsCaptureItemInterop)
                    Marshal.GetObjectForIUnknown(interopPointer);
                Marshal.Release(interopPointer);

                var iid = IidIInspectable;
                try
                {
                    var createHr = interop.CreateForWindow(windowHandle, ref iid, out var itemPointer);
                    if (createHr < 0)
                    {
                        throw new WindowCaptureException("CreateForWindow failed. HRESULT: 0x" + createHr.ToString("X8", CultureInfo.InvariantCulture));
                    }
                    var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
                    Marshal.Release(itemPointer);
                    return item;
                }
                catch (Exception exception) when (exception is not WindowGoneException)
                {
                    throw new WindowGoneException(handle, exception);
                }
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }
}
#endif
