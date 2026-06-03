using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncoderExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        var exception = new EncoderException("test error");
        Assert.Equal("test error", exception.Message);
        Assert.Null(exception.FfmpegErrorCode);
    }

    [Fact]
    public void Constructor_WithMessageAndCode_SetsCode()
    {
        var exception = new EncoderException("test error", -22);
        Assert.Equal("test error", exception.Message);
        Assert.Equal(-22, exception.FfmpegErrorCode);
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_SetsInner()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new EncoderException("outer", inner);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Null(exception.FfmpegErrorCode);
    }
}
