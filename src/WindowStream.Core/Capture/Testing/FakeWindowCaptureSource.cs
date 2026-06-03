namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCaptureSource : IWindowCaptureSource
{
    readonly List<WindowInformation> _windows;
    readonly Dictionary<WindowHandle, FakeWindowCapture> _captures = new();

    public FakeWindowCaptureSource(IEnumerable<WindowInformation>? windows)
    {
        _windows = windows?.ToList() ?? new List<WindowInformation>();
    }

    public IEnumerable<WindowInformation> ListWindows() => _windows;

    public FakeWindowCapture? GetCapture(WindowHandle handle) =>
        _captures.TryGetValue(handle, out var capture) ? capture : null;

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!_windows.Exists(window => window.Handle.Equals(handle)))
        {
            throw new WindowGoneException(handle);
        }
        if (_captures.TryGetValue(handle, out var existing))
        {
            return existing;
        }
        var capture = new FakeWindowCapture(handle, options, cancellationToken);
        _captures[handle] = capture;
        return capture;
    }

    public void EnqueueFrame(WindowHandle handle, CapturedFrame frame) =>
        GetOrCreateCapture(handle)._channel.Writer.TryWrite(frame);

    public void CompleteAfterEnqueued(WindowHandle handle) =>
        GetOrCreateCapture(handle)._channel.Writer.TryComplete();

    public void FaultAfterEnqueued(WindowHandle handle, Exception exception) =>
        GetOrCreateCapture(handle)._channel.Writer.TryComplete(exception);

    FakeWindowCapture GetOrCreateCapture(WindowHandle handle)
    {
        if (!_captures.TryGetValue(handle, out var capture))
        {
            capture = new FakeWindowCapture(handle, new CaptureOptions(60, false), CancellationToken.None);
            _captures[handle] = capture;
        }
        return capture;
    }
}
