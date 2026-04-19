using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class FrameTooLargeExceptionTests
{
    [Fact]
    public void ExceptionExposesActualAndMaximumLength()
    {
        FrameTooLargeException exception = new(actualLength: 1234, maximumLength: 100);
        Assert.Equal(1234, exception.ActualLength);
        Assert.Equal(100, exception.MaximumLength);
    }

    [Fact]
    public void MessageDescribesLengths()
    {
        FrameTooLargeException exception = new(actualLength: 999, maximumLength: 500);
        Assert.Contains("999", exception.Message);
        Assert.Contains("500", exception.Message);
    }
}
