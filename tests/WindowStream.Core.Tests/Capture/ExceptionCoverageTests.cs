using System;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class ExceptionCoverageTests
{
    [Fact]
    public void WindowCaptureException_TwoArgConstructor_SetsMessage()
    {
        InvalidOperationException inner = new InvalidOperationException("inner");
        WindowStream.Core.Capture.WindowCaptureException exception =
            new WindowStream.Core.Capture.WindowCaptureException("outer", inner);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void WindowGoneException_TwoArgConstructor_SetsHandle()
    {
        InvalidOperationException inner = new InvalidOperationException("boom");
        WindowStream.Core.Capture.WindowHandle handle = new WindowStream.Core.Capture.WindowHandle(42);
        WindowStream.Core.Capture.WindowGoneException exception =
            new WindowStream.Core.Capture.WindowGoneException(handle, inner);
        Assert.Equal(handle, exception.handle);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void WindowGoneException_OneArgConstructor_ExposesHandle()
    {
        WindowStream.Core.Capture.WindowHandle handle = new WindowStream.Core.Capture.WindowHandle(7);
        WindowStream.Core.Capture.WindowGoneException exception =
            new WindowStream.Core.Capture.WindowGoneException(handle);
        Assert.Equal(handle, exception.handle);
        Assert.Contains("0x7", exception.Message);
    }
}
