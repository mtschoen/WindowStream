namespace WindowStream.Core.Capture.Windows;

public interface IWindowEnumerator
{
    IEnumerable<WindowInformation> EnumerateWindows();
}
