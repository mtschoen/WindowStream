using WindowStream.Cli;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class CliServicesTests
{
    [Fact]
    public void Constructor_AssignsAllDependencies()
    {
        IWindowCaptureSource captureSource = new FakeWindowCaptureSource(new List<WindowInformation>());
        ISessionHostLauncher hostLauncher = new FakeSessionHostLauncher();
        var output = new StringWriter();

        var services = new CliServices(captureSource, hostLauncher, output);

        Assert.Same(captureSource, services.CaptureSource);
        Assert.Same(hostLauncher, services.HostLauncher);
        Assert.Same(output, services.Output);
    }

    [Fact]
    public void Constructor_NullCaptureSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CliServices(
            captureSource: null!,
            hostLauncher: new FakeSessionHostLauncher(),
            output: new StringWriter()));
    }

    [Fact]
    public void Constructor_NullHostLauncher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CliServices(
            captureSource: new FakeWindowCaptureSource(new List<WindowInformation>()),
            hostLauncher: null!,
            output: new StringWriter()));
    }

    [Fact]
    public void Constructor_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CliServices(
            captureSource: new FakeWindowCaptureSource(new List<WindowInformation>()),
            hostLauncher: new FakeSessionHostLauncher(),
            output: null!));
    }
}
