using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class MalformedMessageExceptionTests
{
    [Fact]
    public void SingleArgumentConstructorSetsMessage()
    {
        MalformedMessageException exception = new MalformedMessageException("test error");
        Assert.Equal("test error", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void TwoArgumentConstructorSetsMessageAndInnerException()
    {
        System.InvalidOperationException inner = new System.InvalidOperationException("inner");
        MalformedMessageException exception = new MalformedMessageException("outer", inner);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }
}
