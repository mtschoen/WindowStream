using System.Collections.Generic;
using System.IO;
using WindowStream.Cli;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Testing;

namespace WindowStream.Core.Tests.Cli;

internal sealed class FakeCliServices : ICliServices
{
    public IWindowCaptureSource CaptureSource { get; } = new FakeWindowCaptureSource(new List<WindowInformation>());
    public ISessionHostLauncher HostLauncher { get; } = new FakeSessionHostLauncher();
    public TextWriter Output { get; } = new StringWriter();
}

internal sealed class NamedCliServices : ICliServices
{
    public IWindowCaptureSource CaptureSource { get; }
    public ISessionHostLauncher HostLauncher { get; }
    public TextWriter Output { get; }

    public NamedCliServices(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher, TextWriter output)
    {
        CaptureSource = captureSource;
        HostLauncher = hostLauncher;
        Output = output;
    }
}
