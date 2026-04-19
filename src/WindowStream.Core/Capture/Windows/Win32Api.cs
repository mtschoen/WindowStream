#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowStream.Core.Capture.Windows;

public sealed class Win32Api : IWin32Api
{
    private delegate bool EnumWindowsProcedure(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure procedure, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleExtern(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder builder, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder builder, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processIdentifier);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int left, top, right, bottom; }

    public IEnumerable<IntPtr> EnumerateTopLevelWindowHandles()
    {
        List<IntPtr> handles = new List<IntPtr>();
        EnumWindows((windowHandle, _) => { handles.Add(windowHandle); return true; }, IntPtr.Zero);
        return handles;
    }

    public bool IsWindowVisible(IntPtr handle) => IsWindowVisibleExtern(handle);

    public string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        StringBuilder buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public string GetWindowClassName(IntPtr handle)
    {
        StringBuilder buffer = new StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public (int processIdentifier, string processName) GetWindowProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out uint processIdentifier);
        try
        {
            using Process process = Process.GetProcessById((int)processIdentifier);
            return ((int)processIdentifier, process.ProcessName);
        }
        catch
        {
            return ((int)processIdentifier, string.Empty);
        }
    }

    public (int widthPixels, int heightPixels) GetWindowSize(IntPtr handle)
    {
        if (!GetWindowRect(handle, out NativeRect rect))
        {
            return (0, 0);
        }
        return (rect.right - rect.left, rect.bottom - rect.top);
    }
}
#endif
