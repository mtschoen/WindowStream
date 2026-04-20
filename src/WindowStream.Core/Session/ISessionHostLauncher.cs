using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Session;

public interface ISessionHostLauncher
{
    Task LaunchAsync(WindowHandle handle, CancellationToken cancellationToken);
}
