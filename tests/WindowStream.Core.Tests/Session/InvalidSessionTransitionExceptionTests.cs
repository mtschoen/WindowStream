using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class InvalidSessionTransitionExceptionTests
{
    [Fact]
    public void MessageMentionsFromAndToStates()
    {
        InvalidSessionTransitionException exception = new(SessionState.Stopped, SessionState.Capturing);
        Assert.Contains("Stopped", exception.Message);
        Assert.Contains("Capturing", exception.Message);
        Assert.Equal(SessionState.Stopped, exception.FromState);
        Assert.Equal(SessionState.Capturing, exception.ToState);
    }
}
