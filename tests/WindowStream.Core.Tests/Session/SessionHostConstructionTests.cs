using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Encode;
using WindowStream.Core.Encode.Testing;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionHostConstructionTests
{
    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            null!,
            new FakeWindowCaptureSource(Array.Empty<WindowInformation>()),
            new FakeVideoEncoder(),
            new FakeTcpConnectionAcceptor(TimeProvider.System),
            new FakeUdpVideoSender(),
            TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullCaptureSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            new SessionHostOptions(),
            null!,
            new FakeVideoEncoder(),
            new FakeTcpConnectionAcceptor(TimeProvider.System),
            new FakeUdpVideoSender(),
            TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullVideoEncoder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            new SessionHostOptions(),
            new FakeWindowCaptureSource(Array.Empty<WindowInformation>()),
            null!,
            new FakeTcpConnectionAcceptor(TimeProvider.System),
            new FakeUdpVideoSender(),
            TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullTcpAcceptor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            new SessionHostOptions(),
            new FakeWindowCaptureSource(Array.Empty<WindowInformation>()),
            new FakeVideoEncoder(),
            null!,
            new FakeUdpVideoSender(),
            TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullUdpSender_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            new SessionHostOptions(),
            new FakeWindowCaptureSource(Array.Empty<WindowInformation>()),
            new FakeVideoEncoder(),
            new FakeTcpConnectionAcceptor(TimeProvider.System),
            null!,
            TimeProvider.System));
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionHost(
            new SessionHostOptions(),
            new FakeWindowCaptureSource(Array.Empty<WindowInformation>()),
            new FakeVideoEncoder(),
            new FakeTcpConnectionAcceptor(TimeProvider.System),
            new FakeUdpVideoSender(),
            null!));
    }

    [Fact]
    public async Task UdpPort_And_TcpPort_ReturnExpectedValues_After_Start()
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using SessionHostTestHarness harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        // Access ports via the Host's own properties (exercises SessionHost.UdpPort and SessionHost.TcpPort).
        Assert.Equal(harness.UdpPort, harness.Host.UdpPort);
        Assert.Equal(harness.TcpPort, harness.Host.TcpPort);
    }
}
