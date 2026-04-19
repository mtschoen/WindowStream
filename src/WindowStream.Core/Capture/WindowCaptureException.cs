using System;

namespace WindowStream.Core.Capture;

public class WindowCaptureException : Exception
{
    public WindowCaptureException(string message) : base(message) { }
    public WindowCaptureException(string message, Exception innerException) : base(message, innerException) { }
}
