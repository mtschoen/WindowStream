using System;
using Xunit;
using WindowStream.Core.Encode;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncoderExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        EncoderException exception = new EncoderException("test error");
        Assert.Equal("test error", exception.Message);
        Assert.Null(exception.ffmpegErrorCode);
    }

    [Fact]
    public void Constructor_WithMessageAndCode_SetsCode()
    {
        EncoderException exception = new EncoderException("test error", -22);
        Assert.Equal("test error", exception.Message);
        Assert.Equal(-22, exception.ffmpegErrorCode);
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_SetsInner()
    {
        InvalidOperationException inner = new InvalidOperationException("inner");
        EncoderException exception = new EncoderException("outer", inner);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Null(exception.ffmpegErrorCode);
    }
}
