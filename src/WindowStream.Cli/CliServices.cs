using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Cli;

[ExcludeFromCodeCoverage]
public sealed class CliServices : ICliServices
{
    public IWindowCaptureSource CaptureSource { get; }
    public ISessionHostLauncher HostLauncher { get; }
    public TextWriter Output { get; }

    private CliServices(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher, TextWriter output)
    {
        CaptureSource = captureSource;
        HostLauncher = hostLauncher;
        Output = output;
    }

    public static CliServices CreateDefault()
    {
        throw new PlatformNotSupportedException(
            "windowstream serve is only supported on Windows 10 version 1903 or later.");
    }
}
