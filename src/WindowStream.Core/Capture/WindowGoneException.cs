using System;

namespace WindowStream.Core.Capture;

public sealed class WindowGoneException : WindowCaptureException
{
    public WindowHandle handle { get; }

    public WindowGoneException(WindowHandle handle)
        : base("Captured window no longer exists: " + handle)
    {
        this.handle = handle;
    }

    public WindowGoneException(WindowHandle handle, Exception innerException)
        : base("Captured window no longer exists: " + handle, innerException)
    {
        this.handle = handle;
    }
}
