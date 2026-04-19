using System;
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
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset reported = SystemClock.Instance.UtcNow;
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.True(reported >= before && reported <= after);
    }
}
