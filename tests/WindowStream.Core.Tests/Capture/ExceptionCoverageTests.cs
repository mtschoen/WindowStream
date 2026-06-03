using WindowStream.Core.Capture;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class ExceptionCoverageTests
{
    [Fact]
    public void WindowCaptureException_TwoArgConstructor_SetsMessage()
    {
        var inner = new InvalidOperationException("inner");
        var exception =
            new WindowCaptureException("outer", inner);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void WindowGoneException_TwoArgConstructor_SetsHandle()
    {
        var inner = new InvalidOperationException("boom");
        var handle = new WindowHandle(42);
        var exception =
            new WindowGoneException(handle, inner);
        Assert.Equal(handle, exception.Handle);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void WindowGoneException_OneArgConstructor_ExposesHandle()
    {
        var handle = new WindowHandle(7);
        var exception =
            new WindowGoneException(handle);
        Assert.Equal(handle, exception.Handle);
        Assert.Contains("0x7", exception.Message, StringComparison.Ordinal);
    }
}
