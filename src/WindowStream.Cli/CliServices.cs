using System.Diagnostics.CodeAnalysis;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;
#if WINDOWS
using Microsoft.Extensions.Logging;
using Serilog;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Hosting;
using WindowStream.Core.Observability;
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
        var serilogLogger = WindowStreamFileLogging.CreateConfiguration().CreateLogger();
        // Not `using`: `serve`/`worker` are long-running commands, and the
        // factory (and the file sink it owns via AddSerilog(dispose: true))
        // must stay open for the life of the process, not just this method.
        // The OS reclaims the log file handle on exit.
#pragma warning disable CA2000
        var loggerFactory = LoggerFactory.Create(builder => builder
            .AddConsole()
            .AddSerilog(serilogLogger, dispose: true));
#pragma warning restore CA2000
        Microsoft.Extensions.Logging.ILogger logger = loggerFactory.CreateLogger<CoordinatorLauncher>();
        var diagnostics = new Diagnostics(logger);
        ISessionHostLauncher hostLauncher = new CoordinatorLauncher(tcpPort, diagnostics);
        return new CliServices(captureSource, hostLauncher, Console.Out);
#else
        throw new PlatformNotSupportedException(
            "windowstream serve requires the Windows target framework. "
            + "Rebuild with -f net8.0-windows10.0.19041.0.");
#endif
    }
}
