#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCaptureSource : IWindowCaptureSource
{
    // IGraphicsCaptureItemInterop — GUID from Windows.Graphics.Capture.Interop.h (3628E81B-...)
    // C++ vtable: HRESULT CreateForWindow(HWND window, REFIID riid, void** ppv)
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(
            [In] IntPtr window,
            [In] ref Guid iid,
            out IntPtr result);
    }

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoGetActivationFactory(
        IntPtr hstring,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private readonly IWindowEnumerator enumerator;

    public WgcCaptureSource() : this(new WindowEnumerator(new Win32Api())) { }

    public WgcCaptureSource(IWindowEnumerator enumerator)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    public IEnumerable<WindowInformation> ListWindows() => enumerator.EnumerateWindows();

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(new IntPtr(handle.value), handle);
        Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        try
        {
            return new WgcCapture(handle, options, item, deviceManager, cancellationToken);
        }
        catch
        {
            deviceManager.Dispose();
            throw;
        }
    }

    private static readonly Guid iidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");
    private static readonly Guid iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    // IInspectable GUID — WinRT objects are requested via IInspectable
    private static readonly Guid iidIInspectable = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle, WindowHandle handle)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(classId, (uint)classId.Length, out IntPtr hstring);
        try
        {
            // Get activation factory as IUnknown, then QI for IGraphicsCaptureItemInterop
            Guid iUnknown = iidIUnknown;
            int hr = RoGetActivationFactory(hstring, ref iUnknown, out IntPtr factoryPointer);
            if (hr < 0)
            {
                throw new WindowCaptureException("RoGetActivationFactory failed. HRESULT: 0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }

            // QueryInterface for IGraphicsCaptureItemInterop
            unsafe
            {
                void** vtable = *(void***)factoryPointer;
                // QueryInterface is slot 0 of IUnknown vtable
                delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> qi =
                    (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)vtable[0];
                Guid interopIid = iidInterop;
                IntPtr interopPointer;
                int qiHr = qi(factoryPointer, &interopIid, &interopPointer);
                Marshal.Release(factoryPointer);
                if (qiHr < 0)
                {
                    throw new WindowCaptureException("Failed to obtain IGraphicsCaptureItemInterop via QI. HRESULT: 0x" + ((uint)qiHr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                IGraphicsCaptureItemInterop interop = (IGraphicsCaptureItemInterop)
                    Marshal.GetObjectForIUnknown(interopPointer);
                Marshal.Release(interopPointer);

                Guid iid = iidIInspectable;
                try
                {
                    int createHr = interop.CreateForWindow(windowHandle, ref iid, out IntPtr itemPointer);
                    if (createHr < 0)
                    {
                        throw new COMException("CreateForWindow failed. HRESULT: 0x" + ((uint)createHr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture), createHr);
                    }
                    GraphicsCaptureItem item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
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
            WindowsDeleteString(hstring);
        }
    }
}
#endif
