using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionHostOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveExpectedValues()
    {
        SessionHostOptions options = new SessionHostOptions();
        Assert.Equal(2000, options.HeartbeatIntervalMilliseconds);
        Assert.Equal(6000, options.HeartbeatTimeoutMilliseconds);
        Assert.Equal(1, options.ServerVersion);
        Assert.Equal(1, options.StreamId);
        Assert.Equal("h264", options.Codec);
    }

    [Fact]
    public void CanOverrideAllOptions()
    {
        SessionHostOptions options = new SessionHostOptions(
            HeartbeatIntervalMilliseconds: 500,
            HeartbeatTimeoutMilliseconds: 1500,
            ServerVersion: 2,
            StreamId: 3,
            Codec: "h265");

        Assert.Equal(500, options.HeartbeatIntervalMilliseconds);
        Assert.Equal(1500, options.HeartbeatTimeoutMilliseconds);
        Assert.Equal(2, options.ServerVersion);
        Assert.Equal(3, options.StreamId);
        Assert.Equal("h265", options.Codec);
    }
}
