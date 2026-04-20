using System.IO;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Cli;

public interface ICliServices
{
    IWindowCaptureSource CaptureSource { get; }
    ISessionHostLauncher HostLauncher { get; }
    TextWriter Output { get; }
}
