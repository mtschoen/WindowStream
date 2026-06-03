namespace WindowStream.Core.Capture;

public sealed class WindowGoneException : WindowCaptureException
{
    public WindowHandle Handle { get; }

    public WindowGoneException(WindowHandle handle)
        : base("Captured window no longer exists: " + handle)
    {
        Handle = handle;
    }

    public WindowGoneException(WindowHandle handle, Exception innerException)
        : base("Captured window no longer exists: " + handle, innerException)
    {
        Handle = handle;
    }
}
