using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;
#if WINDOWS
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Hosting;
#endif

namespace WindowStream.Cli;

public sealed class CliServices : ICliServices
{
    public IWindowCaptureSource CaptureSource { get; }
    public ISessionHostLauncher HostLauncher { get; }
    public TextWriter Output { get; }

    public CliServices(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher, TextWriter output)
    {
        CaptureSource = captureSource ?? throw new ArgumentNullException(nameof(captureSource));
        HostLauncher = hostLauncher ?? throw new ArgumentNullException(nameof(hostLauncher));
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// Real-hardware wiring: WGC window capture and the v2 coordinator (worker
    /// supervisor + control server + load shedder + UDP fragmenter) bound to
    /// all interfaces so LAN viewers can connect. Only available on the
    /// Windows target framework.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public static CliServices CreateDefault(int tcpPort = 0)
    {
#if WINDOWS
        IWindowCaptureSource captureSource = new WgcCaptureSource();
        ISessionHostLauncher hostLauncher = new CoordinatorLauncher(tcpPort, Console.Out);
        return new CliServices(captureSource, hostLauncher, Console.Out);
#else
        throw new PlatformNotSupportedException(
            "windowstream serve requires the Windows target framework. "
            + "Rebuild with -f net8.0-windows10.0.19041.0.");
#endif
    }
}
