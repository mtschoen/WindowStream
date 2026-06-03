namespace WindowStream.Core.Capture;

public interface IWindowCapture : IAsyncDisposable
{
    IAsyncEnumerable<CapturedFrame> Frames { get; }
    WindowHandle Handle { get; }
    CaptureOptions Options { get; }
}
