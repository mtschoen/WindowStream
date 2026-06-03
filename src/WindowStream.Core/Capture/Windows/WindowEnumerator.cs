namespace WindowStream.Core.Capture.Windows;

public sealed class WindowEnumerator : IWindowEnumerator
{
    readonly IWin32Api _win32Api;

    public WindowEnumerator(IWin32Api win32Api)
    {
        _win32Api = win32Api ?? throw new ArgumentNullException(nameof(win32Api));
    }

    public IEnumerable<WindowInformation> EnumerateWindows()
    {
        foreach (var handle in _win32Api.EnumerateTopLevelWindowHandles())
        {
            var visible = _win32Api.IsWindowVisible(handle);
            var title = _win32Api.GetWindowTitle(handle);
            var className = _win32Api.GetWindowClassName(handle);
            var size = _win32Api.GetWindowSize(handle);

            if (!WindowEnumerationFilters.PassesFilters(
                visible, title, className, size.widthPixels, size.heightPixels))
            {
                continue;
            }
            var process = _win32Api.GetWindowProcess(handle);
            yield return new WindowInformation(
                new WindowHandle(handle.ToInt64()),
                title,
                process.processName,
                size.widthPixels,
                size.heightPixels);
        }
    }
}
