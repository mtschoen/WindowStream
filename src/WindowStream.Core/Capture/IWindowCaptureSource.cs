namespace WindowStream.Core.Capture;

public interface IWindowCaptureSource
{
    IEnumerable<WindowInformation> ListWindows();
    IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken);
}
