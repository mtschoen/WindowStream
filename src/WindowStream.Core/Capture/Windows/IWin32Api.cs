using System;
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public interface IWin32Api
{
    IEnumerable<IntPtr> EnumerateTopLevelWindowHandles();
    bool IsWindowVisible(IntPtr handle);
    string GetWindowTitle(IntPtr handle);
    string GetWindowClassName(IntPtr handle);
    (int processIdentifier, string processName) GetWindowProcess(IntPtr handle);
    (int widthPixels, int heightPixels) GetWindowSize(IntPtr handle);
}
