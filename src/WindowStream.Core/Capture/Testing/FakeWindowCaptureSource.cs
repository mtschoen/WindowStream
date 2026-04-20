using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCaptureSource : IWindowCaptureSource
{
    private readonly List<WindowInformation> windows;
    private readonly Dictionary<WindowHandle, FakeWindowCapture> captures = new();

    public FakeWindowCaptureSource(IEnumerable<WindowInformation> windows)
    {
        this.windows = windows?.ToList() ?? new List<WindowInformation>();
    }

    public IEnumerable<WindowInformation> ListWindows() => windows;

    public FakeWindowCapture? GetCapture(WindowHandle handle) =>
        captures.TryGetValue(handle, out FakeWindowCapture? capture) ? capture : null;

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!windows.Exists(window => window.handle.Equals(handle)))
        {
            throw new WindowGoneException(handle);
        }
        if (captures.TryGetValue(handle, out FakeWindowCapture? existing))
        {
            return existing;
        }
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, cancellationToken);
        captures[handle] = capture;
        return capture;
    }

    public void EnqueueFrame(WindowHandle handle, CapturedFrame frame) =>
        GetOrCreateCapture(handle).channel.Writer.TryWrite(frame);

    public void CompleteAfterEnqueued(WindowHandle handle) =>
        GetOrCreateCapture(handle).channel.Writer.TryComplete();

    public void FaultAfterEnqueued(WindowHandle handle, System.Exception exception) =>
        GetOrCreateCapture(handle).channel.Writer.TryComplete(exception);

    private FakeWindowCapture GetOrCreateCapture(WindowHandle handle)
    {
        if (!captures.TryGetValue(handle, out FakeWindowCapture? capture))
        {
            capture = new FakeWindowCapture(handle, new CaptureOptions(60, false), CancellationToken.None);
            captures[handle] = capture;
        }
        return capture;
    }
}
