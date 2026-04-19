using System;
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public sealed class WindowEnumerator : IWindowEnumerator
{
    private readonly IWin32Api win32Api;

    public WindowEnumerator(IWin32Api win32Api)
    {
        this.win32Api = win32Api ?? throw new ArgumentNullException(nameof(win32Api));
    }

    public IEnumerable<WindowInformation> EnumerateWindows()
    {
        foreach (IntPtr handle in win32Api.EnumerateTopLevelWindowHandles())
        {
            bool visible = win32Api.IsWindowVisible(handle);
            string title = win32Api.GetWindowTitle(handle);
            string className = win32Api.GetWindowClassName(handle);
            (int widthPixels, int heightPixels) size = win32Api.GetWindowSize(handle);

            if (!WindowEnumerationFilters.PassesFilters(
                visible, title, className, size.widthPixels, size.heightPixels))
            {
                continue;
            }
            (int processIdentifier, string processName) process = win32Api.GetWindowProcess(handle);
            yield return new WindowInformation(
                new WindowHandle(handle.ToInt64()),
                title,
                process.processName,
                size.widthPixels,
                size.heightPixels);
        }
    }
}
