using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class SystemClockTests
{
    [Fact]
    public void InstanceIsSingleton()
    {
        Assert.Same(SystemClock.Instance, SystemClock.Instance);
    }

    [Fact]
    public void UtcNowIsCloseToSystemTime()
    {
        var before = DateTimeOffset.UtcNow;
        var reported = SystemClock.Instance.UtcNow;
        var after = DateTimeOffset.UtcNow;
        Assert.True(reported >= before && reported <= after);
    }
}
